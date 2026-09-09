using Codec.Brr;

namespace Compression.Tests.Codecs.Brr;

[TestFixture]
public class BrrCodecTests {

  private const string BrrToolsRevision = "5b809f171d6a8fe436f09cd883f26994e58feb35";

  private static byte[] Block(byte header, params int[] nibbles) {
    var block = new byte[BrrCodec.BlockSize];
    block[0] = header;
    for (var i = 0; i < BrrCodec.SamplesPerBlock; ++i) {
      var nibble = i < nibbles.Length ? nibbles[i] & 0x0F : 0;
      if ((i & 1) == 0)
        block[1 + (i >> 1)] |= (byte)(nibble << 4);
      else
        block[1 + (i >> 1)] |= (byte)nibble;
    }

    return block;
  }

  // The oracle vectors in this fixture were captured from BRRtools 3.15 at the pinned
  // revision above. BRRtools has no repository LICENSE file, so its implementation is used
  // only as a behavioral oracle; no encoder/decoder source is incorporated here.

  [Test]
  public void Decode_Filter0Range1_MatchesBrrTools() {
    var pcm = BrrCodec.Decode(Block(0x10, 1, 2, 0xF));

    Assert.That(pcm.Length, Is.EqualTo(16));
    Assert.That(pcm[0], Is.EqualTo((short)2));
    Assert.That(pcm[1], Is.EqualTo((short)4));
    Assert.That(pcm[2], Is.EqualTo((short)-2));
    Assert.That(pcm[3..], Is.All.EqualTo((short)0));
  }

  [Test]
  public void Decode_Filter0Range12_MatchesBrrTools() {
    var pcm = BrrCodec.Decode(Block(0xC0, 1, 7, 0xF));

    Assert.That(pcm[0], Is.EqualTo((short)4096));
    Assert.That(pcm[1], Is.EqualTo((short)28672));
    Assert.That(pcm[2], Is.EqualTo((short)-4096));
  }

  [Test]
  public void Decode_Filter2NegativeHistory_MatchesBrrTools() {
    var encoded = Convert.FromHexString("181F27E34C5A6987F0");
    short[] expected = [2, 0, 2, 16, 24, 34, 48, 50, 58, 50, 50, 32, -4, -24, -46, -66];

    Assert.That(BrrCodec.Decode(encoded), Is.EqualTo(expected), $"BRRtools {BrrToolsRevision}");
  }

  [Test]
  public void Decode_Filter3NegativeHistory_MatchesBrrTools() {
    var encoded = Convert.FromHexString("1C89ABCDEF01234567");
    short[] expected = [-16, -44, -80, -120, -160, -198, -230, -256, -274, -284, -286, -278, -260, -234, -200, -156];

    Assert.That(BrrCodec.Decode(encoded), Is.EqualTo(expected), $"BRRtools {BrrToolsRevision}");
  }

  [Test]
  public void Decode_InvalidRange_MatchesBrrTools() {
    var pcm = BrrCodec.Decode(Block(0xD0, 0, 7, 8, 0xF));

    Assert.Multiple(() => {
      Assert.That(pcm[0], Is.EqualTo((short)4096));
      Assert.That(pcm[1], Is.EqualTo((short)4096));
      Assert.That(pcm[2], Is.EqualTo((short)-4096));
      Assert.That(pcm[3], Is.EqualTo((short)-4096));
    });
  }

  [Test]
  public void Decode_StopsAfterEndFlaggedBlock() {
    var first = Block(0x11, 1);
    var second = Block(0x10, 5);
    var stream = new byte[BrrCodec.BlockSize * 2];
    first.CopyTo(stream, 0);
    second.CopyTo(stream, BrrCodec.BlockSize);

    var pcm = BrrCodec.Decode(stream);

    Assert.That(pcm.Length, Is.EqualTo(16));
    Assert.That(pcm[0], Is.EqualTo((short)2));
  }

  [Test]
  public void Decode_TrailingPartialBlock_IsIgnored() {
    var data = new byte[BrrCodec.BlockSize + 4];
    Assert.That(BrrCodec.Decode(data).Length, Is.EqualTo(16));
  }

  [Test]
  public void Decode_Empty_ReturnsEmpty()
    => Assert.That(BrrCodec.Decode(ReadOnlySpan<byte>.Empty), Is.Empty);

