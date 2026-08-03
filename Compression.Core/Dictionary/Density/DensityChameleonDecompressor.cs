using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Density;

/// <summary>
/// Decompresses data produced by <see cref="DensityChameleonCompressor"/>.
/// </summary>
public static class DensityChameleonDecompressor {
  /// <summary>
  /// Decompresses Chameleon-compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data, prefixed with a 4-byte little-endian original length.</param>
  /// <returns>The original decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">The compressed stream is malformed or truncated.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    if (compressed.Length < 4)
      throw new InvalidDataException("Density stream too short for header.");

    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    if (originalLength == 0)
      return [];

    var data = compressed[4..];
    var totalChunks = (originalLength + DensityConstants.ChunkSize - 1) / DensityConstants.ChunkSize;
    var buffer = new byte[totalChunks * DensityConstants.ChunkSize];
    var table = new uint[DensityConstants.HashSize];

    uint prevChunk = 0;
    var pos = 0;
    var outPos = 0;
    var chunkIndex = 0;

    while (chunkIndex < totalChunks) {
      if (pos + 4 > data.Length)
        throw new InvalidDataException("Density stream truncated at signature word.");

      var signature = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
      pos += 4;

      var chunksInBlock = Math.Min(DensityConstants.ChunksPerBlock, totalChunks - chunkIndex);
      for (var i = 0; i < chunksInBlock; ++i) {
        var hash = Hash(prevChunk);
        uint chunk;

        if ((signature & (1u << i)) != 0) {
          if (pos + 4 > data.Length)
            throw new InvalidDataException("Density stream truncated at literal chunk.");
          chunk = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
          pos += 4;
          table[hash] = chunk;
        } else
          chunk = table[hash];

        buffer[outPos] = (byte)chunk;
        buffer[outPos + 1] = (byte)(chunk >> 8);
        buffer[outPos + 2] = (byte)(chunk >> 16);
        buffer[outPos + 3] = (byte)(chunk >> 24);
        outPos += DensityConstants.ChunkSize;

        prevChunk = chunk;
        ++chunkIndex;
      }
    }

    return buffer.AsSpan(0, originalLength).ToArray();
  }

  private static int Hash(uint chunk) =>
    (int)((chunk * DensityConstants.HashMultiplier) >> (32 - DensityConstants.HashBits));
}
