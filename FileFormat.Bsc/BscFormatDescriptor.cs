#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Bsc;

public sealed class BscFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Bsc";
  public string DisplayName => "BSC";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".bsc";
  public IReadOnlyList<string> Extensions => [".bsc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x62, 0x73, 0x63, 0x31], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  public string Description => "Ilya Grebnov's libbsc block sorting compressor (BWT+MTF+RLE)";

  public void Compress(Stream input, Stream output) => BscStream.Compress(input, output);
  public void Decompress(Stream input, Stream output) => BscStream.Decompress(input, output);
}
