using Compression.Registry;

namespace Compression.Core.Dictionary.Lzturbo;

/// <summary>
/// Exposes an LZTURBO-inspired codec as a benchmarkable building block.
/// </summary>
/// <remarks>
/// LZTURBO's real bitstream is closed-source and undocumented; this building
/// block reproduces only the documented outer block shape (magic, method byte,
/// original/compressed length) plus an original fast-LZ front end, with no
/// entropy back end. See <see cref="LzturboConstants"/> for the full scope
/// statement.
/// </remarks>
public sealed class LzturboBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzturbo";
  /// <inheritdoc/>
  public string DisplayName => "LZTURBO";
  /// <inheritdoc/>
  public string Description => "Fast hash-matched LZ77 front end in a magic/method/length block, modelling LZTURBO's documented outer shape (entropy back end not reproduced: undocumented)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) =>
    LzturboCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) =>
    LzturboDecompressor.Decompress(data);
}
