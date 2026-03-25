#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Squeeze;

public sealed class SqueezeFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Squeeze";
  public string DisplayName => "Squeeze";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".sqz";
  public IReadOnlyList<string> Extensions => [".sqz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("squeeze", "Squeeze")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "CP/M era Huffman squeezing (Richard Greenlaw, 1981)";

  public void Decompress(Stream input, Stream output) => SqueezeStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => SqueezeStream.Compress(input, output);
}
