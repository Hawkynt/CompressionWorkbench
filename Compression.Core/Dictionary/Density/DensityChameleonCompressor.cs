using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Density;

/// <summary>
/// Compresses data using the Density "Chameleon" predictive-dictionary format
/// (see <see cref="DensityConstants"/>).
/// </summary>
public static class DensityChameleonCompressor {
  /// <summary>
  /// Compresses the input data using Chameleon.
  /// </summary>
  /// <param name="source">The data to compress.</param>
  /// <returns>The compressed data, prefixed with a 4-byte little-endian original length.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> source) {
    var output = new List<byte>(source.Length + 16);
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, source.Length);
    output.AddRange(header);

    if (source.Length == 0)
      return output.ToArray();

    var data = source;
    var totalChunks = (data.Length + DensityConstants.ChunkSize - 1) / DensityConstants.ChunkSize;
    var table = new uint[DensityConstants.HashSize];

    uint prevChunk = 0;
    var pos = 0;
    var chunkIndex = 0;

    while (chunkIndex < totalChunks) {
      var chunksInBlock = Math.Min(DensityConstants.ChunksPerBlock, totalChunks - chunkIndex);
      uint signature = 0;
      var blockData = new List<byte>(chunksInBlock * DensityConstants.ChunkSize);

      for (var i = 0; i < chunksInBlock; ++i) {
        var chunk = ReadChunkPadded(data, pos);
        var hash = Hash(prevChunk);
        var predicted = table[hash];

        if (predicted != chunk) {
          signature |= 1u << i;
          blockData.Add((byte)chunk);
          blockData.Add((byte)(chunk >> 8));
          blockData.Add((byte)(chunk >> 16));
          blockData.Add((byte)(chunk >> 24));
        }

        table[hash] = chunk;
        prevChunk = chunk;
        pos += DensityConstants.ChunkSize;
        ++chunkIndex;
      }

      output.Add((byte)signature);
      output.Add((byte)(signature >> 8));
      output.Add((byte)(signature >> 16));
      output.Add((byte)(signature >> 24));
      output.AddRange(blockData);
    }

    return output.ToArray();
  }

  private static uint ReadChunkPadded(ReadOnlySpan<byte> data, int pos) {
    uint chunk = 0;
    for (var i = 0; i < DensityConstants.ChunkSize; ++i) {
      var p = pos + i;
      if (p < data.Length)
        chunk |= (uint)data[p] << (8 * i);
    }
    return chunk;
  }

  private static int Hash(uint chunk) =>
    (int)((chunk * DensityConstants.HashMultiplier) >> (32 - DensityConstants.HashBits));
}
