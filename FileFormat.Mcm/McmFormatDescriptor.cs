#pragma warning disable CS1591

using Compression.Registry;

namespace FileFormat.Mcm;

public sealed class McmFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Mcm";
  public string DisplayName => "MCM";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".mcm";
  public IReadOnlyList<string> Extensions => [".mcm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x4D, 0x43, 0x4D, 0x41, 0x52, 0x43, 0x48, 0x49, 0x56, 0x45], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("mcm", "MCM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;
  public string Description => "Mathieu Chartier's Multi-Context Mixing compressor";

  public void Decompress(Stream input, Stream output) => McmStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => McmStream.Compress(input, output);
}
