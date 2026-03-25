#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Yaz0;

public sealed class Yaz0FormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Yaz0";
  public string DisplayName => "Yaz0";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".yaz0";
  public IReadOnlyList<string> Extensions => [".yaz0", ".szs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x59, 0x61, 0x7A, 0x30], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("yaz0", "Yaz0")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Nintendo LZ77 format used in GameCube/Wii/Switch";

  public void Decompress(Stream input, Stream output) => Yaz0Stream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => Yaz0Stream.Compress(input, output);
}
