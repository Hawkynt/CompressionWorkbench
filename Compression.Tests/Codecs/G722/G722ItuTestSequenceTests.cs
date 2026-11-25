#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.G722;

namespace Compression.Tests.Codecs.G722;

/// <summary>
/// Runs the ITU-T G.722 Appendix II digital test sequences through the sub-band ADPCM encoder
/// and decoder, QMF bypassed, and requires every word to match. The sequences are ITU
/// copyright without a redistribution grant, so they are not committed: point
/// <c>CWB_G722_ITU_VECTORS</c> at the little-endian binary set (<c>g722-ts-le/bin</c>), which
/// <c>ReferenceVectors/fetch-itu-vectors.sh</c> downloads from itu.int and checks.
/// <para>
/// Word layout (Appendix II): 16-bit words; bit 0 set means "reset" (the codec state returns to
/// its initial values and the expected output word is 1). Encoder input carries the 14-bit
/// sample in bits 15–2 and feeds the same value, shifted right by one, to both sub-band
/// encoders. Code words carry the octet in bits 15–8. Reconstructed outputs carry the 15-bit
/// sub-band signal shifted left by one.
/// </para>
/// </summary>
[TestFixture]
public class G722ItuTestSequenceTests {

  private const string VectorDirectoryVariable = "CWB_G722_ITU_VECTORS";

  private static string VectorDirectory() {
    var directory = Environment.GetEnvironmentVariable(VectorDirectoryVariable);
    if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
      Assert.Ignore($"{VectorDirectoryVariable} does not name a directory holding the ITU-T G.722 Appendix II test sequences (g722-ts-le/bin).");
    return directory!;
  }

  private static short[] Words(string name) {
    var path = Path.Combine(VectorDirectory(), name);
    if (!File.Exists(path))
      Assert.Fail($"{name} is missing from {VectorDirectoryVariable}.");

    var bytes = File.ReadAllBytes(path);
    var words = new short[bytes.Length / 2];
    for (var i = 0; i < words.Length; ++i)
      words[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2 * i));
    return words;
  }

  [TestCase("bt1c1.xmt", "bt2r1.cod")]
  [TestCase("bt1c2.xmt", "bt2r2.cod")]
  public void Given_ItuEncoderSequence_When_EncodedWithQmfBypassed_Then_EveryCodeWordMatches(string input, string reference) {
    var samples = Words(input);
    var expected = Words(reference);
    Assert.That(expected, Has.Length.EqualTo(samples.Length));

    // The reference harness resets once before the first word.
    var low = G722Codec.Band.CreateLow();
    var high = G722Codec.Band.CreateHigh();
    var mismatches = 0;
    var firstMismatch = -1;
    for (var n = 0; n < samples.Length; ++n) {
      int actual;
      if ((samples[n] & 1) != 0) {
        low = G722Codec.Band.CreateLow();
        high = G722Codec.Band.CreateHigh();
        actual = 1;
      } else {
        var x = samples[n] >> 1;
        actual = (short)(G722Codec.EncodeSubbands(low, high, x, x) << 8);
      }

      if (actual == expected[n])
        continue;
      ++mismatches;
      if (firstMismatch < 0)
        firstMismatch = n;
    }

    Assert.That(mismatches, Is.Zero, $"first mismatch at word {firstMismatch} of {samples.Length}");
  }

  [TestCase("bt2r1.cod", "bt3l1.rc1", "bt3h1.rc0", 1)]
  [TestCase("bt2r1.cod", "bt3l1.rc2", "bt3h1.rc0", 2)]
  [TestCase("bt2r1.cod", "bt3l1.rc3", "bt3h1.rc0", 3)]
  [TestCase("bt2r2.cod", "bt3l2.rc1", "bt3h2.rc0", 1)]
  [TestCase("bt2r2.cod", "bt3l2.rc2", "bt3h2.rc0", 2)]
  [TestCase("bt2r2.cod", "bt3l2.rc3", "bt3h2.rc0", 3)]
  [TestCase("bt1d3.cod", "bt3l3.rc1", "bt3h3.rc0", 1)]
  [TestCase("bt1d3.cod", "bt3l3.rc2", "bt3h3.rc0", 2)]
  [TestCase("bt1d3.cod", "bt3l3.rc3", "bt3h3.rc0", 3)]
  public void Given_ItuDecoderSequence_When_DecodedWithQmfBypassed_Then_BothSubbandsMatch(string input, string lowReference, string highReference, int mode) {
    var codes = Words(input);
    var expectedLow = Words(lowReference);
    var expectedHigh = Words(highReference);
    Assert.That(expectedLow, Has.Length.EqualTo(codes.Length));
    Assert.That(expectedHigh, Has.Length.EqualTo(codes.Length));

    var low = G722Codec.Band.CreateLow();
    var high = G722Codec.Band.CreateHigh();
    var mismatches = 0;
    var firstMismatch = -1;
    for (var n = 0; n < codes.Length; ++n) {
      int rl, rh;
      if ((codes[n] & 1) != 0) {
        low = G722Codec.Band.CreateLow();
        high = G722Codec.Band.CreateHigh();
        rl = rh = 1;
      } else {
        var (l, h) = G722Codec.DecodeSubbands(low, high, (codes[n] >> 8) & 0xFF, mode);
        rl = (short)(l << 1);
        rh = (short)(h << 1);
      }

      if (rl == expectedLow[n] && rh == expectedHigh[n])
        continue;
      ++mismatches;
      if (firstMismatch < 0)
        firstMismatch = n;
    }

    Assert.That(mismatches, Is.Zero, $"first mismatch at word {firstMismatch} of {codes.Length}");
  }

  // ── Interface boundaries that hold without the sequences ─────────────────

  [TestCase(0)]
  [TestCase(4)]
  [TestCase(-1)]
  public void Given_UnknownDecoderMode_When_Decoded_Then_Throws(int mode)
    => Assert.Throws<ArgumentOutOfRangeException>(
      () => G722Codec.DecodeSubbands(G722Codec.Band.CreateLow(), G722Codec.Band.CreateHigh(), 0x5F, mode));

  [Test]
  public void Given_FreshState_When_SameOctetDecodedInEveryMode_Then_OnlyLowBandOutputDiffersAndStateAgrees() {
    // Adaptation reads IL >> 2 only, so the three modes share the next state.
    var states = new (G722Codec.Band Low, G722Codec.Band High)[3];
    var outputs = new (int Low, int High)[3];
    for (var mode = 1; mode <= 3; ++mode) {
      states[mode - 1] = (G722Codec.Band.CreateLow(), G722Codec.Band.CreateHigh());
      for (var i = 0; i < 40; ++i)
        outputs[mode - 1] = G722Codec.DecodeSubbands(states[mode - 1].Low, states[mode - 1].High, 0x2D + i, mode);
    }

    Assert.That(outputs.Select(o => o.High).Distinct().Count(), Is.EqualTo(1));
    Assert.That(states.Select(s => s.Low.S).Distinct().Count(), Is.EqualTo(1));
    Assert.That(states.Select(s => s.Low.Det).Distinct().Count(), Is.EqualTo(1));
  }
}
