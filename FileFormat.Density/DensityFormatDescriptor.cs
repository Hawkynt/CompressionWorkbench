using Compression.Registry;

namespace FileFormat.Density;

public sealed class DensityFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Density";
  public string DisplayName => "Density";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".density";
  public IReadOnlyList<string> Extensions => [".density"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'D', (byte)'E', (byte)'N', (byte)'S'], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("chameleon", "Chameleon"),
    new("cheetah", "Cheetah"),
    new("lion", "Lion"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Chameleon/Cheetah/Lion algorithms, tuned for speed tiers";

  public void Decompress(Stream input, Stream output) => DensityStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => DensityStream.Compress(input, output);
}
