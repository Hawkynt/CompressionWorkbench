using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Snappy;

namespace FileFormat.Snappy;

/// <summary>
/// Reads data from the Snappy framing format (streams).
/// </summary>
public sealed class SnappyFrameReader {
  private const byte ChunkCompressed = 0x00;
  private const byte ChunkUncompressed = 0x01;
  private const byte ChunkStreamId = 0xFF;

  private readonly Stream _input;

  /// <summary>
  /// Initializes a new <see cref="SnappyFrameReader"/>.
  /// </summary>
  /// <param name="input">The input stream.</param>
  public SnappyFrameReader(Stream input) => this._input = input;

  /// <summary>
  /// Reads and decompresses the entire Snappy framing stream.
  /// </summary>
  /// <returns>The decompressed data.</returns>
  public byte[] Read() {
    using var output = new MemoryStream();
    bool seenStreamId = false;

    while (true) {
      int chunkType = this._input.ReadByte();
      if (chunkType < 0)
        break;

      int length = ReadLength24();

      switch (chunkType) {
        case ChunkStreamId:
          // Stream identifier — validate and skip
          var id = new byte[length];
          ReadExact(id);
          if (length != 6 || !id.AsSpan().SequenceEqual(SnappyConstants.StreamIdentifier))
            throw new InvalidDataException("Invalid Snappy stream identifier.");
          seenStreamId = true;
          break;

        case ChunkCompressed: {
          if (!seenStreamId)
            throw new InvalidDataException("Snappy compressed chunk before stream identifier.");
          var chunkData = new byte[length];
          ReadExact(chunkData);

          uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(chunkData.AsSpan(0, 4));
          var compressedData = chunkData.AsSpan(4);
          byte[] decompressed = SnappyDecompressor.Decompress(compressedData.ToArray());

          uint actualCrc = MaskChecksum(ComputeCrc32C(decompressed));
          if (expectedCrc != actualCrc)
            throw new InvalidDataException("Snappy chunk CRC mismatch.");

          output.Write(decompressed);
          break;
        }

        case ChunkUncompressed: {
          if (!seenStreamId)
            throw new InvalidDataException("Snappy uncompressed chunk before stream identifier.");
          var chunkData = new byte[length];
          ReadExact(chunkData);

          uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(chunkData.AsSpan(0, 4));
          var rawData = chunkData.AsSpan(4);

          uint actualCrc = MaskChecksum(ComputeCrc32C(rawData.ToArray()));
          if (expectedCrc != actualCrc)
            throw new InvalidDataException("Snappy chunk CRC mismatch.");

          output.Write(rawData);
          break;
        }

        default:
          // Skip unknown/reserved chunks (type 0x02-0x7F: unskippable, 0x80-0xFE: skippable)
          if (chunkType >= 0x02 && chunkType <= 0x7F)
            throw new InvalidDataException($"Unknown unskippable Snappy chunk type: 0x{chunkType:X2}");
          // Skippable chunk
          var skipData = new byte[length];
          ReadExact(skipData);
          break;
      }
    }

    return output.ToArray();
  }

  private int ReadLength24() {
    int b0 = this._input.ReadByte();
    int b1 = this._input.ReadByte();
    int b2 = this._input.ReadByte();
    if (b0 < 0 || b1 < 0 || b2 < 0)
      throw new EndOfStreamException("Unexpected end of Snappy stream.");
    return b0 | (b1 << 8) | (b2 << 16);
  }

  private void ReadExact(byte[] buffer) {
    int remaining = buffer.Length;
    int offset = 0;
    while (remaining > 0) {
      int read = this._input.Read(buffer, offset, remaining);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of Snappy stream data.");
      offset += read;
      remaining -= read;
    }
  }

  private static uint MaskChecksum(uint crc) =>
    ((crc >> 15) | (crc << 17)) + 0xa282ead8;

  private static uint ComputeCrc32C(byte[] data) {
    var crc = new Crc32(Crc32.Castagnoli);
    crc.Update(data);
    return crc.Value;
  }
}
