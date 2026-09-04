#pragma warning disable CS1591
using Codec.Gsm610;

namespace Compression.Tests.Codecs.Gsm610;

/// <summary>
/// Bit-exactness of GSM 06.10 against the RPE-LTP reference implementation by Jutta Degener
/// and Carsten Bormann (libgsm, built with <c>SASR</c> and without <c>USE_FLOAT_MUL</c>,
/// <c>LTP_CUT</c> or <c>WAV49</c>) — the implementation ffmpeg, sox and toast all share.
/// <para>
/// Both directions are pinned, and the encoder vector is the one that carries the weight: an
/// encoder that merely sounds right still fails here, because the offset-compensation filter,
/// the Schur recursion, the LAR quantisation, the LTP lag and gain, the RPE grid and the APCM
/// exponent must each land on the reference's integer for the packed frame to match. Three
/// frames are encoded in one call, so the vector also pins the state carried between frames.
/// </para>
/// <para>
/// The input is generated from integers alone. A sine would make the vector depend on the
/// platform's transcendental rounding, which is not a property this codec should assert.
/// </para>
/// </summary>
[TestFixture]
public class Gsm610ReferenceVectorTests {

  private const string ReferenceFrames =
    "D7DF92615A501886DB6E38EC73D75B6D1239237192D7848DB8D5D0EE306A76CDB7D7E092DD6273AEB5B325F193E3F159A3EEC61570B126ECB6CF6F71" +
    "8DF89E39555AD81EA2DD59E3CE9743E178B371CDA914E11670EE6FB5547A7BFDE1D02A687D46F0";

  private const string ReferenceDecodeOfFirstTwoFrames =
    "00BA00C3B0CA68C688CC50D3C8CF60D590DB78D6D0DBC0E0F8DA38DF88E330DD10E128E580DE38E220E660F390F460F658EEE0F0F0F2E0FD10FE50FE" +
    "70F508F748F88016C813B011901B001A681770144814481140258021C81D5032682EB029303C4838D8315043883E503700488042D83A00BA78C370C7" +
    "A0C0A0BD18C3A0CA48CA10D340C7A8CD40D368D590D9C8DD48E040E340E7F0E7C0EA78ED58F2F8F320F6D8F718F9B000D80028011009C8081008180D" +
    "A00C400BA017D8158013881570142812C822C01F281C102DB0295025A8342031882B1041183C50352843683E403708ACF0B600BC58C068BC38C208CB" +
    "18CB30D400D6D0D910DE08E620DEE0E1A0E288DDC8E080E720EF18F040EF60EDD0EF20FCA0FA68FB4800200680052005C00AD009B010E010C00F5816" +
    "901CF819301F5825E0215026582B7827082BB032782DB0392834D02E103F7038283288489041F03910B880C0A8C6A8C700C888C788C620CF98D7A0CF" +
    "D8D308DA68E000D950DD08EB50E5D0E858E370EB68EF98F560F2E0F308FC38FBF0FB48FE6803B8034003D8022808F80FE80D9019281A20177019B023" +
    "C01F982E102CE8258836E82CD8261836F840C839E84C1041603968489845903CF8AF68B6F8C298C5C0C780C290CAF0C818D2A0D410CF78D6E0DFF8E0" +
    "78E598E3C8E4D0E978E6E8EAC0EDB0EE08F0F8F118FE98FEA8FE3003D8044805100D100BA00B30166013F815401D30193018181DE018B01C602D4027" +
    "7029082D2827882AD833382C0030D83C083508368842C03988AEC0B8B8C270C630C560BFF8C778C4E0CDC0D0F8D268DA28E318DD10E270E1A0E9B0ED" +
    "F8E868E9E0ECA8EED8F3D8F4280078FC00FD2802D807B007C00E900B580C3817800F1812D019D015";

  /// <summary>
  /// A deterministic sawtooth plus a linear-congruential dither, wide enough to drive the
  /// quantisers through most of their range without clipping.
  /// </summary>
  private static short[] ReferenceSignal(int count) {
    var pcm = new short[count];
    var state = 0x2545F491u;
    for (var i = 0; i < count; ++i) {
      state = state * 1664525u + 1013904223u;
      var noise = (int)(state >> 20) - 2048;
      var tone = i * 71 % 4001 - 2000;
      pcm[i] = (short)Math.Clamp(tone * 9 + noise, short.MinValue, short.MaxValue);
    }
    return pcm;
  }

  [Test]
  public void EncodeRaw_IsBitExactWithTheReferenceEncoder() {
    var encoded = Gsm610Codec.EncodeRaw(ReferenceSignal(Gsm610Codec.FrameSamples * 3));

    Assert.That(Convert.ToHexString(encoded), Is.EqualTo(ReferenceFrames));
  }

  [Test]
  public void DecodeRaw_IsBitExactWithTheReferenceDecoder() {
    var frames = Convert.FromHexString(ReferenceFrames)[..(Gsm610Codec.FrameBytes * 2)];

    var decoded = Gsm610Codec.DecodeRaw(frames);
    var bytes = new byte[decoded.Length * 2];
    Buffer.BlockCopy(decoded, 0, bytes, 0, bytes.Length);

    Assert.Multiple(() => {
      Assert.That(decoded.Length, Is.EqualTo(Gsm610Codec.FrameSamples * 2));
      Assert.That(Convert.ToHexString(bytes), Is.EqualTo(ReferenceDecodeOfFirstTwoFrames));
    });
  }
}
