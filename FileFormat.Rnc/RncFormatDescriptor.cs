#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Rnc;

public sealed class RncFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Rnc";
  public string DisplayName => "RNC ProPack";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".rnc";
  public IReadOnlyList<string> Extensions => [".rnc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x52, 0x4E, 0x43, 0x01], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rnc", "RNC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Rob Northen Compression — Amiga/console game standard";

  public void Decompress(Stream input, Stream output) => RncStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => RncStream.Compress(input, output);
}
