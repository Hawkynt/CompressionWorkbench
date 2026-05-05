#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Rzip;

public sealed class RzipFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Rzip";
  public string DisplayName => "Rzip";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".rz";
  public IReadOnlyList<string> Extensions => [".rz", ".rzip"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rzip", "Rzip")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Long-distance redundancy + bzip2, for large files";

  public void Decompress(Stream input, Stream output) => RzipStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => RzipStream.Compress(input, output);
}
