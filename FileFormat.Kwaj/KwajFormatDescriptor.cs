#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Kwaj;

public sealed class KwajFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Kwaj";
  public string DisplayName => "KWAJ";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".kwaj";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x4B, 0x57, 0x41, 0x4A], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("kwaj", "KWAJ")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "MS-DOS COMPRESS.EXE variant with extended header";

  public void Decompress(Stream input, Stream output) => KwajStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => KwajStream.Compress(input, output);
}
