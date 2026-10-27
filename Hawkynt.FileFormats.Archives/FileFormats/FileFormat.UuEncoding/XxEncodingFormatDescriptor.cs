using Compression.Registry;

namespace FileFormat.UuEncoding;

/// <summary>
/// XXencode binary-to-text wrapper (<c>.xx</c> / <c>.xxe</c>).
/// </summary>
/// <remarks>
/// Detection is by extension only: the <c>begin </c> header is shared with
/// uuencode, so a magic signature would shadow <see cref="UuEncodingFormatDescriptor"/>.
/// </remarks>
public sealed class XxEncodingFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <inheritdoc />
  public string Id => "XxEncoding";
  /// <inheritdoc />
  public string DisplayName => "XXEncoding";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Wrapper;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <inheritdoc />
  public string DefaultExtension => ".xxe";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".xxe", ".xx"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xxencode", "XXEncode")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  /// <inheritdoc />
  public string Description => "XXencode, the uuencode envelope with an EBCDIC-safe +-0-9A-Za-z alphabet";

  /// <inheritdoc />
  public void Decompress(Stream input, Stream output) {
    var (_, _, data) = XxEncoder.Decode(input);
    output.Write(data);
  }

  /// <inheritdoc />
  public void Compress(Stream input, Stream output) => XxEncoder.Encode(input, output, "data");
}
