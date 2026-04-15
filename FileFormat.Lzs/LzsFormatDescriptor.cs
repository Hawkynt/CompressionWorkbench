#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Lzs;

public sealed class LzsFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Lzs";
  public string DisplayName => "LZS";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".lzs";
  public IReadOnlyList<string> Extensions => [".lzs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x1F, 0x9D, 0x8C, 0x53], Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzs", "LZS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Stac LZS (RFC 1967), LZSS variant for networking";

  public void Decompress(Stream input, Stream output) => LzsStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => LzsStream.Compress(input, output);
}
