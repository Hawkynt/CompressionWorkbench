using Compression.Core.Entropy.UnixPack;
using Compression.Registry;

namespace FileFormat.UnixPack;

/// <summary>
/// System V <c>pack</c> files (<c>.z</c>, magic <c>1F 1E</c>), see <see cref="UnixPackBuildingBlock"/>.
/// </summary>
/// <remarks>
/// Detected by magic only: the lower-case <c>.z</c> extension is also what Unix
/// <c>compress</c> files are found under, and that descriptor owns it.
/// </remarks>
public sealed class UnixPackFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <inheritdoc />
  public string Id => "UnixPack";
  /// <inheritdoc />
  public string DisplayName => "pack (.z)";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Stream;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <inheritdoc />
  public string DefaultExtension => ".z";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x1F, 0x1E], Confidence: 0.80)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("pack", "Huffman")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <inheritdoc />
  public string Description => "System V pack: one static Huffman code over the whole file";

  /// <inheritdoc />
  public void Decompress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(UnixPackBuildingBlock.Decode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }

  /// <inheritdoc />
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(UnixPackBuildingBlock.Encode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }
}
