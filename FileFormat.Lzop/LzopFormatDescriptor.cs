#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Lzop;

public sealed class LzopFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Lzop";
  public string DisplayName => "LZOP";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark | FormatCapabilities.SupportsOptimize;
  public string DefaultExtension => ".lzo";
  public IReadOnlyList<string> Extensions => [".lzo"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x89, 0x4C, 0x5A, 0x4F], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzo", "LZO", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "LZO-based, optimized for real-time compression speed";

  public void Decompress(Stream input, Stream output) {
    var r = new LzopReader(input);
    output.Write(r.Decompress());
  }
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(LzopWriter.Compress(ms.ToArray()));
  }
}
