#pragma warning disable CS1591 // Missing XML comment

using Compression.Registry;

namespace FileFormat.PackBits;

public sealed class PackBitsFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "PackBits";
  public string DisplayName => "PackBits";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".packbits";
  public IReadOnlyList<string> Extensions => [".packbits"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x50, 0x4B, 0x42, 0x54], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("packbits", "PackBits")];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) => PackBitsStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => PackBitsStream.Compress(input, output);
}
