#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Rzip;

public sealed class RzipFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Rzip";
  public string DisplayName => "Rzip";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".rz";
  public IReadOnlyList<string> Extensions => [".rz", ".rzip"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rzip", "Rzip")];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) => RzipStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => RzipStream.Compress(input, output);
}
