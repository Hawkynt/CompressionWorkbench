#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Lzma;

public sealed class LzmaFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Lzma";
  public string DisplayName => "LZMA";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark | FormatCapabilities.SupportsOptimize;
  public string DefaultExtension => ".lzma";
  public IReadOnlyList<string> Extensions => [".lzma"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzma", "LZMA", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) => LzmaStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => LzmaStream.Compress(input, output);
}
