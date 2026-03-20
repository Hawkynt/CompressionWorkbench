using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lz4;

namespace FileFormat.Lz4;

/// <summary>
/// Reads data from the LZ4 frame format (specification v1.6.1+).
/// </summary>
public sealed class Lz4FrameReader {
  private readonly Stream _input;

  /// <summary>
  /// Initializes a new <see cref="Lz4FrameReader"/>.
  /// </summary>
  /// <param name="input">The input stream containing an LZ4 frame.</param>
  public Lz4FrameReader(Stream input) => this._input = input;

  /// <summary>
  /// Reads and decompresses the entire LZ4 frame.
  /// </summary>
  /// <returns>The decompressed data.</returns>
  public byte[] Read() {
    var header = ReadFrameHeader();
    using var output = new MemoryStream(header.ContentSize > 0 ? (int)header.ContentSize : 4096);

    while (true) {
      int blockSize = ReadBlock(output, header);
      if (blockSize == 0)
        break;
    }

    if (header.ContentChecksum) {
      byte[] data = output.ToArray();
      uint expected = ReadUInt32();
      uint actual = XxHash32.Compute(data);
      if (expected != actual)
        throw new InvalidDataException("LZ4 frame content checksum mismatch.");
      return data;
    }

    return output.ToArray();
  }

  private FrameHeader ReadFrameHeader() {
    uint magic = ReadUInt32();
    if (magic != Lz4Constants.FrameMagic && magic != Lz4Constants.LegacyMagic)
      throw new InvalidDataException($"Invalid LZ4 frame magic: 0x{magic:X8}");

    if (magic == Lz4Constants.LegacyMagic)
      throw new NotSupportedException("LZ4 legacy frame format is not supported.");

    int flg = this._input.ReadByte();
    int bd = this._input.ReadByte();
    if (flg < 0 || bd < 0)
      throw new EndOfStreamException();

    bool blockIndependence = ((flg >> 5) & 1) == 1;
    bool blockChecksum = ((flg >> 4) & 1) == 1;
    bool contentSizePresent = ((flg >> 3) & 1) == 1;
    bool contentChecksum = ((flg >> 2) & 1) == 1;

    int blockMaxSizeBits = (bd >> 4) & 0x07;
    int blockMaxSize = blockMaxSizeBits switch {
      4 => 65536,
      5 => 262144,
      6 => 1048576,
      7 => 4194304,
      _ => Lz4Constants.MaxBlockSize
    };

    // Collect header bytes for checksum: FLG + BD + optional content size
    Span<byte> headerData = stackalloc byte[contentSizePresent ? 10 : 2];
    headerData[0] = (byte)flg;
    headerData[1] = (byte)bd;

    long contentSize = 0;
    if (contentSizePresent) {
      ReadExact(headerData.Slice(2, 8));
      contentSize = BinaryPrimitives.ReadInt64LittleEndian(headerData.Slice(2));
    }

    // Read and verify header checksum (second byte of xxHash32 of descriptor)
    int hc = this._input.ReadByte();
    if (hc < 0)
      throw new EndOfStreamException();

    byte expectedHc = (byte)((XxHash32.Compute(headerData) >> 8) & 0xFF);
    if ((byte)hc != expectedHc)
      throw new InvalidDataException("LZ4 frame header checksum mismatch.");

    return new FrameHeader {
      BlockIndependence = blockIndependence,
      BlockChecksum = blockChecksum,
      ContentChecksum = contentChecksum,
      ContentSize = contentSize,
      BlockMaxSize = blockMaxSize
    };
  }

  private int ReadBlock(MemoryStream output, FrameHeader header) {
    uint blockHeader = ReadUInt32();
    if (blockHeader == 0)
      return 0; // End mark

    bool isUncompressed = (blockHeader & 0x80000000u) != 0;
    int dataSize = (int)(blockHeader & 0x7FFFFFFFu);

    var blockData = new byte[dataSize];
    ReadExact(blockData);

    if (header.BlockChecksum) {
      uint expectedBc = ReadUInt32();
      uint actualBc = XxHash32.Compute(blockData);
      if (expectedBc != actualBc)
        throw new InvalidDataException("LZ4 block checksum mismatch.");
    }

    if (isUncompressed) {
      output.Write(blockData);
    } else {
      // Need to know uncompressed size. We'll decompress incrementally.
      int existingLen = (int)output.Length;
      // Use a large enough buffer — worst case is blockMaxSize
      var decompBuf = new byte[header.BlockMaxSize];
      int written = Lz4BlockDecompressor.Decompress(blockData, decompBuf);
      output.Write(decompBuf, 0, written);
    }

    return dataSize;
  }

  private uint ReadUInt32() {
    Span<byte> buf = stackalloc byte[4];
    ReadExact(buf);
    return BinaryPrimitives.ReadUInt32LittleEndian(buf);
  }

  private void ReadExact(Span<byte> buffer) {
    int remaining = buffer.Length;
    int offset = 0;
    while (remaining > 0) {
      int read = this._input.Read(buffer.Slice(offset, remaining));
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of LZ4 frame data.");
      offset += read;
      remaining -= read;
    }
  }

  private struct FrameHeader {
    public bool BlockIndependence;
    public bool BlockChecksum;
    public bool ContentChecksum;
    public long ContentSize;
    public int BlockMaxSize;
  }
}
