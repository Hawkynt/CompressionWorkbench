#pragma warning disable CS1591

using Compression.Registry;

namespace FileFormat.Flac;

/// <summary>
/// Format descriptor and stream operations for the FLAC (Free Lossless Audio Codec) format.
/// </summary>
public sealed class FlacFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Flac";
  public string DisplayName => "FLAC";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".flac";
  public IReadOnlyList<string> Extensions => [".flac", ".fla"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x66, 0x4C, 0x61, 0x43], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("flac", "FLAC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;
  public string Description => "Free Lossless Audio Codec, entropy-coded residuals + LPC prediction";

  public void Decompress(Stream input, Stream output) => FlacReader.Decompress(input, output);
  public void Compress(Stream input, Stream output) => FlacWriter.Compress(input, output);
}
