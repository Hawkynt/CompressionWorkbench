#pragma warning disable CS1591 // Missing XML comment

using Compression.Registry;

namespace FileFormat.Lz4;

public sealed class Lz4FormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Lz4";
  public string DisplayName => "LZ4";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark | FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".lz4";
  public IReadOnlyList<string> Extensions => [".lz4"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x04, 0x22, 0x4D, 0x18], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lz4", "LZ4", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Extremely fast LZ77 with byte-aligned tokens, optimized for speed";

  public void Decompress(Stream input, Stream output) {
    var r = new Lz4FrameReader(input);
    output.Write(r.Read());
  }

  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var w = new Lz4FrameWriter(output);
    w.Write(ms.ToArray());
  }

  public void CompressOptimal(Stream input, Stream output) => Compress(input, output);
}
