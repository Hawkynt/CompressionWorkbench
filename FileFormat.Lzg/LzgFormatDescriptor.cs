#pragma warning disable CS1591 // Missing XML comment

using Compression.Registry;

namespace FileFormat.Lzg;

public sealed class LzgFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Lzg";
  public string DisplayName => "LZG";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".lzg";
  public IReadOnlyList<string> Extensions => [".lzg"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x4C, 0x5A, 0x47], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzg1", "LZG1")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Marcus Geelnard's simple LZ77 with Huffman back-end";

  public void Decompress(Stream input, Stream output) => LzgStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => LzgStream.Compress(input, output);
}
