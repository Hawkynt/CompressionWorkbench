using Codec.Gsm610;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class Gsm610Wav49Tests {
  [Test]
  public void PackRawFrames_MatchesLibgsmMsKnownAnswer() {
    var raw = Convert.FromHexString(
      "D236EC2DA2504F7C809B449D5006F8E5AD3923A802360DFD92EDF04203BF64BB62" +
      "D236EC6DE2F000A8D276B4A6B6C03066CD19B46CE0157545DD1A914039634A5A92");
    var expected = Convert.FromHexString(
      "88DDE11A8542EF16409BD28A82E61CDB692447056243FA2F598B4702F1BF642D85" +
      "D83DEE517808C4496B27D1DB0486D09E429BB606A2BACA6A4E4805C672125B49");

    var packed = Gsm610Wav49.PackRawFrames(raw);

    Assert.Multiple(() => {
      Assert.That(packed, Is.EqualTo(expected));
      Assert.That(Gsm610Wav49.UnpackToRawFrames(packed), Is.EqualTo(raw));
    });
  }

  [Test]
  public void EncodeDecode_PadsToWholeWav49Blocks() {
    var pcm = new short[Gsm610Codec.FrameSamples * 2 + 17];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * i / 8000.0) * 12_000);

    var encoded = Gsm610Wav49.Encode(pcm);
    var decoded = Gsm610Wav49.Decode(encoded);

    Assert.Multiple(() => {
      Assert.That(encoded.Length % Gsm610Wav49.BlockBytes, Is.Zero);
      Assert.That(decoded.Length, Is.EqualTo(Gsm610Wav49.SamplesPerBlock * 2));
      Assert.That(decoded.Any(static sample => sample != 0), Is.True);
    });
  }
}
