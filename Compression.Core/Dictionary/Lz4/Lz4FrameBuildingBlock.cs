#pragma warning disable CS1591

using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lz4;

/// <summary>
/// Exposes the LZ4 frame format (with content size, checksums, and multi-block support)
/// as a benchmarkable building block. Unlike <see cref="Lz4BuildingBlock"/> which wraps
/// raw blocks with a custom size prefix, this produces spec-compliant LZ4 frames
/// (magic 0x184D2204) that any LZ4 tool can read.
/// </summary>
public sealed class Lz4FrameBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lz4Frame";
  /// <inheritdoc/>
  public string DisplayName => "LZ4 Frame";
  /// <inheritdoc/>
  public string Description => "LZ4 frame format with content size, checksums, and multi-block support";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const uint FrameMagic = 0x184D2204;
  private const int DefaultBlockMaxSize = 4 * 1024 * 1024; // 4 MB
  private const int BlockMaxSizeBits = 7; // 4 MB

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    WriteFrameHeader(output, data.Length);

    var offset = 0;
    while (offset < data.Length) {
      var blockLen = Math.Min(DefaultBlockMaxSize, data.Length - offset);
      var block = data.Slice(offset, blockLen);
      WriteBlock(output, block);
      offset += blockLen;
    }

    // End mark (0x00000000)
    Span<byte> endMark = stackalloc byte[4];
    output.Write(endMark);

    // Content checksum
    var checksum = XxHash32.Compute(data);
    Span<byte> csBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(csBuf, checksum);
    output.Write(csBuf);

    return output.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var pos = 0;

    // Read magic
    if (data.Length < 4)
      throw new InvalidDataException("LZ4 frame too short.");
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
    if (magic != FrameMagic)
      throw new InvalidDataException($"Invalid LZ4 frame magic: 0x{magic:X8}");
    pos = 4;

    // Read FLG + BD
    if (pos + 2 > data.Length)
      throw new InvalidDataException("Truncated LZ4 frame header.");
    var flg = data[pos++];
    var bd = data[pos++];

    var contentSizePresent = ((flg >> 3) & 1) == 1;
    var contentChecksum = ((flg >> 2) & 1) == 1;
    var blockChecksum = ((flg >> 4) & 1) == 1;

    var blockMaxSizeBits = (bd >> 4) & 0x07;
    var blockMaxSize = blockMaxSizeBits switch {
      4 => 65536,
      5 => 262144,
      6 => 1048576,
      7 => 4194304,
      _ => DefaultBlockMaxSize
    };

    // Skip content size if present
    var headerFieldSize = contentSizePresent ? 8 : 0;
    long contentSize = 0;
    if (contentSizePresent) {
      if (pos + 8 > data.Length)
        throw new InvalidDataException("Truncated LZ4 content size.");
      contentSize = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(pos));
      pos += 8;
    }

    // Skip header checksum byte
    if (pos >= data.Length)
      throw new InvalidDataException("Truncated LZ4 header checksum.");
    pos++;

    // Read blocks
    using var output = new MemoryStream(contentSize > 0 ? (int)contentSize : 4096);
    while (pos + 4 <= data.Length) {
      var blockHeader = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos));
      pos += 4;

      if (blockHeader == 0)
        break; // End mark

      var isUncompressed = (blockHeader & 0x80000000u) != 0;
      var dataSize = (int)(blockHeader & 0x7FFFFFFFu);

      if (pos + dataSize > data.Length)
        throw new InvalidDataException("Truncated LZ4 block data.");

      if (isUncompressed) {
        output.Write(data.Slice(pos, dataSize));
      } else {
        var decompBuf = new byte[blockMaxSize];
        var written = Lz4BlockDecompressor.Decompress(data.Slice(pos, dataSize), decompBuf);
        output.Write(decompBuf, 0, written);
      }

      pos += dataSize;

      // Skip block checksum if present
      if (blockChecksum)
        pos += 4;
    }

    // Verify content checksum if present
    if (contentChecksum && pos + 4 <= data.Length) {
      var expectedCs = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos));
      var result = output.ToArray();
      var actualCs = XxHash32.Compute(result);
      if (expectedCs != actualCs)
        throw new InvalidDataException("LZ4 frame content checksum mismatch.");
      return result;
    }

    return output.ToArray();
  }

  private static void WriteFrameHeader(Stream output, int contentSize) {
    Span<byte> buf = stackalloc byte[15];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, FrameMagic);

    // FLG byte
    var flg = 0;
    flg |= 1 << 6; // Version = 01
    flg |= 1 << 5; // Block independence
    flg |= 1 << 3; // Content size present
    flg |= 1 << 2; // Content checksum
    buf[4] = (byte)flg;

    // BD byte
    buf[5] = (byte)(BlockMaxSizeBits << 4);

    // Content size (8 bytes LE)
    BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(6), contentSize);

    // Header checksum
    var headerData = buf.Slice(4, 10);
    var hc = XxHash32.Compute(headerData);
    buf[14] = (byte)((hc >> 8) & 0xFF);

    output.Write(buf.Slice(0, 15));
  }

  private static void WriteBlock(Stream output, ReadOnlySpan<byte> uncompressed) {
    var compressed = Lz4BlockCompressor.Compress(uncompressed);
    Span<byte> header = stackalloc byte[4];

    if (compressed.Length >= uncompressed.Length) {
      // Store uncompressed
      BinaryPrimitives.WriteUInt32LittleEndian(header,
        (uint)uncompressed.Length | 0x80000000u);
      output.Write(header);
      output.Write(uncompressed);
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)compressed.Length);
      output.Write(header);
      output.Write(compressed);
    }
  }
}