  [Test]
  public void Decode_FifteenBitWrap_FoldsSaturatedValueNegative() {
    var block = Block(0xC4, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7);
    var pcm = BrrCodec.Decode(block);

    Assert.That(pcm.Any(static sample => sample < 0), Is.True);
  }

  [Test]
  public void Encode_Empty_ReturnsEmpty()
    => Assert.That(BrrCodec.Encode(ReadOnlySpan<short>.Empty), Is.Empty);

  [Test]
  public void Encode_Silence_MatchesBrrTools() {
    var encoded = BrrCodec.Encode(new short[16]);

    Assert.That(encoded, Is.EqualTo(Convert.FromHexString("010000000000000000")),
      $"BRRtools {BrrToolsRevision}");
  }

  [Test]
  public void Encode_Ramp_MatchesBrrTools() {
    var pcm = new short[16];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(-12000 + i * 1600);

    var encoded = BrrCodec.Encode(pcm);

    Assert.That(encoded, Is.EqualTo(Convert.FromHexString(
      "000000000000000000BDA6F1000001F1001F")),
      $"BRRtools {BrrToolsRevision}");
  }

  [Test]
  public void Encode_LeadingPadding_MatchesBrrTools() {
    var pcm = new short[20];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)((i & 1) == 0 ? -9000 : 9000);

    var encoded = BrrCodec.Encode(pcm);

    Assert.That(encoded, Is.EqualTo(Convert.FromHexString(
      "000000000000000000C4000000000000E4C4C5C4C4C4C4C4C4C4C4")),
      $"BRRtools {BrrToolsRevision}");
  }

  [Test]
  public void Encode_NonTrivialSignal_HasNoMoreSquaredErrorThanBrrTools() {
    var pcm = new short[64];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)Math.Round(Math.Sin(i * 2 * Math.PI / 23) * 12000, MidpointRounding.ToEven);

    var oracle = Convert.FromHexString(
      "0000000000000000009C0611F0FFEEFEFFF07841534523200EDDCC7C9A9BCD0135576765691EDAA8888BBFF355");

    var oursDecoded = BrrCodec.Decode(BrrCodec.Encode(pcm));
    var oracleDecoded = BrrCodec.Decode(oracle);
    var alignedSource = new short[pcm.Length + BrrCodec.SamplesPerBlock];
    pcm.CopyTo(alignedSource, BrrCodec.SamplesPerBlock);

    var oursError = SquaredError(alignedSource, oursDecoded);
    var oracleError = SquaredError(alignedSource, oracleDecoded);

    Assert.That(oursError, Is.LessThanOrEqualTo(oracleError),
      $"independent encoder SSE {oursError} exceeded BRRtools {BrrToolsRevision} SSE {oracleError}");
  }

  [Test]
  public void Encode_PadsToWholeBlocks_AndMarksLastBlock() {
    var pcm = new short[20];
    var encoded = BrrCodec.Encode(pcm);

    Assert.That(encoded.Length, Is.EqualTo(2 * BrrCodec.BlockSize));
    Assert.That(encoded[0] & 0x01, Is.Zero);
    Assert.That(encoded[BrrCodec.BlockSize] & 0x01, Is.EqualTo(0x01));
  }

  [Test]
  public void EncodeDecode_Silence_RoundTripsExactly() {
    var pcm = new short[16 * 3];
    Assert.That(BrrCodec.Decode(BrrCodec.Encode(pcm)), Is.EqualTo(pcm));
  }

  [Test]
  public void EncodeDecode_Sine_RoundTripsWithinTolerance() {
    const int count = 16 * 40;
    var pcm = new short[count];
    for (var i = 0; i < count; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 48) * 10000);

    var decoded = BrrCodec.Decode(BrrCodec.Encode(pcm));
    var leading = decoded.Length - pcm.Length;

    Assert.That(leading, Is.EqualTo(BrrCodec.SamplesPerBlock));

    var maxError = 0;
    for (var i = 0; i < count; ++i)
      maxError = Math.Max(maxError, Math.Abs(decoded[leading + i] - pcm[i]));

    Assert.That(maxError, Is.LessThan(1500), $"max abs error {maxError}");
  }

  private static long SquaredError(ReadOnlySpan<short> expected, ReadOnlySpan<short> actual) {
    Assert.That(actual.Length, Is.EqualTo(expected.Length));

    long error = 0;
    for (var i = 0; i < expected.Length; ++i) {
      var difference = (long)actual[i] - expected[i];
      error += difference * difference;
    }

    return error;
  }
}
