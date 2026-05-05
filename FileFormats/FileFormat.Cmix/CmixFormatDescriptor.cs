#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Cmix;

public sealed class CmixFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Cmix";
  public string DisplayName => "cmix";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".cmix";
  public IReadOnlyList<string> Extensions => [".cmix"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;
  public string Description => "Neural context-mixing compressor by Byron Knoll";

  public void Decompress(Stream input, Stream output) => CmixStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => CmixStream.Compress(input, output);
}
