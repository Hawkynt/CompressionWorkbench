#pragma warning disable CS1591
namespace FileFormat.G711;

/// <summary>
/// Raw A-law (G.711) container: a headerless stream of A-law bytes. Decoded to 16-bit
/// LE PCM via <c>Codec.ALaw</c>; see <see cref="G711FormatDescriptorBase"/>.
/// </summary>
public sealed class G711AlawFormatDescriptor : G711FormatDescriptorBase {
    /// <summary>
  /// Gets the id.
  /// </summary>
public override string Id => "G711Alaw";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public override string DisplayName => "Raw A-law (G.711)";
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public override string DefaultExtension => ".al";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public override IReadOnlyList<string> Extensions => [".al", ".alaw"];

    /// <summary>
  /// Gets the variant.
  /// </summary>
protected override string Variant => "A-law";
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
protected override short[] Decode(byte[] companded) => Codec.ALaw.ALawCodec.Decode(companded);
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
protected override byte[] Encode(short[] linear) => Codec.ALaw.ALawCodec.Encode(linear);
}
