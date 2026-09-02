using Compression.Registry;

namespace FileFormat.UuEncoding;

/// <summary>
/// Describes uu encoding format.
/// </summary>
public sealed class UuEncodingFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "UuEncoding";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "UUEncoding";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Wrapper;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".uue";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".uue", ".uu"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("uuencode", "UUEncode")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Encoding;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Unix-to-Unix encoding, binary-to-text for email";

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) {
    var (_, _, data) = UuEncoder.Decode(input);
    output.Write(data);
  }

    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    ms.Position = 0;
    UuEncoder.Encode(ms, output, "data");
  }
}
