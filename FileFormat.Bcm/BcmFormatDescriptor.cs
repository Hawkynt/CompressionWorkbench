#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Bcm;

public sealed class BcmFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Bcm";
  public string DisplayName => "BCM";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".bcm";
  public IReadOnlyList<string> Extensions => [".bcm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x42, 0x43, 0x4D, 0x21], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;
  public string Description => "Ilya Muravyov's BWT + Context Mixing compressor";

  public void Decompress(Stream input, Stream output) => BcmStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => BcmStream.Compress(input, output);
}
