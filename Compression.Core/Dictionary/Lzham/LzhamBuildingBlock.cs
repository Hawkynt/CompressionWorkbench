using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzham;

/// <summary>
/// Exposes LZHAM (LZ + Huffman) as a benchmarkable building block.
/// Inspired by Valve's open-source LZHAM codec used in Steam.
/// </summary>
public sealed class LzhamBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_LZHAM";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LZHAM";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "LZ77 + Huffman, inspired by Valve's LZHAM codec";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Header: 4-byte LE original size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0) return ms.ToArray();

    var encoder = new LzhamEncoder();
    var encoded = encoder.Encode(data);

    ms.Write(encoded);
    return ms.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0) return [];

    var compressed = data[4..].ToArray();
    var decoder = new LzhamDecoder();
    return decoder.Decode(compressed, originalSize);
  }
}
