#pragma warning disable CS1591
using SwfCodec = Codec.AdpcmX.Swf;

namespace Compression.Tests.Codecs.AdpcmX;

[TestFixture]
public class SwfTests {

  // Minimal MSB-first bit writer mirroring ffmpeg's get_bits ordering used by the decoder.
  private sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private int _cur;
    private int _bits;

    public void Write(int value, int count) {
      for (var i = count - 1; i >= 0; --i) {
        _cur = (_cur << 1) | ((value >> i) & 1);
        if (++_bits == 8) { _bytes.Add((byte)_cur); _cur = 0; _bits = 0; }
      }
    }

    public byte[] ToArray() {
      if (_bits > 0) _bytes.Add((byte)(_cur << (8 - _bits)));
      return [.. _bytes];
    }
  }

  // codeWidth 4 (2-bit field = 2). Block header: 16-bit init sample 100, 6-bit index 0.
  // First emitted sample is the literal init sample (100). Then a 4-bit code 0b0001 (sign 0,
  // magnitude 1): step[0]=7, vpdiff = 7>>3 = 0, magnitude bit0 ⇒ += 7>>2 = 1 ⇒ vpdiff 1,
  // pred 100+1 = 101. Next code 0b0001 again: index walked to 0 (table[1]=-1, clamped), step[0]=7
  // again ⇒ +1 ⇒ 102.
  [Test]
  public void Swf_Mono_FirstSamples() {
    var w = new BitWriter();
    w.Write(2, 2);        // codeWidth - 2 = 2 ⇒ width 4
    w.Write(100, 16);     // init sample
    w.Write(0, 6);        // init index
    w.Write(0b0001, 4);   // code 1
    w.Write(0b0001, 4);   // code 1
    var pcm = SwfCodec.Decode(w.ToArray(), channels: 1);
    Assert.That(pcm.Length, Is.GreaterThanOrEqualTo(3));
    Assert.That(pcm[0], Is.EqualTo(100));
    Assert.That(pcm[1], Is.EqualTo(101));
    Assert.That(pcm[2], Is.EqualTo(102));
  }

  // A negative code (sign bit set) subtracts: code 0b1001 (sign bit3 set, magnitude 1) ⇒ -1.
  [Test]
  public void Swf_SignBitSubtracts() {
    var w = new BitWriter();
    w.Write(2, 2);        // width 4
    w.Write(500, 16);
    w.Write(0, 6);
    w.Write(0b1001, 4);   // sign set, magnitude 1 ⇒ vpdiff 1, subtract
    var pcm = SwfCodec.Decode(w.ToArray(), channels: 1);
    Assert.That(pcm[0], Is.EqualTo(500));
    Assert.That(pcm[1], Is.EqualTo(499));
  }

  [Test]
  public void Swf_RejectsBadChannels()
    => Assert.Throws<ArgumentException>(() => SwfCodec.Decode(new byte[4], channels: 3));
}
