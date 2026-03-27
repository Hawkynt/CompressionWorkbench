#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Zling;

public sealed class ZlingFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Zling";
  public string DisplayName => "Zling";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".zling";
  public IReadOnlyList<string> Extensions => [".zling"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "ROLZ + Huffman block compressor by Zhang Li";

  public void Decompress(Stream input, Stream output) => ZlingStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => ZlingStream.Compress(input, output);
}
