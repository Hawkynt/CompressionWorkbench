using Compression.Registry;
using FileFormat.Lzfse;

namespace Compression.Core.Dictionary.Lzfse;

/// <summary>
/// Exposes Apple's LZFSE as a benchmarkable building block.
/// </summary>
/// <remarks>
/// The block is the codec <see cref="LzfseStream"/> implements, not a second one: compressing
/// yields a complete LZFSE stream (<c>bvx2</c>, <c>bvxn</c> or <c>bvx-</c> blocks closed by
/// <c>bvx$</c>) that Apple's <c>lzfse</c> decodes, and decompressing reads the streams Apple's
/// encoder writes.
/// </remarks>
public sealed class LzfseBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzfse";
  /// <inheritdoc/>
  public string DisplayName => "LZFSE";
  /// <inheritdoc/>
  public string Description => "Apple's LZFSE: LZ77 with finite-state entropy (tANS) coded literals and matches, as Apple's lzfse writes and reads it";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray(), writable: false);
    using var output = new MemoryStream();
    LzfseStream.Compress(input, output);
    return output.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray(), writable: false);
    using var output = new MemoryStream();
    LzfseStream.Decompress(input, output);
    return output.ToArray();
  }
}
