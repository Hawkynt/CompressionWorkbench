#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Brotli;

public sealed class BrotliFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Brotli";
  public string DisplayName => "Brotli";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark | FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".br";
  public IReadOnlyList<string> Extensions => [".br"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("brotli", "Brotli", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Google's modern LZ77+Huffman with static dictionary, great for web content";

  public void Decompress(Stream input, Stream output) {
    var d = BrotliStream.Decompress(input);
    output.Write(d);
  }
  public void Compress(Stream input, Stream output) {
    var c = BrotliStream.Compress(input);
    output.Write(c);
  }
}
