#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Balz;

public sealed class BalzFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Balz";
  public string DisplayName => "BALZ";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".balz";
  public IReadOnlyList<string> Extensions => [".balz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "ROLZ compressor by Ilya Muravyov with arithmetic coding";

  public void Decompress(Stream input, Stream output) => BalzStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => BalzStream.Compress(input, output);
}
