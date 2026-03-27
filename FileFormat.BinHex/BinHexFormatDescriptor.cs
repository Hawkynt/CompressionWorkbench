#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.BinHex;

public sealed class BinHexFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "BinHex";
  public string DisplayName => "BinHex";
  public FormatCategory Category => FormatCategory.Wrapper;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".hqx";
  public IReadOnlyList<string> Extensions => [".hqx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("binhex", "BinHex")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  public string Description => "Macintosh BinHex 4.0, binary-to-text with CRC";

  public void Decompress(Stream input, Stream output) {
    var result = BinHexReader.Decode(input);
    output.Write(result.DataFork);
  }
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    BinHexWriter.Write(output, "data", ms.ToArray());
  }
}
