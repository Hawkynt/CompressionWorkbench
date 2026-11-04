using Compression.Core.Streams.SfArk;
using Compression.Registry;

namespace FileFormat.SfArk;

/// <summary>sfArk V2 compressed SoundFonts (<c>.sfArk</c>), see <see cref="SfArkCodec"/>.</summary>
public sealed class SfArkFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <inheritdoc />
  public string Id => "SfArk";
  /// <inheritdoc />
  public string DisplayName => "sfArk";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Stream;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <inheritdoc />
  public string DefaultExtension => ".sfark";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".sfark"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(SfArkCodec.Signature.ToArray(), Offset: SfArkCodec.SignatureOffset, Confidence: 0.85)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("fast", "Fast")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <inheritdoc />
  public string Description => "sfArk V2: lossless SoundFont 2 packing, predicted samples with Rice-style residuals, zlib for the rest";

  /// <inheritdoc />
  public void Decompress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(SfArkCodec.Decode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }

  /// <inheritdoc />
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(SfArkCodec.Encode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }
}
