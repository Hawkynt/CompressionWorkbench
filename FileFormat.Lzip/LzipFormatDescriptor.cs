#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Lzip;

public sealed class LzipFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Lzip";
  public string DisplayName => "Lzip";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".lz";
  public IReadOnlyList<string> Extensions => [".lz", ".lzip"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x4C, 0x5A, 0x49, 0x50], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzma", "LZMA", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "LZMA with CRC32, designed for long-term archival";

  public void Decompress(Stream input, Stream output) => LzipStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => LzipStream.Compress(input, output);
}
