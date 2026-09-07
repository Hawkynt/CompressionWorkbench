#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Transforms;

namespace FileFormat.Bsc;

internal enum BscSortingContexts : byte {
  Following = 1,
  Preceding = 2,
}

/// <summary>
/// BSC (libbsc) stream implementation.
///
/// File layout:
///   [0..3]  magic "bsc1" (0x62 0x73 0x63 0x31)
///   [4..7]  int32 LE: block count
///   Per block:
///     BSC_BLOCK_HEADER (10 bytes):
///       [0..7]  int64 LE: blockOffset
///       [8]     int8:  recordSize
///       [9]     int8:  sortingContexts
///     Internal header (28 bytes = 7 × int32 LE):
///       blockSize, dataSize, mode, index,
///       adler32_data, adler32_compressed, adler32_header
///     Compressed payload bytes
/// </summary>
public static class BscStream {
  // -------------------------------------------------------------------------
  // Constants
  // -------------------------------------------------------------------------
  private static readonly byte[] Magic = [0x62, 0x73, 0x63, 0x31]; // "bsc1"
  private const int BscBlockHeaderSize = 10;   // offset(8) + recordSize(1) + sortingContexts(1)
  private const int InternalHeaderSize = 28;   // 7 × int32 LE
  private const int MinimumEncodedBlockSize = BscBlockHeaderSize + InternalHeaderSize;
  private const int ModeStoreRle = 0;          // historical managed BWT + MTF + zero-run mode

  internal const int MinimumBlockSize = 10_000;
  internal const int DefaultBlockSize = 25 * 1024 * 1024;
  internal const int MaximumBlockSize = 2047 * 1024 * 1024;

  // -------------------------------------------------------------------------
  // Public API
  // -------------------------------------------------------------------------
  /// <summary>
  /// Encodes the supplied input using libbsc's default 25 MiB block size and
  /// following-context ordering.
  /// </summary>
  public static void Compress(Stream input, Stream output)
    => Compress(input, output, DefaultBlockSize, BscSortingContexts.Following);

