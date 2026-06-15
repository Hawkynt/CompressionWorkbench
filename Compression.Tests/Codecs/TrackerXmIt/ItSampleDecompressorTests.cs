#pragma warning disable CS1591
using Codec.TrackerXmIt;

namespace Compression.Tests.Codecs.TrackerXmIt;

/// <summary>
/// Hand-walks the IT214/IT215 sample decompressor over crafted bitstreams. The test owns a
/// small LSB-first bit writer that mirrors the decoder's reader, so the exact packed bytes are
/// constructed in-test and the decoded samples are pinned to the expected delta accumulation
/// (including a width-change escape and the IT215 double-delta pass).
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

  // Helper: at the start of an 8-bit block the width is 9. Width 9 carries 8-bit samples
  // (low 8 bits), with bit 8 reserved as the escape flag; so a "plain" data value is the
  // unsigned 8-bit pattern of the signed sample (bit 8 = 0).
  private static int Sample9(sbyte v) => (byte)v;

  [Test]
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

  [Test]
  public void Decompress8_It215_AppliesDoubleDelta() {
    var w = new BlockWriter();
    foreach (sbyte v in new sbyte[] { 5, -3, 10, -1 })
      w.Write(Sample9(v), 9);
    var block = w.ToBlock();

    var decoded = ItSampleDecompressor.Decompress8(block, 4, it215: true);

    // d1: 5, 2, 12, 11 ; d2: 5, 7, 19, 30
    Assert.That(decoded, Is.EqualTo(new sbyte[] { 5, 7, 19, 30 }));
  }

  [Test]
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

  // At the start of a 16-bit block the width is 17; width 17 carries 16-bit samples (low
  // 16 bits) with bit 16 reserved as the escape flag.
  private static int Sample17(short v) => (ushort)v;

  [Test]
  public void Decompress16_PlainWidth17_AccumulatesSingleDelta() {
    var w = new BlockWriter();
    foreach (short v in new short[] { 1000, -250, 4000 })
      w.Write(Sample17(v), 17);
    var block = w.ToBlock();

    var decoded = ItSampleDecompressor.Decompress16(block, 3, it215: false);

    // d1: 1000, 750, 4750
    Assert.That(decoded, Is.EqualTo(new short[] { 1000, 750, 4750 }));
  }

  [Test]
  public void Decompress16_It215_AppliesDoubleDelta() {
    var w = new BlockWriter();
    foreach (short v in new short[] { 1000, -250, 4000 })
      w.Write(Sample17(v), 17);
    var block = w.ToBlock();

    var decoded = ItSampleDecompressor.Decompress16(block, 3, it215: true);

    // d1: 1000, 750, 4750 ; d2: 1000, 1750, 6500
    Assert.That(decoded, Is.EqualTo(new short[] { 1000, 1750, 6500 }));
  }
}
