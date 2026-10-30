#pragma warning disable CS1591
using Codec.TrackerXmIt;

namespace Compression.Tests.Codecs.TrackerXmIt;

/// <summary>
/// Hand-walks the IT214/IT215 sample decompressor over crafted bitstreams. The test owns a
/// small LSB-first bit writer that mirrors the decoder's reader, so the exact packed bytes are
/// constructed in-test and the decoded samples are pinned to the expected delta accumulation.
/// Every boundary is taken from docs/IT-SAMPLE-COMPRESSION-SPEC.md §2–§5.
/// </summary>
[TestFixture]
public class ItSampleDecompressorTests {

  /// <summary>LSB-first bit writer producing a single IT compression block (with u16 prefix).</summary>
  private sealed class BlockWriter {
    private readonly List<byte> _bytes = [];
    private int _cur;
    private int _bits;

    public void Write(int value, int width) {
      for (var i = 0; i < width; ++i) {
        if ((value & (1 << i)) != 0) this._cur |= 1 << this._bits;
        if (++this._bits == 8) { this._bytes.Add((byte)this._cur); this._cur = 0; this._bits = 0; }
      }
    }

    /// <summary>Flushes pending bits and returns the block prefixed with its u16 LE length.</summary>
    public byte[] ToBlock() {
      if (this._bits > 0) { this._bytes.Add((byte)this._cur); this._cur = 0; this._bits = 0; }
      var len = this._bytes.Count;
      var result = new byte[2 + len];
      result[0] = (byte)(len & 0xFF);
      result[1] = (byte)((len >> 8) & 0xFF);
      this._bytes.CopyTo(result, 2);
      return result;
    }
  }

  private static byte[] Concat(params byte[][] blocks) => blocks.SelectMany(b => b).ToArray();

  // Helper: at the start of an 8-bit block the width is 9. Width 9 carries 8-bit samples
  // (low 8 bits), with bit 8 reserved as the escape flag; so a "plain" data value is the
  // unsigned 8-bit pattern of the signed sample (bit 8 = 0).
  private static int Sample9(sbyte v) => (byte)v;

  // At the start of a 16-bit block the width is 17; width 17 carries 16-bit samples (low
  // 16 bits) with bit 16 reserved as the escape flag.
  private static int Sample17(short v) => (ushort)v;

  /// <summary>From the block-start width (9 / 17) jump straight to <paramref name="width"/>.</summary>
  private static void JumpToWidth(BlockWriter w, int width, bool is16) {
    if (is16) w.Write(0x10000 | (width - 1), 17);
    else w.Write(0x100 | (width - 1), 9);
  }

  /// <summary>Writes the delta <paramref name="delta"/> as a <paramref name="width"/>-bit two's-complement code.</summary>
  private static void WriteDelta(BlockWriter w, int delta, int width) => w.Write(delta & ((1 << width) - 1), width);

  // ── Existing hand-walked vectors ──────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Decompress8_PlainWidth9_AccumulatesSingleDelta() {
    // Four width-9 data values (no escape), each an 8-bit signed delta. IT214 stores d1.
    var w = new BlockWriter();
    foreach (sbyte v in new sbyte[] { 5, -3, 10, -1 })
      w.Write(Sample9(v), 9);
    var block = w.ToBlock();

    var decoded = ItSampleDecompressor.Decompress8(block, 4, it215: false);

