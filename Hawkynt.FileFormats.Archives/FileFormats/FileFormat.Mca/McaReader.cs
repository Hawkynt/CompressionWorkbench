#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Streams;
using FileFormat.Gzip;
using FileFormat.Zlib;

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
  private readonly List<string> _problems = [];

  /// <summary>
  /// Gets the chunks.
  /// </summary>
  public IReadOnlyList<ChunkEntry> Chunks => this._chunks;

  /// <summary>
  /// Structural defects skipped by a lenient parse; always empty after a strict parse, which throws
  /// on the first one instead.
  /// </summary>
  public IReadOnlyList<string> Problems => this._problems;

  /// <summary>
  /// Initializes a new instance of <see cref="McaReader"/>.
  /// </summary>
  /// <param name="data">The complete region file.</param>
  /// <param name="strict">
  /// <see langword="true"/> throws <see cref="InvalidDataException"/> on the first malformed
  /// header or location entry; <see langword="false"/> skips it and records it in
  /// <see cref="Problems"/> so a damaged region can still be listed.
  /// </param>
  public McaReader(byte[] data, bool strict = true) {
    this._data = data;
    if (data.Length < 8192) {
      this.Reject(strict, $"An MCA region must contain its complete 8 KiB header; got {data.Length} bytes.");
      return;
    }

    for (var i = 0; i < 1024; ++i) {
      var entry = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i * 4));
      var sectorOffset = (int)(entry >> 8);
      var sectorCount = (byte)(entry & 0xFF);
      if (sectorOffset == 0 && sectorCount == 0) continue;
      if (sectorOffset < 2 || sectorCount == 0) {
        this.Reject(strict, $"Invalid MCA location entry for chunk {i} (sector {sectorOffset}, count {sectorCount}).");
        continue;
      }

      // The payload must lie inside the file and inside its own sector allocation. The allocation
      // itself may run past the end of an unpadded file, which Minecraft tolerates on read.
      var byteOffset = (long)sectorOffset * 4096;
      if (byteOffset + 5 > data.Length) {
        this.Reject(strict, $"MCA chunk {i} points outside the region file.");
        continue;
      }
      var chunkLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan((int)byteOffset));
      if (chunkLen < 1 || chunkLen > (long)sectorCount * 4096 - 4 || byteOffset + 4 + chunkLen > data.Length) {
        this.Reject(strict, $"Invalid MCA chunk length {chunkLen} for chunk {i}.");
        continue;
      }
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

  private void Reject(bool strict, string problem) {
    if (strict) throw new InvalidDataException(problem);
    this._problems.Add(problem);
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
        using var gz = new GzipStream(input, CompressionStreamMode.Decompress);
        gz.CopyTo(output);
        break;
      }
      case 2: {
        using var zl = new ZlibStream(input, CompressionStreamMode.Decompress);
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
