#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.RomFs;

/// <summary>
/// Walks a Linux ROMFS (romfs v1) image and yields its actual on-disk byte
/// layout. ROMFS is a packed, read-only image: the superblock is followed by a
/// chain of file records, each consisting of a 16-byte header, a
/// null-terminated 16-byte-aligned name, and (for regular files) the data
/// padded to a 16-byte boundary.
/// <para>
/// Every header+name region — including the "." and ".." records that thread a
/// directory's child chain — is emitted as
/// <see cref="DefragBlockKind.MetadataReserved"/> so a free-space wiper never
/// mistakes live metadata for a gap. File data is emitted as
/// <see cref="DefragBlockKind.Used"/>. The 16-byte alignment padding after each
/// file's data and any trailing slack are left uncovered, so the caller treats
/// them as <see cref="DefragBlockKind.Free"/>.
/// </para>
/// </summary>
public static class RomFsExtentMap {

  private static readonly byte[] Magic = "-rom1fs-"u8.ToArray();

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < 16) yield break;

    for (var i = 0; i < Magic.Length; i++)
      if (data[i] != Magic[i]) yield break;

    // Superblock: 16-byte fixed header + null-terminated, 16-byte-aligned name.
    var nameStart = 16;
    var nameEnd = nameStart;
    while (nameEnd < data.Length && data[nameEnd] != 0) nameEnd++;
    var sbLen = nameStart + Align16(nameEnd - nameStart + 1);
    if (sbLen > data.Length) sbLen = data.Length;
    yield return new DefragBlockInfo(0, sbLen, DefragBlockKind.MetadataReserved, "superblock");

    // Walk every directory chain reachable from the root, emitting each record's
    // header+name region as metadata and each file's data as Used.
    var visited = new HashSet<long>();
    foreach (var block in WalkChain(data, sbLen, visited))
      yield return block;
  }

  private static IEnumerable<DefragBlockInfo> WalkChain(byte[] data, long firstOffset, HashSet<long> visited) {
    var offset = firstOffset;
    while (offset != 0 && offset + 16 <= data.Length) {
      if (!visited.Add(offset)) break;

      var nextAndType = ReadUInt32BE(data, offset);
      var specInfo = ReadUInt32BE(data, offset + 4);
      var size = (int)ReadUInt32BE(data, offset + 8);

      var next = (long)(nextAndType & 0xFFFFFFF0u);
      var type = (int)(nextAndType & 0x0F);

      var nameOffset = offset + 16;
      var nameEnd = nameOffset;
      while (nameEnd < data.Length && data[nameEnd] != 0) nameEnd++;
      var nameFieldLen = Align16((int)(nameEnd - nameOffset + 1));
      var dataOffset = nameOffset + nameFieldLen;

      // Header + name: live metadata, never free.
      var headerLen = dataOffset - offset;
      if (headerLen > 0 && offset + headerLen <= data.Length)
        yield return new DefragBlockInfo(offset, headerLen, DefragBlockKind.MetadataReserved, "romfs record");

      if (type == 2 && size > 0 && dataOffset + size <= data.Length) {
        // Regular file data.
        yield return new DefragBlockInfo(dataOffset, size, DefragBlockKind.Used);
      } else if (type == 1 && specInfo != 0 && specInfo < data.Length) {
        // Directory: recurse into its first child record.
        foreach (var block in WalkChain(data, specInfo, visited))
          yield return block;
      }

      if (next == 0) break;
      offset = next;
    }
  }

  private static uint ReadUInt32BE(byte[] data, long offset)
    => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)offset, 4));

  private static int Align16(int len) => (len + 15) & ~15;
}
