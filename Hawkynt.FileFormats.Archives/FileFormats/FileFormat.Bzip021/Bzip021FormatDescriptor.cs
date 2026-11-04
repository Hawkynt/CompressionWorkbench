using Compression.Core.Streams.Bzip021;
using Compression.Registry;

namespace FileFormat.Bzip021;

/// <summary>bzip 0.21 files (<c>.bz</c>, magic <c>BZ0</c>), see <see cref="Bzip021Codec"/>.</summary>
public sealed class Bzip021FormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <inheritdoc />
  public string Id => "Bzip021";
  /// <inheritdoc />
  public string DisplayName => "bzip 0.21";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Stream;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <inheritdoc />
  public string DefaultExtension => ".bz";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".bz"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("BZ0"u8.ToArray(), Confidence: 0.85)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("bzip", "BWT + arithmetic")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <inheritdoc />
  public string Description => "bzip 0.21: the 1996 arithmetic-coded block-sorting predecessor of bzip2";

  /// <inheritdoc />
  public void Decompress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(Bzip021Codec.Decode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }

  /// <inheritdoc />
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(Bzip021Codec.Encode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }
}
