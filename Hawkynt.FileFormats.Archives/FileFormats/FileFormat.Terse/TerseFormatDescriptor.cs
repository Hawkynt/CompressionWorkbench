using Compression.Core.Dictionary.Terse;
using Compression.Registry;

namespace FileFormat.Terse;

/// <summary>
/// IBM TERSE files (<c>.tersed</c>): z/OS AMATERSE / TRSMAIN and TERSE/PC, PACK and SPACK.
/// </summary>
/// <remarks>
/// Native (PC) files carry the <c>01|07 89 69 A5</c> signature and are detected by it; host
/// files start with a version byte and record fields only, so they are found by extension.
/// Decoding yields the binary dataset content (variable records with their RDWs); writing
/// produces a host-header PACK file with the data as one fixed-length record.
/// </remarks>
public sealed class TerseFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <inheritdoc />
  public string Id => "Terse";
  /// <inheritdoc />
  public string DisplayName => "IBM TERSE";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Stream;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <inheritdoc />
  public string DefaultExtension => ".tersed";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".tersed", ".trs"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x01, 0x89, 0x69, 0xA5], Confidence: 0.90),
    new([0x07, 0x89, 0x69, 0xA5], Confidence: 0.90),
  ];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("pack", "PACK")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  /// <inheritdoc />
  public string Description => "IBM TERSE (z/OS AMATERSE, TERSE/PC): PACK LZW and SPACK pair-tree coding";

  /// <inheritdoc />
  public void Decompress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(TerseCodec.Decode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }

  /// <inheritdoc />
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(TerseCodec.EncodePack(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }
}
