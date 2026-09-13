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
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("T64: block moves require a readable, writable, seekable stream.", nameof(image));
    if (srcOffset < 0) throw new ArgumentOutOfRangeException(nameof(srcOffset));
    if (dstOffset < 0) throw new ArgumentOutOfRangeException(nameof(dstOffset));
    if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
    if (length == 0 || srcOffset == dstOffset) return;

    var srcEnd = checked(srcOffset + length);
    var dstEnd = checked(dstOffset + length);
    if (srcEnd > image.Length)
      throw new ArgumentOutOfRangeException(nameof(length), "T64: source extent extends beyond EOF.");
    if (dstEnd > image.Length)
      image.SetLength(dstEnd);

    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      // memmove semantics: a forward-overlapping destination must be copied
      // high-to-low or the first write destroys bytes we have not read yet.
      if (dstOffset > srcOffset && dstOffset < srcEnd)
        CopyBackward(image, srcOffset, dstOffset, length, buffer);
      else
        CopyForward(image, srcOffset, dstOffset, length, buffer);

      if (!zeroSource) return;

      Array.Clear(buffer, 0, buffer.Length);
      var overlapStart = Math.Max(srcOffset, dstOffset);
      var overlapEnd = Math.Min(srcEnd, dstEnd);
      if (overlapStart >= overlapEnd) {
        ZeroRange(image, srcOffset, length, buffer);
        return;
      }

      // Never zero the intersection: those bytes are now part of the moved
      // destination. Scrub only the portions exclusively owned by the old run.
      ZeroRange(image, srcOffset, overlapStart - srcOffset, buffer);
      ZeroRange(image, overlapEnd, srcEnd - overlapEnd, buffer);
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("T64: allocation updates require a readable, writable, seekable stream.", nameof(image));
    if (oldOffset < 0 || oldOffset > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(oldOffset));
    if (newOffset < 0 || newOffset > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(newOffset));

    image.Position = 34;
    Span<byte> hdrBuf = stackalloc byte[2];
    image.ReadExactly(hdrBuf);
    var maxEntries = BinaryPrimitives.ReadUInt16LittleEndian(hdrBuf);
    if ((long)HeaderSize + maxEntries * EntrySize > image.Length)
      throw new InvalidDataException("T64: directory extends beyond end of image.");

    Span<byte> entry = stackalloc byte[EntrySize];
    for (var i = 0; i < maxEntries; i++) {
      var slotOff = HeaderSize + i * EntrySize;
      image.Position = slotOff;
      image.ReadExactly(entry);

      if (entry[0] == 0) continue;

      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
      if (dataOffset != (uint)oldOffset) continue;

      if (!string.Equals(fileName, "*", StringComparison.Ordinal)) {
        var entryName = Encoding.ASCII.GetString(entry[16..32]).TrimEnd('\0', ' ');
        if (!entryName.Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
      }

      BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)newOffset);
      image.Position = slotOff;
      image.Write(entry);
      return;
    }

    throw new InvalidDataException($"T64: no directory entry points at payload offset {oldOffset}.");
  }

  private static void CopyForward(Stream image, long src, long dst, long length, byte[] buffer) {
    var cursor = 0L;
    while (cursor < length) {
      var chunk = (int)Math.Min(length - cursor, buffer.Length);
      image.Position = src + cursor;
      image.ReadExactly(buffer, 0, chunk);
      image.Position = dst + cursor;
      image.Write(buffer, 0, chunk);
      cursor += chunk;
    }
  }

  private static void CopyBackward(Stream image, long src, long dst, long length, byte[] buffer) {
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(remaining, buffer.Length);
      var offset = remaining - chunk;
      image.Position = src + offset;
      image.ReadExactly(buffer, 0, chunk);
      image.Position = dst + offset;
      image.Write(buffer, 0, chunk);
      remaining = offset;
    }
  }

  private static void ZeroRange(Stream image, long offset, long length, byte[] zeroBuffer) {
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(remaining, zeroBuffer.Length);
      image.Position = offset;
      image.Write(zeroBuffer, 0, chunk);
      offset += chunk;
      remaining -= chunk;
    }
  }
}
