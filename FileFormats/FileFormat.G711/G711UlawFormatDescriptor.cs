#pragma warning disable CS1591
namespace FileFormat.G711;

/// <summary>
/// Raw µ-law (G.711) container: a headerless stream of µ-law bytes. Decoded to 16-bit
/// LE PCM via <c>Codec.MuLaw</c>; see <see cref="G711FormatDescriptorBase"/>.
/// </summary>
public sealed class G711UlawFormatDescriptor : G711FormatDescriptorBase {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public override string Id => "G711Ulaw";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public override string DisplayName => "Raw µ-law (G.711)";
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public override string DefaultExtension => ".ul";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public override IReadOnlyList<string> Extensions => [".ul", ".ulaw"];

  /// <summary>
  /// Gets the variant.
  /// </summary>
  protected override string Variant => "µ-law";
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  protected override short[] Decode(byte[] companded) => Codec.MuLaw.MuLawCodec.Decode(companded);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  protected override byte[] Encode(short[] linear) => Codec.MuLaw.MuLawCodec.Encode(linear);
}
