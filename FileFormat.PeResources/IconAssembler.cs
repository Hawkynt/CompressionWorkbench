#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.PeResources;

/// <summary>
/// Reassembles a Win32 <c>RT_GROUP_ICON</c> (or <c>RT_GROUP_CURSOR</c>) resource with its
/// child <c>RT_ICON</c>/<c>RT_CURSOR</c> entries into a single self-contained <c>.ico</c> /
/// <c>.cur</c> file. The on-disk ICO layout uses 16-byte directory entries that reference
/// each image by file offset; the in-PE layout uses 14-byte entries that reference each
/// image by resource id. This class performs the cross-walk and fixes offsets.
/// </summary>
internal static class IconAssembler {
  private const int IconDirHeaderSize = 6;
  private const int InPeDirEntrySize = 14;
  private const int OnDiskDirEntrySize = 16;

  /// <summary>
  /// Builds an <c>.ico</c> file from a <c>RT_GROUP_ICON</c> payload plus a lookup of
  /// numeric id → raw <c>RT_ICON</c> bytes. Returns <c>null</c> if any referenced
  /// icon id is missing from <paramref name="iconsById"/>.
  /// </summary>
  public static byte[]? Assemble(byte[] groupPayload, IReadOnlyDictionary<ushort, byte[]> iconsById) {
    if (groupPayload.Length < IconDirHeaderSize) return null;
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(groupPayload.AsSpan(0));
    var imageType = BinaryPrimitives.ReadUInt16LittleEndian(groupPayload.AsSpan(2));
    var count = BinaryPrimitives.ReadUInt16LittleEndian(groupPayload.AsSpan(4));
    if (reserved != 0 || imageType is not (1 or 2))
      return null;
    if (groupPayload.Length < IconDirHeaderSize + count * InPeDirEntrySize)
      return null;

    var images = new byte[count][];
    for (var i = 0; i < count; i++) {
      var src = IconDirHeaderSize + i * InPeDirEntrySize;
      // Last 2 bytes of the in-PE entry hold the resource id (u16).
      var rtIconId = BinaryPrimitives.ReadUInt16LittleEndian(groupPayload.AsSpan(src + 12));
      if (!iconsById.TryGetValue(rtIconId, out var blob)) return null;
      images[i] = blob;
    }

    var dirSize = IconDirHeaderSize + count * OnDiskDirEntrySize;
    var dataSize = 0;
    foreach (var img in images) dataSize += img.Length;
    var output = new byte[dirSize + dataSize];
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), imageType);
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), count);

    var imageOffset = dirSize;
    for (var i = 0; i < count; i++) {
      var srcEntry = IconDirHeaderSize + i * InPeDirEntrySize;
      var dstEntry = IconDirHeaderSize + i * OnDiskDirEntrySize;
      // Copy the 12 byte-identical fields (width..bytesInRes).
      groupPayload.AsSpan(srcEntry, 12).CopyTo(output.AsSpan(dstEntry));
      // Replace the 2-byte id with a 4-byte file offset.
      BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dstEntry + 12), (uint)imageOffset);

      images[i].AsSpan().CopyTo(output.AsSpan(imageOffset));
      imageOffset += images[i].Length;
    }

    return output;
  }
}
