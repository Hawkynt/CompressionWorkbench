#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.T64;

/// <summary>
/// In-place T64 block mover. Moves data extents within a T64 tape image and
/// patches the directory entry's data-offset field so the file remains reachable.
/// </summary>
public sealed class T64BlockMover : IFilesystemBlockMover {

  private const int HeaderSize = 64;
  private const int EntrySize = 32;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;
    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      var src = srcOffset;
      var dst = dstOffset;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        image.Position = src;
        image.ReadExactly(buffer, 0, chunk);
        image.Position = dst;
        image.Write(buffer, 0, chunk);
        src += chunk;
        dst += chunk;
        remaining -= chunk;
      }
      if (zeroSource) {
        Array.Clear(buffer, 0, buffer.Length);
        remaining = length;
        src = srcOffset;
        while (remaining > 0) {
          var chunk = (int)Math.Min(remaining, buffer.Length);
          image.Position = src;
          image.Write(buffer, 0, chunk);
          src += chunk;
          remaining -= chunk;
        }
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    image.Position = 34;
    Span<byte> hdrBuf = stackalloc byte[2];
    image.ReadExactly(hdrBuf);
    var maxEntries = BinaryPrimitives.ReadUInt16LittleEndian(hdrBuf);

    for (var i = 0; i < maxEntries; i++) {
      var slotOff = HeaderSize + i * EntrySize;
      var entry = new byte[EntrySize];
      image.Position = slotOff;
      image.ReadExactly(entry);

      if (entry[0] == 0) continue;

      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(8));
      if (dataOffset != (uint)oldOffset) continue;

      // Optionally match by name.
      if (!string.Equals(fileName, "*", StringComparison.Ordinal)) {
        var entryName = Encoding.ASCII.GetString(entry, 16, 16).TrimEnd('\0', ' ');
        if (!entryName.Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
      }

      // Patch data offset.
      BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), (uint)newOffset);
      image.Position = slotOff;
      image.Write(entry);
      break;
    }
  }
}
