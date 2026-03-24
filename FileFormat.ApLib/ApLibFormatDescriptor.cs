#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.ApLib;

public sealed class ApLibFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "ApLib";
  public string DisplayName => "aPLib";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".aplib";
  public IReadOnlyList<string> Extensions => [".aplib"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x41, 0x50, 0x33, 0x32], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("aplib", "aPLib")];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) => ApLibStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => ApLibStream.Compress(input, output);
}
