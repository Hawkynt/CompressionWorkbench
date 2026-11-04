using Compression.Registry;

namespace FileFormat.SoftdiskCtx;

/// <summary>Softdisk Text Compressor files (<c>.ctx</c>), see <see cref="SoftdiskCtxCodec"/>.</summary>
public sealed class SoftdiskCtxFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <inheritdoc />
  public string Id => "SoftdiskCtx";
  /// <inheritdoc />
  public string DisplayName => "Softdisk Text Compressor";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Stream;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <inheritdoc />
  public string DefaultExtension => ".ctx";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".ctx"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [new(SoftdiskCtxCodec.Magic.ToArray(), Confidence: 0.95)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ctx", "Substitution")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <inheritdoc />
  public string Description => "Softdisk PC diskmagazine text: byte codes for common strings and letter pairs";

  /// <inheritdoc />
  public void Decompress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(SoftdiskCtxCodec.Decode(ms.GetBuffer().AsSpan(0, (int)ms.Length), out _));
  }

  /// <inheritdoc />
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(SoftdiskCtxCodec.Encode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }
}
