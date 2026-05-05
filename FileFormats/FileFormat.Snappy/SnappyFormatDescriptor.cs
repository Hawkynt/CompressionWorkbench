#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Snappy;

public sealed class SnappyFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Snappy";
  public string DisplayName => "Snappy";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".sz";
  public IReadOnlyList<string> Extensions => [".sz", ".snappy"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0xFF, 0x06, 0x00, 0x00, 0x73, 0x4E, 0x61, 0x50], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("snappy", "Snappy")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Google's fast compressor, designed for speed over ratio";

  public void Decompress(Stream input, Stream output) {
    var r = new SnappyFrameReader(input);
    output.Write(r.Read());
  }
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var w = new SnappyFrameWriter(output);
    w.Write(ms.ToArray());
  }
}
