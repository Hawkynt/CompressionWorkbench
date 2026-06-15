#pragma warning disable CS1591
namespace FileFormat.G711;

/// <summary>
/// Raw µ-law (G.711) container: a headerless stream of µ-law bytes. Decoded to 16-bit
/// LE PCM via <c>Codec.MuLaw</c>; see <see cref="G711FormatDescriptorBase"/>.
/// </summary>
public sealed class G711UlawFormatDescriptor : G711FormatDescriptorBase {
  public override string Id => "G711Ulaw";
  public override string DisplayName => "Raw µ-law (G.711)";
  public override string DefaultExtension => ".ul";
  public override IReadOnlyList<string> Extensions => [".ul", ".ulaw"];

  protected override string Variant => "µ-law";
  protected override short[] Decode(byte[] companded) => Codec.MuLaw.MuLawCodec.Decode(companded);
  protected override byte[] Encode(short[] linear) => Codec.MuLaw.MuLawCodec.Encode(linear);
}