    // d1: 5, 2, 12, 11
    Assert.That(decoded, Is.EqualTo(new sbyte[] { 5, 2, 12, 11 }));
  }

  [Test, Category("HappyPath")]
  public void Decompress8_It215_AppliesDoubleDelta() {
    var w = new BlockWriter();
    foreach (sbyte v in new sbyte[] { 5, -3, 10, -1 })
      w.Write(Sample9(v), 9);
    var block = w.ToBlock();

    var decoded = ItSampleDecompressor.Decompress8(block, 4, it215: true);

    // d1: 5, 2, 12, 11 ; d2: 5, 7, 19, 30
    Assert.That(decoded, Is.EqualTo(new sbyte[] { 5, 7, 19, 30 }));
  }

  [Test, Category("HappyPath")]
  public void Decompress8_WidthChangeEscape_NarrowsThenDecodes() {
    // Start at width 9. Emit the width-9 escape (bit 8 set) to switch to width 3,
    // then emit two width-3 signed values.
    var w = new BlockWriter();
    // width==9 escape: value with bit 0x100 set; low byte = newWidth-1 = 2 → width 3.
    w.Write(0x100 | 2, 9);
    // Two width-3 values: 3 and -2 (3-bit two's complement: -2 = 0b110 = 6).
    w.Write(3 & 0x7, 3);
    w.Write(6, 3);
    var block = w.ToBlock();

    var decoded = ItSampleDecompressor.Decompress8(block, 2, it215: false);

    // After escape width=3. v0=3 → d1=3 ; v1=-2 → d1=1.
    Assert.That(decoded, Is.EqualTo(new sbyte[] { 3, 1 }));
  }

  [Test, Category("HappyPath")]
  public void Decompress16_PlainWidth17_AccumulatesSingleDelta() {
    var w = new BlockWriter();
    foreach (short v in new short[] { 1000, -250, 4000 })
      w.Write(Sample17(v), 17);
    var block = w.ToBlock();

    var decoded = ItSampleDecompressor.Decompress16(block, 3, it215: false);

    // d1: 1000, 750, 4750
    Assert.That(decoded, Is.EqualTo(new short[] { 1000, 750, 4750 }));
  }

  [Test, Category("HappyPath")]
  public void Decompress16_It215_AppliesDoubleDelta() {
    var w = new BlockWriter();
    foreach (short v in new short[] { 1000, -250, 4000 })
      w.Write(Sample17(v), 17);
    var block = w.ToBlock();

    var decoded = ItSampleDecompressor.Decompress16(block, 3, it215: true);

    // d1: 1000, 750, 4750 ; d2: 1000, 1750, 6500
    Assert.That(decoded, Is.EqualTo(new short[] { 1000, 1750, 6500 }));
  }

  // ── Length and empty-block boundaries (spec §2, §5) ───────────────────────────────────

  [Test, Category("Boundary")]
  [TestCase(0)]
  [TestCase(-1)]
  [TestCase(int.MinValue)]
  public void GivenNonPositiveLength_WhenDecoding_ThenEmptyWithoutTouchingInput(int length) {
    Assert.That(ItSampleDecompressor.Decompress8([], length, it215: false), Is.Empty);
    Assert.That(ItSampleDecompressor.Decompress16([], length, it215: true), Is.Empty);
  }

  [Test, Category("Boundary")]
  public void GivenZeroByteBlock_WhenDecoding_ThenEveryCodeReadsAsZeroDelta() {
    byte[] emptyBlock = [0, 0];

    Assert.That(ItSampleDecompressor.Decompress8(emptyBlock, 5, it215: false), Is.EqualTo(new sbyte[5]));
    Assert.That(ItSampleDecompressor.Decompress16(emptyBlock, 5, it215: true), Is.EqualTo(new short[5]));
  }

  [Test, Category("Boundary")]
  public void GivenTrailingBytesAfterTheLastBlock_WhenDecoding_ThenTheyAreIgnored() {
    var w = new BlockWriter();
    w.Write(Sample9(9), 9);
    var data = Concat(w.ToBlock(), [0xDE, 0xAD, 0xBE, 0xEF]);

    Assert.That(ItSampleDecompressor.Decompress8(data, 1, it215: false), Is.EqualTo(new sbyte[] { 9 }));
  }

  // ── Block-size limits: 0x8000 bytes of sample data per block (spec §2) ────────────────

  /// <summary>
  /// One block carrying <paramref name="codes"/> deltas of +1 (more than its share when
  /// <paramref name="codes"/> exceeds the per-block sample count), ending on a narrowing
  /// escape so a missing width reset would be visible in the next block.
  /// </summary>
  private static byte[] RampBlock(int codes, bool is16) {
    var w = new BlockWriter();
    for (var i = 0; i < codes; ++i)
      if (is16) w.Write(1, 17); else w.Write(1, 9);
    JumpToWidth(w, 3, is16);
    return w.ToBlock();
  }

  private static byte[] SingleCodeBlock(int value, bool is16) {
    var w = new BlockWriter();
    if (is16) w.Write(Sample17((short)value), 17); else w.Write(Sample9((sbyte)value), 9);
    return w.ToBlock();
  }

  [Test, Category("Boundary")]
  public void GivenExactly0x8000Samples8_WhenDecoding_ThenOneBlockSuffices() {
    var decoded = ItSampleDecompressor.Decompress8(RampBlock(0x8000, is16: false), 0x8000, it215: false);

    Assert.That(decoded, Has.Length.EqualTo(0x8000));
    for (var i = 0; i < decoded.Length; ++i)
      Assert.That(decoded[i], Is.EqualTo(unchecked((sbyte)(i + 1))), $"sample {i}");
  }

  [Test, Category("Boundary")]
  [TestCase(false)]
  [TestCase(true)]
  public void Given0x8001Samples8_WhenDecoding_ThenSecondBlockRestartsWidthAndIntegrators(bool it215) {
    var data = Concat(RampBlock(0x8000, is16: false), SingleCodeBlock(7, is16: false));

    var decoded = ItSampleDecompressor.Decompress8(data, 0x8001, it215);

    // The second block's lone code is read at width 9 with d1 = d2 = 0, whatever the first block left behind.
    Assert.That(decoded[0x8000], Is.EqualTo((sbyte)7));
  }

  [Test, Category("Boundary")]
  public void GivenBlockWithMoreCodesThanItsShare_WhenDecoding8_ThenSurplusIsSkippedAndNextHeaderIsHonoured() {
    var data = Concat(RampBlock(0x8000 + 10, is16: false), SingleCodeBlock(-5, is16: false));

    var decoded = ItSampleDecompressor.Decompress8(data, 0x8001, it215: false);

    Assert.That(decoded[0x7FFF], Is.EqualTo(unchecked((sbyte)0x8000)));
    Assert.That(decoded[0x8000], Is.EqualTo((sbyte)-5));
  }

  [Test, Category("Boundary")]
  public void GivenExactly0x4000Samples16_WhenDecoding_ThenOneBlockSuffices() {
    var decoded = ItSampleDecompressor.Decompress16(RampBlock(0x4000, is16: true), 0x4000, it215: false);

    Assert.That(decoded, Has.Length.EqualTo(0x4000));
    Assert.That(decoded[^1], Is.EqualTo((short)0x4000));
  }

  [Test, Category("Boundary")]
  [TestCase(false)]
  [TestCase(true)]
  public void Given0x4001Samples16_WhenDecoding_ThenSecondBlockRestartsWidthAndIntegrators(bool it215) {
    var data = Concat(RampBlock(0x4000, is16: true), SingleCodeBlock(-1234, is16: true));

    var decoded = ItSampleDecompressor.Decompress16(data, 0x4001, it215);

    Assert.That(decoded[0x4000], Is.EqualTo((short)-1234));
  }

  [Test, Category("Boundary")]
  public void GivenBlockWithMoreCodesThanItsShare_WhenDecoding16_ThenSurplusIsSkippedAndNextHeaderIsHonoured() {
    var data = Concat(RampBlock(0x4000 + 3, is16: true), SingleCodeBlock(321, is16: true));

    var decoded = ItSampleDecompressor.Decompress16(data, 0x4001, it215: false);

    Assert.That(decoded[0x3FFF], Is.EqualTo((short)0x4000));
    Assert.That(decoded[0x4000], Is.EqualTo((short)321));
  }

  // ── Truncation: refuse rather than invent (spec §3, owner rule "never wrong bytes") ───

  [Test, Category("ErrorHandling")]
  public void Given0x8001Samples8_WhenTheSecondBlockIsMissing_ThenRefuses() {
    var data = RampBlock(0x8000, is16: false);

    Assert.Throws<InvalidDataException>(() => ItSampleDecompressor.Decompress8(data, 0x8001, it215: false));
  }

  [Test, Category("ErrorHandling")]
  public void Given0x4001Samples16_WhenTheSecondBlockIsMissing_ThenRefuses() {
    var data = RampBlock(0x4000, is16: true);

    Assert.Throws<InvalidDataException>(() => ItSampleDecompressor.Decompress16(data, 0x4001, it215: true));
  }

  [Test, Category("ErrorHandling")]
  public void GivenHeaderLengthFarBeyondTheData_WhenDecoding_ThenRefusedBeforeAllocatingTheOutput() {
    var data = SingleCodeBlock(1, is16: true);

    // int.MaxValue 16-bit samples would be a 4 GiB array; the block walk refuses first.
    Assert.Throws<InvalidDataException>(() => ItSampleDecompressor.Decompress16(data, int.MaxValue, it215: false));
    Assert.Throws<InvalidDataException>(() => ItSampleDecompressor.Decompress8(data, int.MaxValue, it215: true));
  }

  [Test, Category("ErrorHandling")]
  [TestCase(new byte[] { })]
  [TestCase(new byte[] { 0x05 })]
  public void GivenBlockHeaderCutShort_WhenDecoding_ThenRefuses(byte[] data) {
    Assert.Throws<InvalidDataException>(() => ItSampleDecompressor.Decompress8(data, 1, it215: false));
    Assert.Throws<InvalidDataException>(() => ItSampleDecompressor.Decompress16(data, 1, it215: false));
  }

  [Test, Category("ErrorHandling")]
  public void GivenBlockClaimingMoreBytesThanRemain_WhenDecoding_ThenRefuses() {
    var block = SingleCodeBlock(3, is16: false);
    var cut = block[..^1];

    Assert.Throws<InvalidDataException>(() => ItSampleDecompressor.Decompress8(cut, 1, it215: false));
  }

  [Test, Category("EdgeCase")]
  public void GivenFinalCodeCutShortInsideItsBlock_WhenDecoding_ThenMissingBitsReadAsZero() {
    // 0x102 at width 9 would be the escape to width 3; with the block's byte count stopping
    // after the low byte, bit 8 reads as zero and the code is the delta +2. The next code
    // lies wholly past the block: all zero, delta 0.
    byte[] data = [1, 0, 0x02];

    Assert.That(ItSampleDecompressor.Decompress8(data, 2, it215: false), Is.EqualTo(new sbyte[] { 2, 2 }));
  }

  // ── Width changes, 8-bit (spec §4 step 3) ──────────────────────────────────────────────

  /// <summary>After landing on <paramref name="expectedWidth"/>, a +1 and a −1 code decode as 1, 0.</summary>
  private static void ProbeWidth8(BlockWriter w, int expectedWidth) {
    if (expectedWidth == 1) { w.Write(0, 1); w.Write(0, 1); return; }
    if (expectedWidth == 9) { w.Write(Sample9(1), 9); w.Write(Sample9(-1), 9); return; }
    WriteDelta(w, 1, expectedWidth);
    WriteDelta(w, -1, expectedWidth);
  }

  [Test, Category("Boundary")]
  [TestCase(6, 4, 5)]  // n = 5 < 6
  [TestCase(6, 5, 7)]  // n = 6 = current → skipped to 7
  [TestCase(6, 7, 9)]  // n = 8 → 9, back to the full width
  [TestCase(1, 0, 2)]  // n = 1 = current → 2
  [TestCase(1, 7, 9)]
  [TestCase(2, 0, 1)]
  [TestCase(5, 3, 4)]
  public void GivenLowWidthEscape8_WhenDecoding_ThenWidthSkipsTheCurrentOne(int from, int payload, int expected) {
    var w = new BlockWriter();
    JumpToWidth(w, from, is16: false);
    w.Write(1 << (from - 1), from);
    w.Write(payload, 3);
    ProbeWidth8(w, expected);

    var decoded = ItSampleDecompressor.Decompress8(w.ToBlock(), 2, it215: false);

    Assert.That(decoded, Is.EqualTo(expected == 1 ? new sbyte[] { 0, 0 } : new sbyte[] { 1, 0 }));
  }

  [Test, Category("Boundary")]
  [TestCase(7, 60, 1)]   // B = 59: first escape
  [TestCase(7, 63, 4)]
  [TestCase(7, 66, 8)]   // n = 7 = current → 8
  [TestCase(7, 67, 9)]   // last escape
  [TestCase(8, 124, 1)]  // B = 123: first escape
  [TestCase(8, 130, 7)]
  [TestCase(8, 131, 9)]  // n = 8 = current → 9; last escape
  public void GivenMidWidthEscapeBand8_WhenDecoding_ThenWidthIsSelected(int from, int code, int expected) {
    var w = new BlockWriter();
    JumpToWidth(w, from, is16: false);
    w.Write(code, from);
    ProbeWidth8(w, expected);

    var decoded = ItSampleDecompressor.Decompress8(w.ToBlock(), 2, it215: false);

    Assert.That(decoded, Is.EqualTo(expected == 1 ? new sbyte[] { 0, 0 } : new sbyte[] { 1, 0 }));
  }

  [Test, Category("Boundary")]
  [TestCase(7, 59, 59)]     // just below the band
  [TestCase(7, 68, -60)]    // just above the band, sign-extended from 7 bits
  [TestCase(8, 123, 123)]
  [TestCase(8, 132, -124)]
  [TestCase(9, 0x0FF, -1)]  // full width: low 8 bits, signed
  [TestCase(9, 0x080, -128)]
  [TestCase(6, 31, 31)]     // below the low-width escape (32)
  [TestCase(6, 33, -31)]    // above it, sign-extended from 6 bits
  public void GivenCodeBesideAnEscape8_WhenDecoding_ThenItIsASample(int width, int code, int expected) {
    var w = new BlockWriter();
    if (width != 9) JumpToWidth(w, width, is16: false);
    w.Write(code, width);

    Assert.That(ItSampleDecompressor.Decompress8(w.ToBlock(), 1, it215: false), Is.EqualTo(new[] { (sbyte)expected }));
  }

  [Test, Category("Boundary")]
  public void GivenFullWidthEscapeToWidth9_WhenDecoding8_ThenWidthStaysAtMaximum() {
    var w = new BlockWriter();
    w.Write(0x100 | 8, 9);
    w.Write(Sample9(42), 9);

    Assert.That(ItSampleDecompressor.Decompress8(w.ToBlock(), 1, it215: false), Is.EqualTo(new sbyte[] { 42 }));
  }

  [Test, Category("ErrorHandling")]
  [TestCase(0x100 | 9)]   // width 10
  [TestCase(0x1FF)]       // width 256
  public void GivenFullWidthEscapeBeyondMaximum8_WhenDecoding_ThenRefuses(int code) {
    var w = new BlockWriter();
    w.Write(code, 9);
    w.Write(0, 16);

    Assert.Throws<InvalidDataException>(() => ItSampleDecompressor.Decompress8(w.ToBlock(), 1, it215: false));
  }

  [Test, Category("EdgeCase")]
  public void GivenSeveralEscapesInARow_WhenDecoding_ThenNoneProducesASample() {
    var w = new BlockWriter();
    JumpToWidth(w, 7, is16: false);     // 9 → 7
    w.Write(60 + 2, 7);                  // band: n = 3 → width 3
    w.Write(1 << 2, 3); w.Write(5, 3);   // low: n = 6 → width 7
    w.Write(67, 7);                      // band: n = 8 → width 9
    w.Write(Sample9(-7), 9);

    Assert.That(ItSampleDecompressor.Decompress8(w.ToBlock(), 1, it215: false), Is.EqualTo(new sbyte[] { -7 }));
  }

  // ── Width changes, 16-bit ───────────────────────────────────────────────────────────────

  private static void ProbeWidth16(BlockWriter w, int expectedWidth) {
    if (expectedWidth == 1) { w.Write(0, 1); w.Write(0, 1); return; }
    if (expectedWidth == 17) { w.Write(Sample17(1), 17); w.Write(Sample17(-1), 17); return; }
    WriteDelta(w, 1, expectedWidth);
    WriteDelta(w, -1, expectedWidth);
  }

  [Test, Category("Boundary")]
  [TestCase(6, 4, 5)]
  [TestCase(6, 5, 7)]
  [TestCase(6, 15, 17)]  // n = 16 → 17, the 4-bit payload's maximum
  [TestCase(1, 0, 2)]
  [TestCase(3, 13, 15)]
  public void GivenLowWidthEscape16_WhenDecoding_ThenWidthUsesTheFourBitPayload(int from, int payload, int expected) {
    var w = new BlockWriter();
    JumpToWidth(w, from, is16: true);
    w.Write(1 << (from - 1), from);
    w.Write(payload, 4);
    ProbeWidth16(w, expected);

    var decoded = ItSampleDecompressor.Decompress16(w.ToBlock(), 2, it215: false);

    Assert.That(decoded, Is.EqualTo(expected == 1 ? new short[] { 0, 0 } : new short[] { 1, 0 }));
  }

  [Test, Category("Boundary")]
  [TestCase(7, 56, 1)]       // B = (0xFFFF >> 10) − 8 = 55
  [TestCase(7, 71, 17)]      // n = 16 → 17
  [TestCase(15, 16376, 1)]   // B = 16375
  [TestCase(15, 16390, 16)]  // n = 15 = current → 16
  [TestCase(16, 32760, 1)]   // B = 32759
  [TestCase(16, 32774, 15)]
  [TestCase(16, 32775, 17)]  // n = 16 = current → 17; last escape
  public void GivenMidWidthEscapeBand16_WhenDecoding_ThenWidthIsSelected(int from, int code, int expected) {
    var w = new BlockWriter();
    JumpToWidth(w, from, is16: true);
    w.Write(code, from);
    ProbeWidth16(w, expected);

    var decoded = ItSampleDecompressor.Decompress16(w.ToBlock(), 2, it215: false);

    Assert.That(decoded, Is.EqualTo(expected == 1 ? new short[] { 0, 0 } : new short[] { 1, 0 }));
  }

  [Test, Category("Boundary")]
  [TestCase(7, 55, 55)]
  [TestCase(7, 72, -56)]
  [TestCase(15, 16375, 16375)]
  [TestCase(15, 16392, -16376)]
  [TestCase(16, 32759, 32759)]
  [TestCase(16, 32776, -32760)]
  [TestCase(17, 0x0FFFF, -1)]
  [TestCase(17, 0x08000, -32768)]
  public void GivenCodeBesideAnEscape16_WhenDecoding_ThenItIsASample(int width, int code, int expected) {
    var w = new BlockWriter();
    if (width != 17) JumpToWidth(w, width, is16: true);
    w.Write(code, width);

    Assert.That(ItSampleDecompressor.Decompress16(w.ToBlock(), 1, it215: false), Is.EqualTo(new[] { (short)expected }));
  }

  [Test, Category("ErrorHandling")]
  [TestCase(0x10000 | 17)]
  [TestCase(0x1FFFF)]
  public void GivenFullWidthEscapeBeyondMaximum16_WhenDecoding_ThenRefuses(int code) {
    var w = new BlockWriter();
    w.Write(code, 17);
    w.Write(0, 24);

    Assert.Throws<InvalidDataException>(() => ItSampleDecompressor.Decompress16(w.ToBlock(), 1, it215: false));
  }

  // ── Integrator wrap-around (spec §4 step 5) ────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void GivenIt214_WhenTheIntegratorOverflows_ThenItWrapsAtEightBits() {
    var w = new BlockWriter();
    w.Write(Sample9(127), 9);
    w.Write(Sample9(1), 9);

    Assert.That(ItSampleDecompressor.Decompress8(w.ToBlock(), 2, it215: false), Is.EqualTo(new sbyte[] { 127, -128 }));
  }

  [Test, Category("Boundary")]
  public void GivenIt215_WhenTheSecondIntegratorOverflows_ThenBothWrapAtEightBits() {
    var w = new BlockWriter();
    w.Write(Sample9(100), 9);
    w.Write(Sample9(0), 9);
    w.Write(Sample9(0), 9);

    // d1: 100, 100, 100 ; d2: 100, 200 → −56, 300 → 44.
    Assert.That(ItSampleDecompressor.Decompress8(w.ToBlock(), 3, it215: true), Is.EqualTo(new sbyte[] { 100, -56, 44 }));
  }

  [Test, Category("Boundary")]
  public void GivenIt215_WhenTheSecondIntegratorOverflows_ThenBothWrapAtSixteenBits() {
    var w = new BlockWriter();
    w.Write(Sample17(30000), 17);
    w.Write(Sample17(0), 17);
    w.Write(Sample17(-30000), 17);

    // d1: 30000, 30000, 0 ; d2: 30000, 60000 → −5536, −5536.
    Assert.That(ItSampleDecompressor.Decompress16(w.ToBlock(), 3, it215: true), Is.EqualTo(new short[] { 30000, -5536, -5536 }));
  }
}
