#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Lzfse;

public sealed class LzfseFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Lzfse";
  public string DisplayName => "LZFSE";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".lzfse";
  public IReadOnlyList<string> Extensions => [".lzfse"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x62, 0x76, 0x78, 0x31], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzfse", "LZFSE")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Apple's entropy-coded LZ77 for iOS/macOS";

  public void Decompress(Stream input, Stream output) => LzfseStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => LzfseStream.Compress(input, output);
}
