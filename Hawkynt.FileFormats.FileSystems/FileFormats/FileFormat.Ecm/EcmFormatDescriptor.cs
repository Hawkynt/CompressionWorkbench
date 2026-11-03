#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Ecm;

/// <summary>
/// Error Code Modeler (ECM): a CD image with every regenerable sync, header, EDC and ECC
/// field removed. Decoding rebuilds the original image byte for byte and verifies the
/// trailing checksum; encoding produces streams the reference <c>unecm</c> accepts.
/// </summary>
/// <remarks>Specification: <c>docs/ECM-ON-DISK.md</c>.</remarks>
public sealed class EcmFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  public string Id => "Ecm";
  public string DisplayName => "ECM";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".ecm";
  public IReadOnlyList<string> Extensions => [".ecm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("ECM\0"u8.ToArray(), Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ecm", "ECM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  public string Description => "Error Code Modeler: CD image with regenerable EDC/ECC, sync and headers stripped";

  public void Decompress(Stream input, Stream output) => EcmCodec.Decode(input, output);
  public void Compress(Stream input, Stream output) => EcmCodec.Encode(input, output);
}
