#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Ppmd;

public sealed class PpmdFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Ppmd";
  public string DisplayName => "PPMd";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".pmd";
  public IReadOnlyList<string> Extensions => [".pmd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x8F, 0xAF, 0xAC, 0x84], Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ppmd", "PPMd")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;
  public string Description => "PPMd standalone, Prediction by Partial Matching (Dmitry Shkarin)";

  public void Decompress(Stream input, Stream output) => PpmdStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => PpmdStream.Compress(input, output);
}
