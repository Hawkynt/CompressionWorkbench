#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Xz;

public sealed class XzFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Xz";
  public string DisplayName => "XZ";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark | FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".xz";
  public IReadOnlyList<string> Extensions => [".xz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], Confidence: 0.98)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzma", "LZMA", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) {
    using var ds = new XzStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }
  public void Compress(Stream input, Stream output) {
    using var cs = new XzStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }
  public Stream? WrapDecompress(Stream input) =>
    new XzStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
  public Stream? WrapCompress(Stream output) =>
    new XzStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
}
