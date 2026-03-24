using Compression.Registry;

namespace FileFormat.YEnc;

public sealed class YEncFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "YEnc";
  public string DisplayName => "yEnc";
  public FormatCategory Category => FormatCategory.Wrapper;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".yenc";
  public IReadOnlyList<string> Extensions => [".yenc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("yenc", "yEnc")];
  public string? TarCompressionFormatId => null;

  public void Decompress(Stream input, Stream output) {
    var (_, _, _, data) = YEncDecoder.Decode(input);
    output.Write(data);
  }
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    YEncEncoder.Encode(output, "data", ms.ToArray());
  }
}
