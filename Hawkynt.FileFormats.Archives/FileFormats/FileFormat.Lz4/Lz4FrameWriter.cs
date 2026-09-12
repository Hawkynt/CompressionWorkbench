using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lz4;

namespace FileFormat.Lz4;

/// <summary>
/// Writes data in the LZ4 frame format (specification v1.6.1+).
/// </summary>
public sealed class Lz4FrameWriter {
  private readonly Stream _output;
  private readonly int _blockMaxSize;
  private readonly bool _contentChecksum;
  private readonly bool _blockChecksum;
  private readonly Lz4CompressionLevel _level;

  /// <summary>
  /// Initializes a new <see cref="Lz4FrameWriter"/>.
  /// </summary>
  /// <param name="output">The output stream.</param>
  /// <param name="blockMaxSize">Maximum block size (default 4MB). Must be 64KB, 256KB, 1MB, or 4MB.</param>
  /// <param name="contentChecksum">Whether to write a content checksum at the end.</param>
  /// <param name="blockChecksum">Whether to write per-block checksums.</param>
  /// <param name="level">The compression level.</param>
  public Lz4FrameWriter(Stream output, int blockMaxSize = Lz4Constants.MaxBlockSize,
      bool contentChecksum = true, bool blockChecksum = false,
      Lz4CompressionLevel level = Lz4CompressionLevel.Fast) {
    this._output = output;
    this._blockMaxSize = blockMaxSize;
    this._contentChecksum = contentChecksum;
    this._blockChecksum = blockChecksum;
    this._level = level;
  }

  /// <summary>
  /// Writes data as an LZ4 frame.
  /// </summary>
  /// <param name="data">The uncompressed data.</param>
  public void Write(ReadOnlySpan<byte> data) {
    WriteFrameHeader(data.Length);

    var offset = 0;
    while (offset < data.Length) {
      var blockLen = Math.Min(this._blockMaxSize, data.Length - offset);
      var block = data.Slice(offset, blockLen);
      WriteBlock(block);
      offset += blockLen;
    }

    WriteEndMark();

    if (this._contentChecksum)
      WriteContentChecksum(data);
  }

  private void WriteFrameHeader(int contentSize) {
    Span<byte> buf = stackalloc byte[15];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, Lz4Constants.FrameMagic);

    // FLG byte
    var flg = 0;
    flg |= (1 << 6); // Version = 01
    flg |= (1 << 5); // Block independence = 1 (independent blocks)
    flg |= (this._blockChecksum ? 1 : 0) << 4;
    flg |= (1 << 3); // Content size present
    flg |= (this._contentChecksum ? 1 : 0) << 2;
    buf[4] = (byte)flg;

    // BD byte - block max size
    var bdBits = this._blockMaxSize switch {
      65536 => 4,
      262144 => 5,
      1048576 => 6,
      _ => 7 // 4MB
    };
    buf[5] = (byte)(bdBits << 4);

    // Content size (8 bytes LE)
    BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(6), contentSize);

    // Header checksum: xxHash32 of FLG+BD+content_size, take bits 8..15
    var headerData = buf.Slice(4, 10);
    var hc = XxHash32.Compute(headerData);
    buf[14] = (byte)((hc >> 8) & 0xFF);

    this._output.Write(buf.Slice(0, 15));
  }

  private void WriteBlock(ReadOnlySpan<byte> uncompressed) {
    var compressed = Lz4BlockCompressor.Compress(uncompressed, this._level);
    Span<byte> header = stackalloc byte[4];

    if (compressed.Length >= uncompressed.Length) {
      // Store uncompressed — set high bit
      BinaryPrimitives.WriteUInt32LittleEndian(header,
        (uint)uncompressed.Length | 0x80000000u);
      this._output.Write(header);
      this._output.Write(uncompressed);

      if (this._blockChecksum) {
        var bc = XxHash32.Compute(uncompressed);
        BinaryPrimitives.WriteUInt32LittleEndian(header, bc);
        this._output.Write(header);
      }
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)compressed.Length);
      this._output.Write(header);
      this._output.Write(compressed);

      if (this._blockChecksum) {
        var bc = XxHash32.Compute(compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(header, bc);
        this._output.Write(header);
      }
    }
  }

  private void WriteEndMark() {
    Span<byte> endMark = stackalloc byte[4];
    this._output.Write(endMark); // 0x00000000
  }

  private void WriteContentChecksum(ReadOnlySpan<byte> data) {
    var checksum = XxHash32.Compute(data);
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, checksum);
    this._output.Write(buf);
  }
}
