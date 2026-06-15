#pragma warning disable CS1591
namespace FileFormat.G711;

/// <summary>
/// Raw A-law (G.711) container: a headerless stream of A-law bytes. Decoded to 16-bit
/// LE PCM via <c>Codec.ALaw</c>; see <see cref="G711FormatDescriptorBase"/>.
/// </summary>
public sealed class G711AlawFormatDescriptor : G711FormatDescriptorBase {
  public override string Id => "G711Alaw";
  public override string DisplayName => "Raw A-law (G.711)";
  public override string DefaultExtension => ".al";
  public override IReadOnlyList<string> Extensions => [".al", ".alaw"];

  protected override string Variant => "A-law";
  protected override short[] Decode(byte[] companded) => Codec.ALaw.ALawCodec.Decode(companded);
  protected override byte[] Encode(short[] linear) => Codec.ALaw.ALawCodec.Encode(linear);
}
