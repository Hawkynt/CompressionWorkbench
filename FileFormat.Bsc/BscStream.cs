#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Transforms;

namespace FileFormat.Bsc;

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
  private const int ModeStoreRle = 0;          // sorter=0 (BWT), coder=0 (none/RLE)

  // -------------------------------------------------------------------------
  // Public API
  // -------------------------------------------------------------------------
  public static void Compress(Stream input, Stream output) {
    var data = ReadAll(input);

    // --- Apply pipeline: BWT → MTF → zero-run-length encode ---
    var (bwtData, primaryIndex) = BurrowsWheelerTransform.Forward(data);
    var mtfData = MoveToFrontTransform.Encode(bwtData);
    var payload = ZeroRunEncode(mtfData);

    // --- Build mode bitfield (sorter=0 BWT, coder=0, lzpMinLen=0, lzpHashSize=0) ---
    var mode = ModeStoreRle;

    // --- Adler-32 checksums ---
    var adler32Data       = Adler32(data);
    var adler32Compressed = Adler32(payload);

    // --- Build internal header (first 24 bytes, then checksum of those) ---
    var blockSize = InternalHeaderSize + payload.Length; // total compressed including header
    var dataSize  = data.Length;

    var headerBytes = new byte[InternalHeaderSize];
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes.AsSpan(0),  blockSize);
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes.AsSpan(4),  dataSize);
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes.AsSpan(8),  mode);
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes.AsSpan(12), primaryIndex);
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes.AsSpan(16), (int)adler32Data);
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes.AsSpan(20), (int)adler32Compressed);
    var adler32Header = Adler32(headerBytes.AsSpan(0, 24)); // checksum of first 24 bytes
    BinaryPrimitives.WriteInt32LittleEndian(headerBytes.AsSpan(24), (int)adler32Header);

    // --- Write file ---
    output.Write(Magic);

    // Block count
    var blockCountBytes = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(blockCountBytes, 1);
    output.Write(blockCountBytes);

    // BSC_BLOCK_HEADER: offset=0, recordSize=1, sortingContexts=1
    var bscBlockHeader = new byte[BscBlockHeaderSize];
    BinaryPrimitives.WriteInt64LittleEndian(bscBlockHeader.AsSpan(0), 0L);
    bscBlockHeader[8] = 1; // recordSize
    bscBlockHeader[9] = 1; // sortingContexts
    output.Write(bscBlockHeader);

    // Internal header + payload
    output.Write(headerBytes);
    output.Write(payload);
  }

  public static void Decompress(Stream input, Stream output) {
    // --- Verify magic ---
    var magic = new byte[4];
    input.ReadExactly(magic);
    if (magic[0] != 0x62 || magic[1] != 0x73 || magic[2] != 0x63 || magic[3] != 0x31)
      throw new InvalidDataException("Not a BSC stream: invalid magic bytes");

    // --- Block count ---
    var blockCountBytes = new byte[4];
    input.ReadExactly(blockCountBytes);
    var nBlocks = BinaryPrimitives.ReadInt32LittleEndian(blockCountBytes);
    if (nBlocks < 0 || nBlocks > 65536)
      throw new InvalidDataException($"BSC: invalid block count {nBlocks}");

    for (var b = 0; b < nBlocks; b++) {
      // --- BSC_BLOCK_HEADER (10 bytes) ---
      var bscBlockHeader = new byte[BscBlockHeaderSize];
      input.ReadExactly(bscBlockHeader);
      // offset, recordSize, sortingContexts — read but not validated further for round-trip

      // --- Internal header (28 bytes) ---
      var headerBytes = new byte[InternalHeaderSize];
      input.ReadExactly(headerBytes);

      var blockSize         = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(0));
      var dataSize          = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(4));
      // mode                = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(8));
      var primaryIndex      = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(12));
      var adler32Data       = (uint)BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(16));
      var adler32Compressed = (uint)BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(20));
      var adler32Header     = (uint)BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(24));

      // Verify header checksum (first 24 bytes of header)
      var expectedHeaderChecksum = Adler32(headerBytes.AsSpan(0, 24));
      if (expectedHeaderChecksum != adler32Header)
        throw new InvalidDataException("BSC: header checksum mismatch");

      // Read payload
      var payloadSize = blockSize - InternalHeaderSize;
      if (payloadSize < 0)
        throw new InvalidDataException($"BSC: invalid block size {blockSize}");

      var payload = new byte[payloadSize];
      if (payloadSize > 0)
        input.ReadExactly(payload);

      // Verify compressed payload checksum
      var actualCompressedChecksum = Adler32(payload);
      if (actualCompressedChecksum != adler32Compressed)
        throw new InvalidDataException("BSC: compressed payload checksum mismatch");

      // --- Reverse pipeline: zero-run decode → MTF inverse → BWT inverse ---
      var mtfData = ZeroRunDecode(payload, dataSize);
      var bwtData = MoveToFrontTransform.Decode(mtfData);
      var original = BurrowsWheelerTransform.Inverse(bwtData, primaryIndex);

      // Verify original data checksum
      var actualDataChecksum = Adler32(original);
      if (actualDataChecksum != adler32Data)
        throw new InvalidDataException("BSC: original data checksum mismatch");

      output.Write(original);
    }
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
        result.Add(data[i]);
        i++;
      } else {
        if (i + 1 >= data.Length)
          throw new InvalidDataException("BSC: truncated zero-run escape sequence");
        var runLen = (int)data[i + 1] + 1;
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
