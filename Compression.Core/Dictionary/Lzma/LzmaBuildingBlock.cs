using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// Exposes the LZMA algorithm as a benchmarkable building block.
/// Format: 5-byte properties + 4-byte LE uncompressed size + compressed data.
/// </summary>
public sealed class LzmaBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzma";
  /// <inheritdoc/>
  public string DisplayName => "LZMA";
  /// <inheritdoc/>
  public string Description => "Lempel-Ziv-Markov chain Algorithm with range coding and sophisticated matching";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var encoder = new LzmaEncoder(dictionarySize: 1 << 20); // 1 MB dictionary for BB
    using var ms = new MemoryStream();

    // Write properties (5 bytes)
    ms.Write(encoder.Properties);
    // Write uncompressed size (4 bytes LE)
    Span<byte> sizeHeader = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(sizeHeader, data.Length);
    ms.Write(sizeHeader);
    // Write compressed data
    encoder.Encode(ms, data);

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var properties = data[..5].ToArray();
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data[5..]);
    using var ms = new MemoryStream(data[9..].ToArray());
    var decoder = new LzmaDecoder(ms, properties, originalSize);
    return decoder.Decode();
  }
}
