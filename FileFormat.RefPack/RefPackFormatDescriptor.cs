#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.RefPack;

public sealed class RefPackFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "RefPack";
  public string DisplayName => "RefPack/QFS";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".qfs";
  public IReadOnlyList<string> Extensions => [".qfs", ".refpack"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("refpack", "RefPack")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "EA Games' LZ77 variant for game assets";

  public void Decompress(Stream input, Stream output) => RefPackStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => RefPackStream.Compress(input, output);
}