  /// <summary>
  /// Encodes the supplied input using the selected block size and sorting-context order.
  /// </summary>
  internal static void Compress(Stream input, Stream output, int blockSize, BscSortingContexts sortingContexts) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (blockSize is < MinimumBlockSize or > MaximumBlockSize)
      throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
        $"BSC block size must be between {MinimumBlockSize} and {MaximumBlockSize} bytes.");
    if (sortingContexts is not (BscSortingContexts.Following or BscSortingContexts.Preceding))
      throw new ArgumentOutOfRangeException(nameof(sortingContexts));

    var data = ReadAll(input);
    // libbsc writes zero blocks for an empty input; keep that envelope convention.
    var blockCount = data.Length == 0 ? 0 : ((data.Length - 1) / blockSize) + 1;

    output.Write(Magic);
    Span<byte> blockCountBytes = stackalloc byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(blockCountBytes, blockCount);
    output.Write(blockCountBytes);

    var offset = 0;
    for (var blockIndex = 0; blockIndex < blockCount; ++blockIndex) {
      var length = Math.Min(blockSize, data.Length - offset);
      WriteBlock(data.AsSpan(offset, length), output, offset, sortingContexts);
      offset += length;
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // --- Verify magic ---
    Span<byte> magic = stackalloc byte[4];
    input.ReadExactly(magic);
    if (!magic.SequenceEqual(Magic))
      throw new InvalidDataException("Not a BSC stream: invalid magic bytes");

    // --- Block count ---
    Span<byte> blockCountBytes = stackalloc byte[4];
    input.ReadExactly(blockCountBytes);
    var nBlocks = BinaryPrimitives.ReadInt32LittleEndian(blockCountBytes);
    if (nBlocks < 0)
      throw new InvalidDataException($"BSC: invalid block count {nBlocks}");

    // Every block consumes at least its 10-byte file header plus the 28-byte
    // libbsc block header. This bounds hostile block counts without imposing an
    // arbitrary 65,536-block ceiling that valid small-block archives can exceed.
    if (input.CanSeek) {
      var remaining = input.Length - input.Position;
      if ((long)nBlocks * MinimumEncodedBlockSize > remaining)
        throw new InvalidDataException($"BSC: block count {nBlocks} exceeds the available stream data");
    }

    long expectedSequentialOffset = 0;
    for (var b = 0; b < nBlocks; ++b) {
      // --- BSC_BLOCK_HEADER (10 bytes) ---
      Span<byte> bscBlockHeader = stackalloc byte[BscBlockHeaderSize];
      input.ReadExactly(bscBlockHeader);
      var blockOffset = BinaryPrimitives.ReadInt64LittleEndian(bscBlockHeader);
      var recordSize = bscBlockHeader[8];
      var sortingContexts = (BscSortingContexts)bscBlockHeader[9];
      if (blockOffset < 0)
        throw new InvalidDataException($"BSC: invalid negative block offset {blockOffset}");
      if (recordSize != 1)
        throw new NotSupportedException($"BSC: record reordering (record size {recordSize}) is not supported");
      if (sortingContexts is not (BscSortingContexts.Following or BscSortingContexts.Preceding))
        throw new InvalidDataException($"BSC: invalid sorting-context order {(byte)sortingContexts}");

      // --- Internal header (28 bytes) ---
      Span<byte> headerBytes = stackalloc byte[InternalHeaderSize];
      input.ReadExactly(headerBytes);

      var blockSize         = BinaryPrimitives.ReadInt32LittleEndian(headerBytes[0..]);
      var dataSize          = BinaryPrimitives.ReadInt32LittleEndian(headerBytes[4..]);
      // mode                = BinaryPrimitives.ReadInt32LittleEndian(headerBytes[8..]);
      var primaryIndex      = BinaryPrimitives.ReadInt32LittleEndian(headerBytes[12..]);
      var adler32Data       = (uint)BinaryPrimitives.ReadInt32LittleEndian(headerBytes[16..]);
      var adler32Compressed = (uint)BinaryPrimitives.ReadInt32LittleEndian(headerBytes[20..]);
      var adler32Header     = (uint)BinaryPrimitives.ReadInt32LittleEndian(headerBytes[24..]);

      // Verify header checksum (first 24 bytes of header)
      var expectedHeaderChecksum = Adler32(headerBytes[..24]);
      if (expectedHeaderChecksum != adler32Header)
        throw new InvalidDataException("BSC: header checksum mismatch");

      // Read payload
      var payloadSize = blockSize - InternalHeaderSize;
      if (payloadSize < 0)
        throw new InvalidDataException($"BSC: invalid block size {blockSize}");
      if (dataSize < 0 || dataSize > MaximumBlockSize)
        throw new InvalidDataException($"BSC: invalid data size {dataSize}");
      if (blockOffset > long.MaxValue - dataSize)
        throw new InvalidDataException("BSC: block range overflows the output address space");
      if (input.CanSeek && payloadSize > input.Length - input.Position)
        throw new InvalidDataException("BSC: truncated block payload");

      var payload = new byte[payloadSize];
      if (payloadSize > 0)
        input.ReadExactly(payload);

      // Verify compressed payload checksum
      var actualCompressedChecksum = Adler32(payload);
      if (actualCompressedChecksum != adler32Compressed)
        throw new InvalidDataException("BSC: compressed payload checksum mismatch");

      // --- Reverse pipeline: zero-run decode → MTF inverse → BWT inverse ---
      var mtfData = ZeroRunDecode(payload, dataSize);
      if (mtfData.Length != dataSize)
        throw new InvalidDataException($"BSC: decoded block size {mtfData.Length} does not match expected {dataSize}");
      var bwtData = MoveToFrontTransform.Decode(mtfData);
      var original = BurrowsWheelerTransform.Inverse(bwtData, primaryIndex);
      if (sortingContexts == BscSortingContexts.Preceding)
        Array.Reverse(original);

      // Verify original data checksum
      var actualDataChecksum = Adler32(original);
      if (actualDataChecksum != adler32Data)
        throw new InvalidDataException("BSC: original data checksum mismatch");

      // libbsc may emit blocks out of physical order when parallel block
      // processing is enabled. blockOffset is therefore semantic, not decorative.
      if (output.CanSeek)
        output.Position = blockOffset;
      else if (blockOffset != expectedSequentialOffset)
        throw new NotSupportedException("BSC: out-of-order blocks require a seekable output stream");

      output.Write(original);
      expectedSequentialOffset = blockOffset + original.LongLength;
    }

    if (output.CanSeek)
      output.Position = output.Length;
  }

  private static void WriteBlock(
      ReadOnlySpan<byte> data, Stream output, long blockOffset, BscSortingContexts sortingContexts) {
    var adler32Data = Adler32(data);

    ReadOnlySpan<byte> transformInput = data;
    byte[]? reversed = null;
    if (sortingContexts == BscSortingContexts.Preceding) {
      reversed = data.ToArray();
      Array.Reverse(reversed);
      transformInput = reversed;
    }

    // --- Apply pipeline: BWT → MTF → zero-run-length encode ---
    var (bwtData, primaryIndex) = BurrowsWheelerTransform.Forward(transformInput);
    var mtfData = MoveToFrontTransform.Encode(bwtData);
    var payload = ZeroRunEncode(mtfData);

    // --- Adler-32 checksums ---
    var adler32Compressed = Adler32(payload);

    // --- Build internal header (first 24 bytes, then checksum of those) ---
    var blockSize = InternalHeaderSize + payload.Length;
    Span<byte> headerBytes = stackalloc byte[InternalHeaderSize];
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes[0..], blockSize);
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes[4..], data.Length);
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes[8..], ModeStoreRle);
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes[12..], primaryIndex);
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes[16..], (int)adler32Data);
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes[20..], (int)adler32Compressed);
    var adler32Header = Adler32(headerBytes[..24]);
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes[24..], (int)adler32Header);

    // BSC_BLOCK_HEADER follows libbsc's public file envelope: original block
    // offset, record size 1 (no record reordering), and context order 1/2.
    Span<byte> bscBlockHeader = stackalloc byte[BscBlockHeaderSize];
    BinaryPrimitives.WriteInt64LittleEndian(bscBlockHeader, blockOffset);
    bscBlockHeader[8] = 1;
    bscBlockHeader[9] = (byte)sortingContexts;
    output.Write(bscBlockHeader);
    output.Write(headerBytes);
    output.Write(payload);
  }

  // -------------------------------------------------------------------------
  // Zero-run-length encoding
  // Zeros are encoded as: 0x00 followed by a byte giving (run_length - 1).
  // Non-zero bytes pass through unchanged.
  // This avoids reserving an escape byte since MTF output concentrates zeros.
  // -------------------------------------------------------------------------
  private static byte[] ZeroRunEncode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var result = new List<byte>(data.Length);
    var i = 0;
    while (i < data.Length) {
      if (data[i] != 0) {
        result.Add(data[i]);
        i++;
      } else {
        // Count consecutive zeros
        var runStart = i;
        while (i < data.Length && data[i] == 0)
          i++;
        var runLen = i - runStart;

        // Emit runs in chunks of max 256 (run byte = runLen-1, fits in one byte)
        while (runLen > 0) {
          var chunk = Math.Min(runLen, 256);
          result.Add(0x00);
          result.Add((byte)(chunk - 1));
          runLen -= chunk;
        }
      }
    }

    return [.. result];
  }

  private static byte[] ZeroRunDecode(ReadOnlySpan<byte> data, int expectedSize) {
    if (data.Length == 0)
      return [];

    var result = new List<byte>(expectedSize);
    var i = 0;
    while (i < data.Length) {
      if (data[i] != 0) {
        if (result.Count >= expectedSize)
          throw new InvalidDataException("BSC: zero-run payload expands beyond the declared data size");
        result.Add(data[i]);
        i++;
      } else {
        if (i + 1 >= data.Length)
          throw new InvalidDataException("BSC: truncated zero-run escape sequence");
        var runLen = (int)data[i + 1] + 1;
        if (runLen > expectedSize - result.Count)
          throw new InvalidDataException("BSC: zero-run payload expands beyond the declared data size");
        for (var r = 0; r < runLen; r++)
          result.Add(0x00);
        i += 2;
      }
    }

    return [.. result];
  }

  // -------------------------------------------------------------------------
  // Adler-32
  // -------------------------------------------------------------------------
  private static uint Adler32(ReadOnlySpan<byte> data) {
    uint a = 1, b = 0;
    foreach (var x in data) {
      a = (a + x) % 65521;
      b = (b + a) % 65521;
    }
    return (b << 16) | a;
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------
  private static byte[] ReadAll(Stream stream) {
    if (stream is MemoryStream ms)
      return ms.ToArray();
    using var buf = new MemoryStream();
    stream.CopyTo(buf);
    return buf.ToArray();
  }
}
