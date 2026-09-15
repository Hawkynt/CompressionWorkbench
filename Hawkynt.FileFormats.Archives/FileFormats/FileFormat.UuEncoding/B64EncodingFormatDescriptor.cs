using Compression.Registry;

namespace FileFormat.UuEncoding;

/// <summary>
/// libarchive/GNU-style <c>begin-base64</c> binary-to-text wrapper.
/// </summary>
/// <remarks>
/// This is the Base64 variant of the uuencode envelope, not bare RFC 4648 Base64:
/// it carries a mode/name header and an <c>====</c> terminator. libarchive exposes
/// it as the <c>b64encode</c> write filter and reads it through its uu filter.
/// </remarks>
public sealed class B64EncodingFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Name", "Embedded name", FormatOptionKind.String, "-",
      Description: "Name written in the begin-base64 header.", IsOptimizationAxis: false),
    new("Mode", "File mode", FormatOptionKind.String, "644",
      Description: "Three-digit octal mode written in the begin-base64 header.", IsOptimizationAxis: false),
  ];

  /// <inheritdoc />
  public string Id => "B64Encoding";

  /// <inheritdoc />
  public string DisplayName => "Base64 (uuencode wrapper)";

  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Wrapper;

  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;

  /// <inheritdoc />
  public string DefaultExtension => ".b64";

  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".b64", ".base64"];

  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("begin-base64 "u8.ToArray(), Confidence: 0.99),
  ];

  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("b64encode", "Base64 uuencode wrapper")];

  /// <inheritdoc />
  public string? TarCompressionFormatId => null;

  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;

  /// <inheritdoc />
  public string Description => "libarchive-compatible begin-base64/==== binary-to-text wrapper";

  /// <inheritdoc />
  public void Decompress(Stream input, Stream output) {
    var (_, _, data) = UuEncoder.Decode(input);
    output.Write(data);
  }

  /// <inheritdoc />
  public void Compress(Stream input, Stream output)
    => UuEncoder.EncodeBase64(input, output);

  /// <inheritdoc />
  public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var name = options.GetOption("Name", "-");
    var mode = ParseOctalMode(options.GetOption("Mode", "644"));
    UuEncoder.EncodeBase64(input, output, name, mode);
  }

  private static int ParseOctalMode(string text) {
    try {
      var value = Convert.ToInt32(text, 8);
      if (value is < 0 or > 0777)
        throw new ArgumentOutOfRangeException(nameof(text), "Base64 wrapper mode must fit 0000..0777 octal.");
      return value;
    } catch (FormatException ex) {
      throw new ArgumentException("Base64 wrapper mode must contain octal digits only.", nameof(text), ex);
    } catch (OverflowException ex) {
      throw new ArgumentOutOfRangeException(nameof(text), text, "Base64 wrapper mode is too large.", ex);
    }
  }
}
