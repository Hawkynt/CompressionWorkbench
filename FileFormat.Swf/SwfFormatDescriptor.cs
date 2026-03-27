#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Swf;

public sealed class SwfFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Swf";
  public string DisplayName => "SWF";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".swf";
  public IReadOnlyList<string> Extensions => [".swf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("CWS"u8.ToArray(), Confidence: 0.85),
    new("ZWS"u8.ToArray(), Confidence: 0.85),
    new("FWS"u8.ToArray(), Confidence: 0.70),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("swf", "SWF")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Other;
  public string Description => "Adobe Flash SWF (compressed)";

  public void Decompress(Stream input, Stream output) => SwfStream.Decompress(input, output);
  public void Compress(Stream input, Stream output) => SwfStream.Compress(input, output);
}
