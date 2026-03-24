#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.BriefLz;

public sealed class BriefLzFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "BriefLz";
  public string DisplayName => "BriefLZ";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".blz";
  public IReadOnlyList<string> Extensions => [".blz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x62, 0x6C, 0x7A, 0x1A], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("brieflz", "BriefLZ")];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) => BriefLzStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => BriefLzStream.Compress(input, output);
}
