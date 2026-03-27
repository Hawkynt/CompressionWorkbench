#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Crunch;

public sealed class CrunchFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Crunch";
  public string DisplayName => "CP/M Crunch";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".cru";
  public IReadOnlyList<string> Extensions => [".cru"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x76, 0xFE], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzw", "LZW (9-12 bit)")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "CP/M Crunch, LZW 9-12 bit MSB-first with original filename header";

  public void Decompress(Stream input, Stream output) {
    using var ds = new CrunchStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }

  public void Compress(Stream input, Stream output) {
    using var cs = new CrunchStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }

  public Stream? WrapDecompress(Stream input) =>
    new CrunchStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);

  public Stream? WrapCompress(Stream output) =>
    new CrunchStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
}
