using Compression.Registry;
using FileFormat.BriefLz;

namespace Compression.Core.Dictionary.BriefLz;

/// <summary>
/// Exposes Jørgen Ibsen's BriefLZ as a benchmarkable building block.
/// </summary>
/// <remarks>
/// The block is the <see cref="BriefLzStream"/> codec, not a second one: it writes the
/// <c>blzpack</c> stream that BriefLZ's own example tool reads, and reads the streams that tool
/// writes.
/// </remarks>
public sealed class BriefLzBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BriefLz";
  /// <inheritdoc/>
  public string DisplayName => "BriefLZ";
  /// <inheritdoc/>
  public string Description => "Ibsen's BriefLZ as blzpack writes it: byte-oriented LZ77 with interleaved 16-bit tag words and gamma-coded lengths and offsets";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    BriefLzStream.Compress(new MemoryStream(data.ToArray(), writable: false), output);
    return output.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    BriefLzStream.Decompress(new MemoryStream(data.ToArray(), writable: false), output);
    return output.ToArray();
  }
}
