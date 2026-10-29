#pragma warning disable CS1591
using System.Reflection;
using System.Runtime.InteropServices;
using Codec.G722;

namespace Compression.Tests.Codecs.G722;

/// <summary>
/// Pins <see cref="G722Codec"/> bit-exactly against streams FFmpeg 8.1.2 encoded and decoded
/// (Codecs/G722/ReferenceVectors, provenance in its README), and covers the edge cases of
/// docs/G722-SPEC.md section 6.
/// </summary>
[TestFixture]
public class G722ReferenceVectorTests {

  private static readonly string[] EncodedInputs = ["silence", "sine1k", "sine6k", "noise", "square", "speech"];

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"G722Vectors.{name}")
      ?? throw new InvalidOperationException($"missing embedded G.722 vector {name}");
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  private static short[] Pcm(string name) => MemoryMarshal.Cast<byte, short>(Vector(name)).ToArray();

  // ── Oracle vectors ───────────────────────────────────────────────────────

  [TestCaseSource(nameof(EncodedInputs))]
  public void Given_FfmpegInput_When_Encoded_Then_OctetsMatchFfmpegByteForByte(string name) {
    var pcm = Pcm($"{name}.pcm");

    var encoded = G722Codec.Encode(pcm);

    Assert.That(encoded, Is.EqualTo(Vector($"{name}.g722")));
  }

  [TestCaseSource(nameof(EncodedInputs))]
  public void Given_FfmpegStream_When_Decoded_Then_SamplesMatchFfmpeg(string name) {
    var stream = Vector($"{name}.g722");

    var decoded = G722Codec.Decode(stream);

    Assert.That(decoded, Is.EqualTo(Pcm($"{name}.dec.pcm")));
  }

  [TestCaseSource(nameof(EncodedInputs))]
  public void Given_OurEncoding_When_DecodedByUs_Then_MatchesFfmpegDecoderOnFfmpegStream(string name) {
    var pcm = Pcm($"{name}.pcm");

    var decoded = G722Codec.Decode(G722Codec.Encode(pcm));

    Assert.That(decoded, Is.EqualTo(Pcm($"{name}.dec.pcm")));
  }

  [Test]
  public void Given_EveryOctetRepeated64Times_When_Decoded_Then_SamplesMatchFfmpeg() {
    var stream = Vector("sweep.g722");
    Assert.That(stream, Has.Length.EqualTo(256 * 64));

    var decoded = G722Codec.Decode(stream);

    Assert.That(decoded, Is.EqualTo(Pcm("sweep.dec.pcm")));
  }

  // ── Section 6 edge cases ─────────────────────────────────────────────────

  [Test]
  public void Given_EmptyInput_When_EncodedOrDecoded_Then_OutputIsEmpty() {
    Assert.That(G722Codec.Encode([]), Is.Empty);
    Assert.That(G722Codec.Decode([]), Is.Empty);
  }

  [TestCase(1, 0)]
  [TestCase(2, 1)]
  [TestCase(3, 1)]
  [TestCase(7999, 3999)]
  public void Given_SampleCount_When_Encoded_Then_FloorHalfOctetsAndTrailingSampleIgnored(int samples, int octets) {
    var pcm = Pcm("noise.pcm").AsSpan(0, samples);

    var encoded = G722Codec.Encode(pcm);

    Assert.That(encoded, Has.Length.EqualTo(octets));
    Assert.That(encoded, Is.EqualTo(G722Codec.Encode(pcm[..(samples & ~1)])));
  }

  [Test]
  public void Given_FullScaleNyquistSquare_When_RoundTripped_Then_NoOverflowAndCountsHold() {
    var pcm = new short[4000];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (i & 1) == 0 ? short.MaxValue : short.MinValue;

    var decoded = G722Codec.Decode(G722Codec.Encode(pcm));

    Assert.That(decoded, Has.Length.EqualTo(pcm.Length));
  }

  [Test]
  public void Given_LoudSignalThenLongSilence_When_RoundTripped_Then_OutputSettlesToNearZero() {
    var pcm = new short[16000];
    Pcm("square.pcm").CopyTo(pcm, 0);

    var decoded = G722Codec.Decode(G722Codec.Encode(pcm));

    // The scale factors decay back to their floors (pinned per band below), so the
    // reconstructed tail is quantisation noise around zero, not a ringing predictor.
    foreach (var sample in decoded[^4000..])
      Assert.That(Math.Abs((int)sample), Is.LessThan(64));
  }

  [Test]
  public void Given_LowBand_When_MaximumCodeRepeats_Then_NbClampsAt18432AndScaleShiftsLeft() {
    var band = G722Codec.Band.CreateLow();
    for (var i = 0; i < 200; ++i)
      band.Adapt(1); // RL42[1] = 7 → WL = 3042, the largest increment.

    Assert.That(band.Nb, Is.EqualTo(G722Codec.LowNbLimit));
    Assert.That(band.Det, Is.EqualTo(16384)); // ILB[0] << 1 << 2: k = 8 − 9 < 0.
  }

  [Test]
  public void Given_HighBand_When_MaximumCodeRepeats_Then_NbClampsAt22528AndScaleShiftsLeft() {
    var band = G722Codec.Band.CreateHigh();
    for (var i = 0; i < 200; ++i)
      band.Adapt(0); // RH2[0] = 2 → WH = 798.

    Assert.That(band.Nb, Is.EqualTo(G722Codec.HighNbLimit));
    Assert.That(band.Det, Is.EqualTo(16384)); // k = 10 − 11 < 0.
  }

  [Test]
  public void Given_SaturatedBands_When_SmallestCodesRepeat_Then_NbDecaysToZeroAndDetReturnsToFloor() {
    var low = G722Codec.Band.CreateLow();
    var high = G722Codec.Band.CreateHigh();
    for (var i = 0; i < 200; ++i) {
      low.Adapt(1);
      high.Adapt(0);
    }

    for (var i = 0; i < 2000; ++i) {
      low.Adapt(0);  // WL = −60
      high.Adapt(1); // WH = −214
    }

    Assert.That(low.Nb, Is.Zero);
    Assert.That(low.Det, Is.EqualTo(32));
    Assert.That(high.Nb, Is.Zero);
    Assert.That(high.Det, Is.EqualTo(8));
  }

  [TestCase(0, 8, 32)]
  [TestCase(0, 10, 8)]
  [TestCase(18432, 8, 16384)]
  [TestCase(22528, 10, 16384)]
  [TestCase(2047, 8, 4008 >> 8 << 2)]
  [TestCase(2048, 8, 2048 >> 7 << 2)]
  public void Given_LogScaleFactor_When_Scaled_Then_InverseLogTableIsApplied(int nb, int scaleBase, int expected)
    => Assert.That(G722Codec.Scale(nb, scaleBase), Is.EqualTo(expected));

  [TestCase(0, 61)]       // WD 0 < 35 → m = 1, ILP
  [TestCase(-1, 63)]      // WD 0 → m = 1, ILN
  [TestCase(34, 61)]      // just under the first threshold
  [TestCase(35, 60)]      // on the threshold → next level
  [TestCase(2918, 33)]    // just under the last threshold → m = 29
  [TestCase(2919, 32)]    // level search falls through → m = 30, ILP
  [TestCase(-2920, 4)]    // WD 2919 → m = 30, ILN
  [TestCase(32767, 32)]
  [TestCase(-32768, 4)]
  public void Given_LowBandDifference_When_Quantised_Then_LevelAndSignSelectCode(int el, int code)
    => Assert.That(G722Codec.QuantiseLow(el, 4096), Is.EqualTo(code)); // DETL 4096 → thresholds are Q6 itself

  [TestCase(0, 3)]
  [TestCase(-1, 1)]
  [TestCase(563, 3)]
  [TestCase(564, 2)]
  [TestCase(-564, 1)]
  [TestCase(-565, 0)]
  public void Given_HighBandDifference_When_Quantised_Then_LevelAndSignSelectCode(int eh, int code)
    => Assert.That(G722Codec.QuantiseHigh(eh, 4096), Is.EqualTo(code));

  [Test]
  public void Given_EveryOctet_When_DecodedTwice_Then_NoExceptionAndIdenticalOutput() {
    var octets = new byte[256];
    for (var i = 0; i < octets.Length; ++i)
      octets[i] = (byte)i;

    var first = G722Codec.Decode(octets);
    var second = G722Codec.Decode(octets);

    Assert.That(first, Has.Length.EqualTo(512));
    Assert.That(second, Is.EqualTo(first));
  }
}
