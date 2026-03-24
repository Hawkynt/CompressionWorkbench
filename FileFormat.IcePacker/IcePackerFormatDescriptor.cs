#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.IcePacker;

public sealed class IcePackerFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "IcePacker";
  public string DisplayName => "ICE Packer";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".ice";
  public IReadOnlyList<string> Extensions => [".ice"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ice", "ICE")];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) => IcePackerStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => IcePackerStream.Compress(input, output);
}
