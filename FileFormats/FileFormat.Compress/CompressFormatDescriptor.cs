#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Compress;

public sealed class CompressFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Compress";
  public string DisplayName => "Unix Compress";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize;
  public string DefaultExtension => ".z";
  public IReadOnlyList<string> Extensions => [".z"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x1F, 0x9D], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzw", "LZW", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Unix compress, LZW adaptive dictionary";

  public void Decompress(Stream input, Stream output) {
    using var ds = new CompressStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }
  public void Compress(Stream input, Stream output) {
    using var cs = new CompressStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }
  public Stream? WrapDecompress(Stream input) =>
    new CompressStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
  public Stream? WrapCompress(Stream output) =>
    new CompressStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
}
