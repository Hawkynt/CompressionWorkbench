#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Freeze;

public sealed class FreezeFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Freeze";
  public string DisplayName => "Freeze";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".f";
  public IReadOnlyList<string> Extensions => [".f", ".freeze"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x1F, 0x9E], Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("freeze", "Freeze")];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) => FreezeStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => FreezeStream.Compress(input, output);
}
