using Compression.Core.Entropy.Grzip;
using Compression.Registry;

namespace FileFormat.Grzip;

/// <summary>GRZipII / grzip compressed files (<c>.grz</c>), see <see cref="GrzipCodec"/>.</summary>
public sealed class GrzipFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <inheritdoc />
  public string Id => "Grzip";
  /// <inheritdoc />
  public string DisplayName => "GRZipII";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Stream;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <inheritdoc />
  public string DefaultExtension => ".grz";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".grz"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [new(GrzipCodec.Signature.ToArray(), Confidence: 0.98)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("grzip", "LZP+BWT+MTF")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <inheritdoc />
  public string Description => "GRZipII block-sorting compressor: LZP, BWT or ST4, MTF or WFC ranking, range coding";

  /// <inheritdoc />
  public void Decompress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(GrzipCodec.Decode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }

  /// <inheritdoc />
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(GrzipCodec.Encode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }
}
