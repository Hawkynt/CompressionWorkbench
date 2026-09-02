using Compression.Registry;

namespace Compression.Core.Dictionary.Lzfse;

/// <summary>
/// Exposes an LZFSE-inspired codec as a benchmarkable building block.
/// </summary>
/// <remarks>
/// See <see cref="LzfseConstants"/> for the format layout and provenance notes.
/// Reuses the project's shared FSE (tANS) engine under
/// <see cref="Compression.Core.Entropy.Fse"/> for entropy coding, as directed,
/// instead of a second implementation.
/// </remarks>
public sealed class LzfseBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Lzfse";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LZFSE";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "LZ77 parse with FSE (tANS) entropy coding of literals, match lengths, literal lengths and distances";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) =>
    LzfseCompressor.Compress(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) =>
    LzfseDecompressor.Decompress(data);
}
