using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Snappy;

namespace FileFormat.Snappy;

/// <summary>
/// Writes data in the Snappy framing format (streams).
/// </summary>
public sealed class SnappyFrameWriter {
  /// <summary>Maximum uncompressed data size permitted in one Snappy framing chunk.</summary>
  public const int MaxBlockSize = 65536;

  private const byte ChunkCompressed = 0x00;
  private const byte ChunkUncompressed = 0x01;
  private const byte ChunkStreamId = 0xFF;

  private readonly Stream _output;
  private readonly int _blockSize;
  private readonly int _hashTableBits;

  /// <summary>
  /// Initializes a new <see cref="SnappyFrameWriter"/> with the historical 64 KiB chunk size and
  /// default Snappy hash table.
  /// </summary>
  /// <param name="output">The output stream.</param>
  public SnappyFrameWriter(Stream output)
    : this(output, MaxBlockSize, SnappyConstants.HashTableBits) { }

  /// <summary>
  /// Initializes a new <see cref="SnappyFrameWriter"/> with explicit encoder parameters.
  /// </summary>
  /// <param name="output">The output stream.</param>
  /// <param name="blockSize">Maximum uncompressed bytes per framing chunk (1 through 65536).</param>
  /// <param name="hashTableBits">Log2 of the Snappy encoder hash-table size (8 through 15).</param>
  public SnappyFrameWriter(Stream output, int blockSize, int hashTableBits) {
    ArgumentNullException.ThrowIfNull(output);
    if (blockSize is <= 0 or > MaxBlockSize)
      throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
        $"Snappy framing chunks must contain between 1 and {MaxBlockSize} uncompressed bytes.");
    if (hashTableBits is < SnappyConstants.MinHashTableBits or > SnappyConstants.MaxHashTableBits)
      throw new ArgumentOutOfRangeException(nameof(hashTableBits), hashTableBits,
        $"Snappy hash table width must be between {SnappyConstants.MinHashTableBits} and {SnappyConstants.MaxHashTableBits} bits.");

    this._output = output;
    this._blockSize = blockSize;
    this._hashTableBits = hashTableBits;
  }

  /// <summary>
  /// Writes data as a Snappy framing stream.
  /// </summary>
  /// <param name="data">The uncompressed data.</param>
  public void Write(ReadOnlySpan<byte> data) {
    WriteStreamIdentifier();

    var offset = 0;
    while (offset < data.Length) {
      var blockLen = Math.Min(this._blockSize, data.Length - offset);
      WriteChunk(data.Slice(offset, blockLen));
      offset += blockLen;
    }
  }

  private void WriteStreamIdentifier() {
    // Stream identifier chunk: type 0xFF, length 6, data = "sNaPpY"
    this._output.WriteByte(ChunkStreamId);
    WriteLength24(6);
    this._output.Write(SnappyConstants.StreamIdentifier);
  }

  private void WriteChunk(ReadOnlySpan<byte> uncompressed) {
    // CRC-32C (Castagnoli) masked
    var crc = MaskChecksum(ComputeCrc32C(uncompressed));
    var compressed = SnappyCompressor.Compress(uncompressed, this._hashTableBits);

    if (compressed.Length < uncompressed.Length) {
      // Compressed chunk: type 0x00
      var chunkLen = 4 + compressed.Length; // 4 bytes CRC + compressed data
      this._output.WriteByte(ChunkCompressed);
      WriteLength24(chunkLen);
      WriteCrc(crc);
      this._output.Write(compressed);
    } else {
      // Uncompressed chunk: type 0x01
      var chunkLen = 4 + uncompressed.Length;
      this._output.WriteByte(ChunkUncompressed);
      WriteLength24(chunkLen);
      WriteCrc(crc);
      this._output.Write(uncompressed);
    }
  }

  private void WriteLength24(int length) {
    this._output.WriteByte((byte)(length & 0xFF));
    this._output.WriteByte((byte)((length >> 8) & 0xFF));
    this._output.WriteByte((byte)((length >> 16) & 0xFF));
  }

  private void WriteCrc(uint crc) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, crc);
    this._output.Write(buf);
  }

  private static uint MaskChecksum(uint crc) =>
    ((crc >> 15) | (crc << 17)) + 0xa282ead8;

  private static uint ComputeCrc32C(ReadOnlySpan<byte> data) {
    var crc = new Crc32(Crc32.Castagnoli);
    crc.Update(data);
    return crc.Value;
  }
}
