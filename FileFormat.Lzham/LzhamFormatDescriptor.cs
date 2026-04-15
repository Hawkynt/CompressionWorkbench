#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Lzham;

public sealed class LzhamFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Lzham";
  public string DisplayName => "LZHAM";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".lzham";
  public IReadOnlyList<string> Extensions => [".lzham"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x4C, 0x5A, 0x48, 0x4D], Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzham", "LZHAM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "LZHAM container, LZ77 + Huffman (Valve-inspired codec)";

  public void Decompress(Stream input, Stream output) => LzhamStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => LzhamStream.Compress(input, output);
}
