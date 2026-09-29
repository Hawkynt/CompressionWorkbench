#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;

namespace FileFormat.Mca;

/// <summary>
/// Minecraft region file format (<c>.mca</c> / <c>.mcr</c>). A region holds up to
/// 32×32 = 1024 chunks in a single 8 KiB header + per-chunk compressed NBT payloads.
/// <para>
/// Layout: 4 KiB location table (1024×uint32 BE: high 3 bytes = 4 KiB-sector offset,
/// low byte = sector count) + 4 KiB timestamp table (1024×uint32 BE) + padded
/// payload area. Each chunk: 4-byte BE length + 1-byte compression type (1 = gzip,
/// 2 = zlib, 3 = uncompressed, 4 = LZ4-Java block stream) + <c>length-1</c> payload bytes.
/// </para>
/// </summary>
public sealed class McaReader {
  /// <summary>
  /// Represents a chunk entry.
  /// </summary>
  public sealed record ChunkEntry(int RegionX, int RegionZ, long OffsetBytes, int LengthBytes, byte CompressionType, uint Timestamp);

  private readonly byte[] _data;
  private readonly List<ChunkEntry> _chunks = [];

  /// <summary>
  /// Gets the chunks.
  /// </summary>
  public IReadOnlyList<ChunkEntry> Chunks => this._chunks;

  /// <summary>
  /// Initializes a new instance of <see cref="McaReader"/>.
  /// </summary>
  public McaReader(byte[] data) {
    this._data = data;
    if (data.Length < 8192) throw new InvalidDataException("An MCA region must contain its complete 8 KiB header.");

    for (var i = 0; i < 1024; ++i) {
      var entry = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i * 4));
      var sectorOffset = (int)(entry >> 8);
      var sectorCount = (byte)(entry & 0xFF);
      if (sectorOffset == 0 && sectorCount == 0) continue;
      if (sectorOffset < 2 || sectorCount == 0)
        throw new InvalidDataException($"Invalid MCA location entry for chunk {i}.");

      var byteOffset = (long)sectorOffset * 4096;
      if (byteOffset + 5 > data.Length || byteOffset + (long)sectorCount * 4096 > data.Length)
        throw new InvalidDataException($"MCA chunk {i} points outside the region file.");
      var chunkLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan((int)byteOffset));
      if (chunkLen < 1 || chunkLen > (long)sectorCount * 4096 - 4)
        throw new InvalidDataException($"Invalid MCA chunk length for chunk {i}.");
      var compressionType = data[(int)byteOffset + 4];
      this._chunks.Add(new ChunkEntry(
        RegionX: i & 31,
        RegionZ: (i >> 5) & 31,
        OffsetBytes: byteOffset,
        LengthBytes: chunkLen,
        CompressionType: compressionType,
        Timestamp: BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4096 + i * 4))));
    }
  }

  /// <summary>
  /// Decompresses and returns the NBT payload for a chunk. Throws when the chunk's
  /// compression type is unknown or the chunk uses an external <c>.mcc</c> payload.
  /// </summary>
  public byte[] ExtractChunkNbt(ChunkEntry chunk) {
    var payloadOffset = (int)chunk.OffsetBytes + 5;
    var payloadLength = chunk.LengthBytes - 1;
    if (payloadLength < 0 || payloadOffset + payloadLength > this._data.Length)
      throw new InvalidDataException($"Chunk payload at {chunk.OffsetBytes:X} truncated.");

    var compressed = this._data.AsSpan(payloadOffset, payloadLength);
    if ((chunk.CompressionType & 0x80) != 0)
      throw new NotSupportedException($"Chunk {chunk.RegionX},{chunk.RegionZ} uses an external .mcc payload, which is not embedded in this region file.");
    using var input = new MemoryStream(compressed.ToArray());
    using var output = new MemoryStream();
    switch (chunk.CompressionType & 0x7F) {
      case 1: {
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        gz.CopyTo(output);
        break;
      }
      case 2: {
        using var zl = new ZLibStream(input, CompressionMode.Decompress);
        zl.CopyTo(output);
        break;
      }
      case 3:
        output.Write(compressed);
        break;
      case 4: {
        output.Write(McaLz4BlockStream.Decompress(compressed));
        break;
      }
      default:
        throw new NotSupportedException(
          $"Unknown MCA chunk compression type {chunk.CompressionType}.");
    }
    return output.ToArray();
  }
}
