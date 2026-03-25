using Compression.Registry;

namespace FileFormat.UuEncoding;

public sealed class UuEncodingFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "UuEncoding";
  public string DisplayName => "UUEncoding";
  public FormatCategory Category => FormatCategory.Wrapper;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark;
  public string DefaultExtension => ".uue";
  public IReadOnlyList<string> Extensions => [".uue", ".uu"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("uuencode", "UUEncode")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  public string Description => "Unix-to-Unix encoding, binary-to-text for email";

  public void Decompress(Stream input, Stream output) {
    var (_, _, data) = UuEncoder.Decode(input);
    output.Write(data);
  }

  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    ms.Position = 0;
    UuEncoder.Encode(ms, output, "data");
  }
}
