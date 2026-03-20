using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Snappy;

namespace FileFormat.Snappy;

/// <summary>
/// Writes data in the Snappy framing format (streams).
/// </summary>
public sealed class SnappyFrameWriter {
  private const int MaxBlockSize = 65536;
  private const byte ChunkCompressed = 0x00;
  private const byte ChunkUncompressed = 0x01;
  private const byte ChunkStreamId = 0xFF;

  private readonly Stream _output;

  /// <summary>
  /// Initializes a new <see cref="SnappyFrameWriter"/>.
  /// </summary>
  /// <param name="output">The output stream.</param>
  public SnappyFrameWriter(Stream output) => this._output = output;

  /// <summary>
  /// Writes data as a Snappy framing stream.
  /// </summary>
  /// <param name="data">The uncompressed data.</param>
  public void Write(ReadOnlySpan<byte> data) {
    WriteStreamIdentifier();

    int offset = 0;
    while (offset < data.Length) {
      int blockLen = Math.Min(MaxBlockSize, data.Length - offset);
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
    uint crc = MaskChecksum(ComputeCrc32C(uncompressed));
    byte[] compressed = SnappyCompressor.Compress(uncompressed);

    if (compressed.Length < uncompressed.Length) {
      // Compressed chunk: type 0x00
      int chunkLen = 4 + compressed.Length; // 4 bytes CRC + compressed data
      this._output.WriteByte(ChunkCompressed);
      WriteLength24(chunkLen);
      WriteCrc(crc);
      this._output.Write(compressed);
    } else {
      // Uncompressed chunk: type 0x01
      int chunkLen = 4 + uncompressed.Length;
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
