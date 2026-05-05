#pragma warning disable CS1591

using Compression.Registry;

namespace FileFormat.Lizard;

public sealed class LizardFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Lizard";
  public string DisplayName => "Lizard (LZ5)";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".liz";
  public IReadOnlyList<string> Extensions => [".liz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x06, 0x22, 0x4D, 0x18], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lizard", "Lizard")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "LZ4-derived fast LZ77 compressor by Przemyslaw Skibinski (formerly LZ5)";

  public void Decompress(Stream input, Stream output) => LizardStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => LizardStream.Compress(input, output);
}
