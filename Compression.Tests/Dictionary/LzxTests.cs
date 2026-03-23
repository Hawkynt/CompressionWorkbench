using System.Text;
using Compression.Core.Dictionary.Lzx;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class LzxTests {
  // -------------------------------------------------------------------------
  // LzxConstants tests
  // -------------------------------------------------------------------------

  [Category("ThemVsUs")]
  [Test]
  public void PositionSlotCount_ReturnsCorrectValues() {
    Assert.That(LzxConstants.GetPositionSlotCount(15), Is.EqualTo(30));
    Assert.That(LzxConstants.GetPositionSlotCount(16), Is.EqualTo(32));
    Assert.That(LzxConstants.GetPositionSlotCount(17), Is.EqualTo(34));
    Assert.That(LzxConstants.GetPositionSlotCount(18), Is.EqualTo(36));
    Assert.That(LzxConstants.GetPositionSlotCount(19), Is.EqualTo(38));
    Assert.That(LzxConstants.GetPositionSlotCount(20), Is.EqualTo(42));
    Assert.That(LzxConstants.GetPositionSlotCount(21), Is.EqualTo(50));
  }

  [Category("Exception")]
  [Test]
  public void PositionSlotCount_InvalidWindowBits_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() => LzxConstants.GetPositionSlotCount(14));
    Assert.Throws<ArgumentOutOfRangeException>(() => LzxConstants.GetPositionSlotCount(22));
  }

  [Category("ThemVsUs")]
  [Test]
  public void OffsetToSlot_SmallOffsets() {
    Assert.That(LzxConstants.OffsetToSlot(0), Is.EqualTo(0));
    Assert.That(LzxConstants.OffsetToSlot(1), Is.EqualTo(1));
    Assert.That(LzxConstants.OffsetToSlot(2), Is.EqualTo(2));
    Assert.That(LzxConstants.OffsetToSlot(3), Is.EqualTo(3));
    Assert.That(LzxConstants.OffsetToSlot(4), Is.EqualTo(4));
    Assert.That(LzxConstants.OffsetToSlot(5), Is.EqualTo(4));
    Assert.That(LzxConstants.OffsetToSlot(6), Is.EqualTo(5));
    Assert.That(LzxConstants.OffsetToSlot(7), Is.EqualTo(5));
    Assert.That(LzxConstants.OffsetToSlot(8), Is.EqualTo(6));
  }

  [Category("ThemVsUs")]
  [Test]
  public void GetSlotInfo_Slot0To3_ZeroFooter() {
    for (var slot = 0; slot < 4; ++slot) {
      LzxConstants.GetSlotInfo(slot, out var baseOffset, out var footerBits);
      Assert.That(baseOffset, Is.EqualTo(slot), $"slot {slot} base");
      Assert.That(footerBits, Is.EqualTo(0), $"slot {slot} footer bits");
    }
  }

  [Category("ThemVsUs")]
  [Test]
  public void GetSlotInfo_Slot4And5_OneFooterBit() {
    LzxConstants.GetSlotInfo(4, out var base4, out var footer4);
    Assert.That(base4, Is.EqualTo(4));
    Assert.That(footer4, Is.EqualTo(1));

    LzxConstants.GetSlotInfo(5, out var base5, out var footer5);
    Assert.That(base5, Is.EqualTo(6));
    Assert.That(footer5, Is.EqualTo(1));
  }

  [Category("ThemVsUs")]
  [Test]
  public void GetSlotInfo_Slot6And7_TwoFooterBits() {
    LzxConstants.GetSlotInfo(6, out var base6, out var footer6);
    Assert.That(base6, Is.EqualTo(8));
    Assert.That(footer6, Is.EqualTo(2));

    LzxConstants.GetSlotInfo(7, out var base7, out var footer7);
    Assert.That(base7, Is.EqualTo(12));
    Assert.That(footer7, Is.EqualTo(2));
  }

  [Category("HappyPath")]
  [Test]
  public void GetSlotInfo_BaseOffsets_AreMonotonicallyIncreasing() {
    var prevBase = -1;
    for (var slot = 0; slot < LzxConstants.PositionSlots21; ++slot) {
      LzxConstants.GetSlotInfo(slot, out var baseOffset, out _);
      Assert.That(baseOffset, Is.GreaterThan(prevBase), $"slot {slot}");
      prevBase = baseOffset;
    }
  }

  [Category("HappyPath")]
  [Test]
  public void GetSlotInfo_FooterBitsIncrementEveryTwoSlots() {
    for (var slot = 4; slot < LzxConstants.PositionSlots21 - 1; slot += 2) {
      LzxConstants.GetSlotInfo(slot, out _, out var footer0);
      LzxConstants.GetSlotInfo(slot + 1, out _, out var footer1);
      Assert.That(footer0, Is.EqualTo(footer1), $"pair starting at slot {slot}");

      LzxConstants.GetSlotInfo(slot + 2, out _, out var footerNext);
      Assert.That(footerNext, Is.EqualTo(footer0 + 1), $"next pair at slot {slot + 2}");
    }
  }

  // -------------------------------------------------------------------------
  // Round-trip tests (compressor → decompressor)
  // -------------------------------------------------------------------------

  private static byte[] RoundTrip(byte[] data, int windowBits = 15) {
    var compressor = new LzxCompressor(windowBits);
    var compressed = compressor.Compress(data);

    using var ms = new MemoryStream(compressed);
    var decompressor = new LzxDecompressor(ms, windowBits);
    return decompressor.Decompress(data.Length);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    var result = RoundTrip([]);
    Assert.That(result, Is.Empty);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [0x42];
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_AllSameBytes() {
    var data = new byte[256];
    Array.Fill(data, (byte)0xAB);
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i) data[i] = (byte)i;
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_ShortAsciiText() {
    var data = Encoding.ASCII.GetBytes("Hello, LZX!");
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    var data = Encoding.ASCII.GetBytes("ABCABCABCABCABCABC");
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepeatedPhrase() {
    var data = Encoding.ASCII.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "The quick brown fox jumps over the lazy dog.");
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData_Small() {
    var rng = new Random(42);
    var data = new byte[256];
    rng.NextBytes(data);
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData_1KB() {
    var rng = new Random(123);
    var data = new byte[1024];
    rng.NextBytes(data);
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData_16KB() {
    var rng = new Random(456);
    var data = new byte[16 * 1024];
    rng.NextBytes(data);
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData_64KB() {
    var rng = new Random(789);
    var data = new byte[64 * 1024];
    rng.NextBytes(data);
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Window16Bits() {
    var rng = new Random(100);
    var data = new byte[8 * 1024];
    rng.NextBytes(data);
    Assert.That(RoundTrip(data, windowBits: 16), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Window17Bits() {
    var rng = new Random(200);
    var data = new byte[8 * 1024];
    rng.NextBytes(data);
    Assert.That(RoundTrip(data, windowBits: 17), Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SpansMultipleBlocks() {
    // Force multiple 32 KB blocks
    var rng = new Random(999);
    var data = new byte[100 * 1024];
    rng.NextBytes(data);
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LongMatch() {
    // Highly compressible: long runs that force max match lengths
    var data = new byte[10000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 16);
    Assert.That(RoundTrip(data), Is.EqualTo(data));
  }

  // -------------------------------------------------------------------------
  // Compressor helpers (internal visibility via InternalsVisibleTo)
  // -------------------------------------------------------------------------

  [Category("EdgeCase")]
  [Test]
  public void BuildCodeLengths_AllZeroFrequencies_ReturnsAllZero() {
    var freq = new int[LzxConstants.NumChars];
    var lengths = LzxCompressor.BuildCodeLengths(freq, LzxConstants.NumChars, 16);
    Assert.That(lengths, Is.All.EqualTo(0));
  }

  [Category("EdgeCase")]
  [Test]
  public void BuildCodeLengths_SingleSymbol_LengthOne() {
    var freq = new int[LzxConstants.NumChars];
    freq[65] = 10;
    var lengths = LzxCompressor.BuildCodeLengths(freq, LzxConstants.NumChars, 16);
    Assert.That(lengths[65], Is.EqualTo(1));
  }

  [Category("HappyPath")]
  [Test]
  public void BuildCanonicalCodes_TwoSymbols_CorrectCodes() {
    int[] lengths = [1, 1];
    var codes = LzxCompressor.BuildCanonicalCodes(lengths);
    Assert.That(codes[0], Is.EqualTo(0u));
    Assert.That(codes[1], Is.EqualTo(1u));
  }

  // -------------------------------------------------------------------------
  // Decompressor constructor validation
  // -------------------------------------------------------------------------

  [Category("Exception")]
  [Test]
  public void Decompressor_NullStream_Throws() {
    Assert.Throws<ArgumentNullException>(() => new LzxDecompressor(null!));
  }

  [Category("Exception")]
  [Test]
  public void Decompressor_InvalidWindowBits_Throws() {
    using var ms = new MemoryStream();
    Assert.Throws<ArgumentOutOfRangeException>(() => new LzxDecompressor(ms, 14));
    Assert.Throws<ArgumentOutOfRangeException>(() => new LzxDecompressor(ms, 22));
  }

  [Category("Exception")]
  [Test]
  public void Compressor_InvalidWindowBits_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() => new LzxCompressor(14));
    Assert.Throws<ArgumentOutOfRangeException>(() => new LzxCompressor(22));
  }
}
