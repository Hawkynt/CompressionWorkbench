#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Zlib;

public sealed class ZlibFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Zlib";
  public string DisplayName => "Zlib";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize;
  public string DefaultExtension => ".zlib";
  public IReadOnlyList<string> Extensions => [".zlib"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Deflate with Adler32 checksum, foundational compression library";

  public void Decompress(Stream input, Stream output) => ZlibStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => ZlibStream.Compress(input, output);
  public void CompressOptimal(Stream input, Stream output) =>
    ZlibStream.Compress(input, output, Compression.Core.Deflate.DeflateCompressionLevel.Maximum);
}
