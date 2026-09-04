using Codec.WsAdpcm;

namespace Compression.Tests.Codecs.WsAdpcm;

[TestFixture]
public class WsAdpcmCodecTests {

  // ──────────── Raw (uncompressed) chunk ────────────

  [Test]
  public void Decode_RawChunk_WhenInSizeEqualsOutSize_CopiesVerbatim() {
    var payload = new byte[] { 10, 20, 30, 40, 255, 0 };
    var output = WsAdpcmCodec.Decode(payload, expectedOut: payload.Length);
    Assert.That(output, Is.EqualTo(payload));
  }

  // ──────────── Mode 3: hold / repeat the current sample ────────────

  [Test]
  public void Decode_Mode3_HoldsCurrentSampleCountPlusOneTimes() {
    // command 0xC2 → mode 3, count 2 → repeat current (128) three times.
    var output = WsAdpcmCodec.Decode([0xC2], expectedOut: 3);
    Assert.That(output, Is.EqualTo(new byte[] { 128, 128, 128 }));
  }

  // ──────────── Mode 2: raw byte run ────────────

  [Test]
  public void Decode_Mode2_RawRun_CopiesFollowingBytes() {
    // command 0x82 → mode 2 (top bits 10), bit5 clear, count 2 → 3 raw bytes follow.
    var output = WsAdpcmCodec.Decode([0x82, 50, 60, 70], expectedOut: 3);
    Assert.That(output, Is.EqualTo(new byte[] { 50, 60, 70 }));
  }

  [Test]
  public void Decode_Mode2_SmallDelta_AppliesSignedFiveBitDelta() {
    // command 0xA5 → mode 2 (top bits 10), bit5 set, low 5 bits = 5 (positive) → 128 + 5 = 133.
    // (trailing pad byte keeps payload length != expectedOut so it isn't a raw chunk.)
    var positive = WsAdpcmCodec.Decode([0xA5, 0x00], expectedOut: 1);
    Assert.That(positive[0], Is.EqualTo(133));

    // command 0xBF → mode 2, bit5 set, low 5 bits = 0x1F = -1 → 128 - 1 = 127.
    var negative = WsAdpcmCodec.Decode([0xBF, 0x00], expectedOut: 1);
    Assert.That(negative[0], Is.EqualTo(127));
  }

  // ──────────── Mode 0: four 2-bit deltas, shift = count ────────────

  [Test]
  public void Decode_Mode0_FourTwoBitDeltas_ScaledByShift() {
    // command 0x00 → mode 0, shift 0. Packed byte 0b11_10_01_00:
    //   i0 code 0 → table -2*?  table2={-2,-1,0,1}; code0=-2; sample 128-2=126
    //   i1 code 1 → -1; 126-1=125
    //   i2 code 2 →  0; 125
    //   i3 code 3 → +1; 126
    var packed = 0b11_10_01_00;
    var output = WsAdpcmCodec.Decode([0x00, (byte)packed], expectedOut: 4);
    Assert.That(output, Is.EqualTo(new byte[] { 126, 125, 125, 126 }));
  }

  // ──────────── Mode 1: 4-bit deltas via the 16-entry WS table ────────────

  [Test]
  public void Decode_Mode1_FourBitDeltas_UseWsTable() {
    // command 0x40 → mode 1, count 0 → one byte of two nibbles (low first).
    // table4[0]=-9, table4[8]=0, table4[0xF]=8.
    // byte 0x08 → low nibble 8 (delta 0) → 128; high nibble 0 (delta -9) → 119.
    // (trailing pad byte keeps payload length != expectedOut so it isn't a raw chunk.)
    var output = WsAdpcmCodec.Decode([0x40, 0x08, 0x00], expectedOut: 2);
    Assert.That(output, Is.EqualTo(new byte[] { 128, 119 }));
  }

  // ──────────── Clamping ────────────

  [Test]
  public void Decode_ClampsToByteRange() {
    // Mode 2 small-delta cannot exceed ±16, but raw run + mode0 with big shift can push out of range.
    // command 0x00 shift 0, packed all code 0 (-2) → drops by 8 over 4 samples from 128.
    // Use a high shift to force underflow: command 0x07 (shift 7), packed 0x00 (all code 0=-2<<7=-256).
    var output = WsAdpcmCodec.Decode([0x07, 0x00], expectedOut: 4);
    Assert.That(output, Is.EqualTo(new byte[] { 0, 0, 0, 0 }));
  }

  // ──────────── PCM conversion ────────────

  [Test]
  public void ToPcm16_Maps128ToZeroAndScales() {
    var pcm = WsAdpcmCodec.ToPcm16([128, 129, 127, 255, 0]);
    Assert.That(pcm[0], Is.EqualTo(0));
    Assert.That(pcm[1], Is.EqualTo(256));
    Assert.That(pcm[2], Is.EqualTo(-256));
    Assert.That(pcm[3], Is.EqualTo((255 - 128) << 8));
    Assert.That(pcm[4], Is.EqualTo((0 - 128) << 8));
  }

  // ──────────── Encoder round trips ────────────

  /// <summary>
  /// The WS chunk writer documents itself as lossless, so decoding what it produces must
  /// return the input exactly — for hold runs, one-byte deltas, literal runs and the raw
  /// fallback alike. The support matrix calls this codec R/W on the strength of this.
  /// </summary>
  [TestCase("hold runs", new byte[] { 128, 128, 128, 128, 128, 128, 128, 128 })]
  [TestCase("small deltas", new byte[] { 128, 132, 140, 130, 118, 120, 121, 121 })]
  [TestCase("literal jumps", new byte[] { 0, 255, 0, 255, 17, 200, 3, 250 })]
  [TestCase("leading jump from the 0x80 predictor", new byte[] { 4, 4, 4, 200 })]
  [TestCase("single sample", new byte[] { 7 })]
  public void Encode_RoundTripsLosslessly(string because, byte[] pcm) {
    var encoded = WsAdpcmCodec.Encode(pcm);

    Assert.That(WsAdpcmCodec.Decode(encoded, pcm.Length), Is.EqualTo(pcm), because);
  }

  [Test]
  public void Encode_RoundTripsALongRamp() {
    var pcm = new byte[512];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (byte)(i * 37 % 256);

    var encoded = WsAdpcmCodec.Encode(pcm);

    Assert.That(WsAdpcmCodec.Decode(encoded, pcm.Length), Is.EqualTo(pcm));
  }

  [Test]
  public void Encode_OfEmptyInput_IsEmpty()
    => Assert.That(WsAdpcmCodec.Encode([]), Is.Empty);

  /// <summary>
  /// PCM16 encoding drops to WS's native unsigned-8 domain, so a round trip is exact only
  /// to that resolution. Decoding must land back on the same 8-bit ladder.
  /// </summary>
  [Test]
  public void EncodePcm16_RoundTripsToTheEightBitLadder() {
    var pcm16 = new short[256];
    for (var i = 0; i < pcm16.Length; ++i)
      pcm16[i] = (short)((i - 128) << 8);

    var decoded = WsAdpcmCodec.ToPcm16(
      WsAdpcmCodec.Decode(WsAdpcmCodec.EncodePcm16(pcm16), pcm16.Length));

    Assert.That(decoded, Is.EqualTo(pcm16));
  }
}
