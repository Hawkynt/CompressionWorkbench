#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PowerPacker;

public sealed class PowerPackerFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "PowerPacker";
  public string DisplayName => "PowerPacker";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".pp";
  public IReadOnlyList<string> Extensions => [".pp", ".pp20"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("powerpacker", "PowerPacker")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Amiga PowerPacker LZ77, classic retro format";

  public void Decompress(Stream input, Stream output) => PowerPackerStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => PowerPackerStream.Compress(input, output);
}
