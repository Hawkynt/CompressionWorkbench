using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.Paq8hp;

/// <summary>
/// Exposes the reduced PAQ8hp-style model set as a benchmarkable building block.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not the real PAQ8hp.</b> The reference compressor ships dozens
/// of specialised models behind a large mixing network. This building block
/// implements a small, explicitly documented subset: hashed byte-history
/// contexts (orders 0, 1, 2, 3, 4, 6) and a match model, combined through
/// PAQ8-style context-selected mixing (16 weight sets keyed by the previous
/// byte's high nibble) and refined by one SSE stage. See
/// <see cref="Paq8hpCompressor"/> for the full model list and citations.
/// </para>
/// </remarks>
public sealed class Paq8hpBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Paq8hp";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "PAQ8hp (reduced model set)";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Reduced PAQ8hp-style subset: orders 0-6 + match model with context-selected mixer weight sets and one SSE stage (not the full PAQ8hp)";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) => Paq8hpCompressor.Compress(data);

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) => Paq8hpCompressor.Decompress(data);
}
