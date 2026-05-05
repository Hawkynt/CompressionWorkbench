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
/// 2 = zlib, 3 = uncompressed) + <c>length-1</c> bytes of compressed NBT.
/// </para>
/// </summary>
public sealed class McaReader {
  public sealed record ChunkEntry(int RegionX, int RegionZ, long OffsetBytes, int LengthBytes, byte CompressionType);

  private readonly byte[] _data;
  private readonly List<ChunkEntry> _chunks = [];

  public IReadOnlyList<ChunkEntry> Chunks => this._chunks;

  public McaReader(byte[] data) {
    this._data = data;
    if (data.Length < 8192) return;

    for (var i = 0; i < 1024; ++i) {
      var entry = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i * 4));
      var sectorOffset = (int)(entry >> 8);
      var sectorCount = (byte)(entry & 0xFF);
      if (sectorOffset == 0 || sectorCount == 0) continue;

      var byteOffset = (long)sectorOffset * 4096;
      if (byteOffset + 5 > data.Length) continue;
      var chunkLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan((int)byteOffset));
      var compressionType = data[(int)byteOffset + 4];
      this._chunks.Add(new ChunkEntry(
        RegionX: i & 31,
        RegionZ: (i >> 5) & 31,
        OffsetBytes: byteOffset,
        LengthBytes: chunkLen,
        CompressionType: compressionType));
    }
  }

  /// <summary>
  /// Decompresses and returns the NBT payload for a chunk. Throws when the chunk's
  /// compression type is unknown (only 1/2/3 are defined).
  /// </summary>
  public byte[] ExtractChunkNbt(ChunkEntry chunk) {
    var payloadOffset = (int)chunk.OffsetBytes + 5;
    var payloadLength = chunk.LengthBytes - 1;
    if (payloadOffset + payloadLength > this._data.Length)
      throw new InvalidDataException($"Chunk payload at {chunk.OffsetBytes:X} truncated.");

    var compressed = this._data.AsSpan(payloadOffset, payloadLength);
    using var input = new MemoryStream(compressed.ToArray());
    using var output = new MemoryStream();
    switch (chunk.CompressionType) {
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
        compressed.ToArray().CopyTo(output.GetBuffer(), 0);
        output.SetLength(compressed.Length);
        break;
      default:
        throw new NotSupportedException(
          $"Unknown MCA chunk compression type {chunk.CompressionType}.");
    }
    return output.ToArray();
  }
}
