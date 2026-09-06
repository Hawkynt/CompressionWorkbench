#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Cso;

/// <summary>
/// In-place block-level mutator for PSP CSO v1 images. Lets callers replace
/// individual decompressed blocks without rewriting the whole container.
///
/// <para>Semantics for <see cref="WriteBlock"/>:
/// <list type="bullet">
///   <item>If the new compressed payload fits inside the old block's on-disk
///         slot, the payload is written at the same offset and trailing slack
///         is zero-padded — the index table and every other block's bytes are
///         unchanged.</item>
///   <item>Otherwise the payload is appended at the current end of stream and
///         the block's index entry is updated to point at the new location.
///         The old in-place bytes become orphaned (defrag-recoverable).</item>
/// </list>
/// </para>
///
/// <para>CSO v2 / ZSO (LZ4) are out of scope; only the v1 header layout with
/// align=0 is supported. The modifier refuses to operate on streams whose
/// header reports a different version or a non-zero align (because the
/// offset-shift semantics would silently misplace the new block).</para>
/// </summary>
public static class CsoInPlaceModifier {

  /// <summary>
  /// Replaces block <paramref name="blockIndex"/>'s content with
  /// <paramref name="newUncompressedData"/> (which must be exactly
  /// <c>block_size</c> bytes long, matching the container's geometry). The
  /// payload is DEFLATE-compressed; if the result is smaller than the slab
  /// it's written compressed, otherwise stored uncompressed (with the index
  /// entry's high bit set).
  /// </summary>
  /// <param name="image">Seekable read/write stream over the CSO image.</param>
  /// <param name="blockIndex">0-based block index. Must be in
  /// <c>[0, block_count)</c>.</param>
  /// <param name="newUncompressedData">Exactly <c>block_size</c> bytes.</param>
  public static void WriteBlock(Stream image, int blockIndex, ReadOnlySpan<byte> newUncompressedData) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek)
      throw new InvalidOperationException("CSO in-place modify requires a seekable stream.");
    if (!image.CanWrite)
      throw new InvalidOperationException("CSO in-place modify requires a writable stream.");

    var header = ReadHeader(image);
    if (blockIndex < 0 || blockIndex >= header.BlockCount)
      throw new ArgumentOutOfRangeException(nameof(blockIndex),
        $"CSO block index {blockIndex} outside [0, {header.BlockCount}).");
    if (newUncompressedData.Length != header.BlockSize)
      throw new ArgumentException(
        $"New block payload must be exactly block_size ({header.BlockSize}) bytes; got {newUncompressedData.Length}.",
        nameof(newUncompressedData));

    var deflated = CsoWriter.Deflate(newUncompressedData);
    byte[] payload;
    bool storedUncompressed;
    if (deflated.Length < header.BlockSize) {
      payload = deflated;
      storedUncompressed = false;
    } else {
      payload = newUncompressedData.ToArray();
      storedUncompressed = true;
    }

    var oldRaw = header.IndexRaw[blockIndex];
    var oldNextRaw = header.IndexRaw[blockIndex + 1];
    var oldOffset = (long)(oldRaw & CsoWriter.IndexOffsetMask) << header.Align;
    var oldEndOffset = (long)(oldNextRaw & CsoWriter.IndexOffsetMask) << header.Align;
    var oldSize = oldEndOffset - oldOffset;

    if (payload.Length <= oldSize) {
      image.Position = oldOffset;
      image.Write(payload, 0, payload.Length);
      var slack = (int)(oldSize - payload.Length);
      if (slack > 0)
        image.Write(new byte[slack], 0, slack);

      var newRaw = (uint)(oldRaw & CsoWriter.IndexOffsetMask);
      if (storedUncompressed)
        newRaw |= CsoWriter.IndexUncompressedFlag;
      WriteIndexEntry(image, blockIndex, newRaw);
    } else {
      var appendOffset = image.Length;
      if (appendOffset > CsoWriter.IndexOffsetMask)
        throw new InvalidOperationException(
          "CSO image would exceed 2 GiB after append — would overflow a 31-bit offset.");
      image.Position = appendOffset;
      image.Write(payload, 0, payload.Length);

      var newSentinelRaw = (uint)image.Length;
      _ = newSentinelRaw;

      var newRaw = (uint)appendOffset;
      if (storedUncompressed)
        newRaw |= CsoWriter.IndexUncompressedFlag;
      WriteIndexEntry(image, blockIndex, newRaw);

      RebuildTailFromBlock(image, header, blockIndex, appendOffset, payload.Length, storedUncompressed);
    }
  }

  /// <summary>
  /// After an append-grow on <paramref name="movedBlockIndex"/>, copies every
  /// block past it to the new tail so that the (next - this) length contract
  /// holds for the entire index. The trailing sentinel is updated to the
  /// final EOF.
  /// </summary>
  private static void RebuildTailFromBlock(
      Stream image, CsoHeader oldHeader, int movedBlockIndex,
      long movedBlockNewOffset, int movedBlockNewSize, bool movedBlockStoredUncompressed) {

    var cursor = movedBlockNewOffset + movedBlockNewSize;
    var newIndex = new uint[oldHeader.IndexRaw.Length];
    Array.Copy(oldHeader.IndexRaw, newIndex, oldHeader.IndexRaw.Length);
    newIndex[movedBlockIndex] = (uint)movedBlockNewOffset
      | (movedBlockStoredUncompressed ? CsoWriter.IndexUncompressedFlag : 0);

    for (var i = movedBlockIndex + 1; i < oldHeader.BlockCount; ++i) {
      var oldOff = (long)(oldHeader.IndexRaw[i] & CsoWriter.IndexOffsetMask) << oldHeader.Align;
      var oldEnd = (long)(oldHeader.IndexRaw[i + 1] & CsoWriter.IndexOffsetMask) << oldHeader.Align;
      var oldLen = (int)(oldEnd - oldOff);
      if (oldLen < 0) oldLen = 0;
      var buf = new byte[oldLen];
      if (oldLen > 0) {
        image.Position = oldOff;
        ReadExact(image, buf);
      }
      image.Position = cursor;
      image.Write(buf, 0, oldLen);

      if (cursor > CsoWriter.IndexOffsetMask)
        throw new InvalidOperationException(
          "CSO image would exceed 2 GiB after append — would overflow a 31-bit offset.");

      newIndex[i] = (uint)cursor | (oldHeader.IndexRaw[i] & CsoWriter.IndexUncompressedFlag);
      cursor += oldLen;
    }
    if (cursor > CsoWriter.IndexOffsetMask)
      throw new InvalidOperationException(
        "CSO image would exceed 2 GiB after append — would overflow a 31-bit offset.");
    newIndex[oldHeader.BlockCount] = (uint)cursor;
    image.SetLength(cursor);

    image.Position = CsoWriter.HeaderSize;
    Span<byte> idxBuf = stackalloc byte[4];
    for (var i = 0; i < newIndex.Length; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(idxBuf, newIndex[i]);
      image.Write(idxBuf);
    }
  }

  internal sealed record CsoHeader(
    uint HeaderSize, ulong UncompressedSize, uint BlockSize, byte Version, byte Align,
    int BlockCount, uint[] IndexRaw);

  internal static CsoHeader ReadHeader(Stream image) {
    image.Position = 0;
    Span<byte> hdr = stackalloc byte[CsoWriter.HeaderSize];
    ReadExact(image, hdr);
    if (hdr[0] != 'C' || hdr[1] != 'I' || hdr[2] != 'S' || hdr[3] != 'O')
      throw new InvalidDataException("Not a CSO v1 image — missing 'CISO' magic.");
    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4, 4));
    var uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(8, 8));
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(16, 4));
    var version = hdr[20];
    var align = hdr[21];

    if (version != 1)
      throw new NotSupportedException(
        $"CSO in-place modify supports v1 only (got v{version}). CSO v2 / ZSO are deferred.");
    if (align != 0)
      throw new NotSupportedException(
        $"CSO in-place modify requires align=0 (got align={align}). Non-zero align is deferred " +
        "because offset shifts would silently misplace a moved block.");
    if (blockSize == 0)
      throw new InvalidDataException("CSO block_size is zero.");

    var blockCount = (int)((uncompressedSize + blockSize - 1) / blockSize);
    var indexCount = blockCount + 1;
    var idxBytes = new byte[indexCount * 4];
    image.Position = CsoWriter.HeaderSize;
    ReadExact(image, idxBytes);
    var indexRaw = new uint[indexCount];
    for (var i = 0; i < indexCount; ++i)
      indexRaw[i] = BinaryPrimitives.ReadUInt32LittleEndian(idxBytes.AsSpan(i * 4, 4));

    return new CsoHeader(headerSize, uncompressedSize, blockSize, version, align, blockCount, indexRaw);
  }

  private static void WriteIndexEntry(Stream image, int blockIndex, uint rawEntry) {
    image.Position = CsoWriter.HeaderSize + blockIndex * 4L;
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, rawEntry);
    image.Write(buf);
  }

  private static void ReadExact(Stream s, Span<byte> dst) {
    var read = 0;
    while (read < dst.Length) {
      var n = s.Read(dst[read..]);
      if (n <= 0) throw new EndOfStreamException("Unexpected end of CSO stream.");
      read += n;
    }
  }

  private static void ReadExact(Stream s, byte[] dst) {
    var read = 0;
    while (read < dst.Length) {
      var n = s.Read(dst, read, dst.Length - read);
      if (n <= 0) throw new EndOfStreamException("Unexpected end of CSO stream.");
      read += n;
    }
  }
}
