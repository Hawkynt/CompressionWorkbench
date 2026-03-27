#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Paq8;

public sealed class Paq8FormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Paq8";
  public string DisplayName => "PAQ8";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".paq8l";
  public IReadOnlyList<string> Extensions => [".paq8l", ".paq8"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x70, 0x61, 0x71, 0x38, 0x6C, 0x20, 0x2D], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;
  public string Description => "PAQ8 context-mixing compressor by Matt Mahoney";

  public void Compress(Stream input, Stream output) => Paq8Stream.Compress(input, output);
  public void Decompress(Stream input, Stream output) => Paq8Stream.Decompress(input, output);
}
