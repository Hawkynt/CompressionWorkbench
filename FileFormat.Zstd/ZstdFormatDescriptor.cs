#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Zstd;

public sealed class ZstdFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Zstd";
  public string DisplayName => "Zstandard";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark | FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".zst";
  public IReadOnlyList<string> Extensions => [".zst", ".zstd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x28, 0xB5, 0x2F, 0xFD], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zstd", "Zstd", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) {
    using var ds = new ZstdStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }
  public void Compress(Stream input, Stream output) {
    using var cs = new ZstdStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }
  public Stream? WrapDecompress(Stream input) =>
    new ZstdStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
  public Stream? WrapCompress(Stream output) =>
    new ZstdStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
}
