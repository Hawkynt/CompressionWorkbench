#pragma warning disable CS1591
using Codec.Atrac1;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class Atrac1EncoderTests {

  private static readonly int[] BfuCounts = [20, 28, 32, 36, 40, 44, 48, 52];

  [TestCase(1)]
  [TestCase(2)]
  public void SilentFrame_EncodesToOneDecodableSoundUnitPerChannel(int channels) {
    var encoder = new Atrac1Encoder(channels);
    var encoded = encoder.EncodeFrame(new short[Atrac1Codec.SamplesPerFrame * channels]);

    Assert.Multiple(() => {
      Assert.That(encoded.Length, Is.EqualTo(Atrac1Codec.SoundUnitSize * channels));
      Assert.That(encoded[0], Is.EqualTo(0xAC), "window-mask 0 means long windows in all three QMF bands");
    });

    var decoded = new Atrac1Codec(channels).Decode(encoded);
    Assert.That(decoded.All(static sample => sample == 0), Is.True);
  }

  [Test]
  public void EveryWindowMaskAndBfuSelector_ProducesAValidSilentSoundUnit() {
    foreach (var windowMask in Enumerable.Range(0, 8))
      foreach (var bfuCount in BfuCounts) {
        var encoded = new Atrac1Encoder(1, new Atrac1EncoderOptions {
          WindowMask = windowMask,
          BfuCount = bfuCount,
        }).EncodeFrame(new short[Atrac1Codec.SamplesPerFrame]);

        Assert.That(encoded.Length, Is.EqualTo(Atrac1Codec.SoundUnitSize), $"mask {windowMask}, BFUs {bfuCount}");
        Assert.That(() => new Atrac1Codec(1).Decode(encoded), Throws.Nothing, $"mask {windowMask}, BFUs {bfuCount}");
      }
  }

  [Test]
  public void EncodeStream_PadsOnlyTheFinalPartialFrame() {
    var input = new short[Atrac1Codec.SamplesPerFrame + 1];
    input[^1] = 1000;

    var encoded = new Atrac1Encoder(1).EncodeStream(input);
    var decoded = new Atrac1Codec(1).DecodeStream(encoded);

    Assert.Multiple(() => {
      Assert.That(encoded.Length, Is.EqualTo(2 * Atrac1Codec.SoundUnitSize));
      Assert.That(decoded.Length, Is.EqualTo(2 * Atrac1Codec.SamplesPerFrame));
    });
  }

  [Test]
  public void Tone_ProducesNonZeroMantissasAndDecodesToNonSilence() {
    const int frames = 4;
    var pcm = new short[frames * Atrac1Codec.SamplesPerFrame];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)Math.Round(Math.Sin(2 * Math.PI * 1000 * i / 44100.0) * 12000);

    var encoded = new Atrac1Encoder(1).EncodeStream(pcm);
    var decoded = new Atrac1Codec(1).DecodeStream(encoded);

    Assert.Multiple(() => {
      Assert.That(encoded.Skip(2).Any(static value => value != 0), Is.True);
      Assert.That(decoded.Any(static value => value != 0), Is.True);
      Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
    });
  }

  [Test]
  public void InvalidEncoderGeometry_IsRejected() {
    Assert.Multiple(() => {
      Assert.That(() => new Atrac1Encoder(0), Throws.InstanceOf<ArgumentOutOfRangeException>());
      Assert.That(() => new Atrac1Encoder(3), Throws.InstanceOf<ArgumentOutOfRangeException>());
      Assert.That(() => new Atrac1Encoder(1, new Atrac1EncoderOptions { WindowMask = 8 }),
        Throws.InstanceOf<ArgumentOutOfRangeException>());
      Assert.That(() => new Atrac1Encoder(1, new Atrac1EncoderOptions { BfuCount = 24 }),
        Throws.InstanceOf<ArgumentOutOfRangeException>());
    });
  }
}
