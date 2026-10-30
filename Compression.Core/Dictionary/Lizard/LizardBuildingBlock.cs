using Compression.Registry;
using FileFormat.Lizard;

namespace Compression.Core.Dictionary.Lizard;

/// <summary>
/// Exposes Przemysław Skibiński's Lizard (formerly LZ5) as a benchmarkable building block.
/// </summary>
/// <remarks>
/// The block is the <see cref="LizardStream"/> codec, not a second one: it writes Lizard frames
/// that the reference <c>lizard</c> tool decodes, and reads the frames that tool writes.
/// </remarks>
public sealed class LizardBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lizard";
  /// <inheritdoc/>
  public string DisplayName => "Lizard";
  /// <inheritdoc/>
  public string Description => "Skibiński's Lizard (formerly LZ5) frames as the reference lizard tool writes them: LZ4-derived and LIZv1 parsing with optional Huffman streams";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    LizardStream.Compress(new MemoryStream(data.ToArray(), writable: false), output);
    return output.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    LizardStream.Decompress(new MemoryStream(data.ToArray(), writable: false), output);
    return output.ToArray();
  }
}
