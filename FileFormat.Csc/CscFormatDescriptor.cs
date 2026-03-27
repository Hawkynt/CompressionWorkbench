#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Csc;

public sealed class CscFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Csc";
  public string DisplayName => "CSC";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".csc";
  public IReadOnlyList<string> Extensions => [".csc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Context Stream Compression by Fu Siyuan; LZ77 with range coding";

  public void Decompress(Stream input, Stream output) => CscStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => CscStream.Compress(input, output);
}
