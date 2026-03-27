#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.MacBinary;

public sealed class MacBinaryFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "MacBinary";
  public string DisplayName => "MacBinary";
  public FormatCategory Category => FormatCategory.Wrapper;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".bin";
  public IReadOnlyList<string> Extensions => [".bin", ".macbin"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("macbinary", "MacBinary")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  public string Description => "Macintosh resource+data fork container encoding";

  public void Decompress(Stream input, Stream output) {
    var data = MacBinaryReader.ReadDataFork(input);
    output.Write(data);
  }
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    MacBinaryWriter.Write(output, "data", ms.ToArray());
  }
}
