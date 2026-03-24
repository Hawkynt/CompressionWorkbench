#pragma warning disable CS1591 // Missing XML comment

using Compression.Registry;

namespace FileFormat.Gzip;

public sealed class GzipFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Gzip";
  public string DisplayName => "GZIP";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark | FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".gz";
  public IReadOnlyList<string> Extensions => [".gz", ".gzip"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x1F, 0x8B], Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) {
    using var ds = new GzipStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }

  public void Compress(Stream input, Stream output) {
    using var cs = new GzipStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }

  public void CompressOptimal(Stream input, Stream output) {
    using var cs = new GzipStream(output, Compression.Core.Streams.CompressionStreamMode.Compress,
      Compression.Core.Deflate.DeflateCompressionLevel.Maximum, leaveOpen: true);
    input.CopyTo(cs);
  }

  public Stream? WrapDecompress(Stream input) =>
    new GzipStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);

  public Stream? WrapCompress(Stream output) =>
    new GzipStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
}
