#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Bzip2;

public sealed class Bzip2FormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Bzip2";
  public string DisplayName => "BZip2";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".bz2";
  public IReadOnlyList<string> Extensions => [".bz2", ".bzip2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x42, 0x5A, 0x68], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("bzip2", "BZip2")];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) {
    using var ds = new Bzip2Stream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }
  public void Compress(Stream input, Stream output) {
    using var cs = new Bzip2Stream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }
  public Stream? WrapDecompress(Stream input) =>
    new Bzip2Stream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
  public Stream? WrapCompress(Stream output) =>
    new Bzip2Stream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
}
