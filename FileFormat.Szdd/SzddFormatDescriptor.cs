#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Szdd;

public sealed class SzddFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Szdd";
  public string DisplayName => "SZDD";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".sz_";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x53, 0x5A, 0x44, 0x44], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzss", "LZSS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "MS-DOS COMPRESS.EXE LZ77, used by old Windows setup";

  public void Decompress(Stream input, Stream output) => SzddStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => SzddStream.Compress(input, output);
}
