#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Alac;

namespace Compression.Tests.Codecs.Alac;

[TestFixture]
public class AlacCodecTests {

  // ── Round-trip through our own encoder + decoder ─────────────────────────────

  private static byte[] MakeMono16(int samples) {
    var pcm = new byte[samples * 2];
    for (var i = 0; i < samples; ++i) {
      // A mix of a ramp and a sine-ish wave so the predictor/rice paths are exercised.
      var v = (int)(8000 * Math.Sin(i * 0.05)) + (i * 7 % 2000) - 1000;
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)v);
    }
    return pcm;
  }

  private static byte[] MakeStereo16(int samples) {
    var pcm = new byte[samples * 2 * 2];
    for (var i = 0; i < samples; ++i) {
      var l = (int)(9000 * Math.Sin(i * 0.03)) + (i % 500) - 250;
      var r = (int)(6000 * Math.Cos(i * 0.07)) - (i % 300);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)l);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)r);
    }
    return pcm;
  }

  private static byte[] MakeStereo24(int samples) {
    var pcm = new byte[samples * 2 * 3];
    for (var i = 0; i < samples; ++i) {
      var l = (int)(2_000_000 * Math.Sin(i * 0.02)) + (i * 13 % 40000);
      var r = (int)(1_500_000 * Math.Cos(i * 0.05)) - (i * 11 % 30000);
      WriteS24(pcm, i * 6, l);
      WriteS24(pcm, i * 6 + 3, r);
    }
    return pcm;
  }

  private static void WriteS24(byte[] b, int off, int v) {
    b[off] = (byte)v;
    b[off + 1] = (byte)(v >> 8);
    b[off + 2] = (byte)(v >> 16);
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Mono16_IsLossless() {
    var pcm = MakeMono16(5000);
    var (frames, cookie) = AlacCodec.Encode(pcm, channels: 1, sampleRate: 44100, bitsPerSample: 16);
    var decoded = AlacCodec.Decode(frames, cookie);
    Assert.That(decoded, Is.EqualTo(pcm));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Stereo16_IsLossless() {
    var pcm = MakeStereo16(5000);
    var (frames, cookie) = AlacCodec.Encode(pcm, channels: 2, sampleRate: 48000, bitsPerSample: 16);
    var decoded = AlacCodec.Decode(frames, cookie);
    Assert.That(decoded, Is.EqualTo(pcm));
    Assert.That(cookie.NumChannels, Is.EqualTo(2));
    Assert.That(cookie.SampleRate, Is.EqualTo(48000u));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Stereo24_IsLossless() {
    var pcm = MakeStereo24(4096 + 123); // spans a partial trailing frame
    var (frames, cookie) = AlacCodec.Encode(pcm, channels: 2, sampleRate: 96000, bitsPerSample: 24);
    var decoded = AlacCodec.Decode(frames, cookie);
    Assert.That(decoded, Is.EqualTo(pcm));
    Assert.That(cookie.BitDepth, Is.EqualTo(24));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_PartialLastFrame_IsLossless() {
    // Not a multiple of frameLength → last frame is a partial frame.
    var pcm = MakeMono16(4096 * 2 + 17);
    var (frames, cookie) = AlacCodec.Encode(pcm, 1, 44100, 16, frameLength: 4096);
    var decoded = AlacCodec.Decode(frames, cookie);
    Assert.That(decoded, Is.EqualTo(pcm));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_Silence_IsLossless() {
    var pcm = new byte[2000 * 2]; // all-zero mono → exercises the zero-run path
    var (frames, cookie) = AlacCodec.Encode(pcm, 1, 44100, 16);
    var decoded = AlacCodec.Decode(frames, cookie);
    Assert.That(decoded, Is.EqualTo(pcm));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_SmallFrameLength_IsLossless() {
    var pcm = MakeStereo16(700);
    var (frames, cookie) = AlacCodec.Encode(pcm, 2, 44100, 16, frameLength: 128);
    var decoded = AlacCodec.Decode(frames, cookie);
    Assert.That(decoded, Is.EqualTo(pcm));
  }

  // ── Cookie parse/write round-trip ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Cookie_WriteThenParse_RoundTrips() {
    var c = new AlacCookie(4096, 0, 16, 40, 10, 14, 2, 255, 0, 0, 44100);
    var parsed = AlacCookie.Parse(c.Write());
    Assert.That(parsed, Is.EqualTo(c));
  }

  [Test, Category("HappyPath")]
  public void Cookie_Parse_SkipsVersionFlagsPrefix() {
    var bare = new AlacCookie(4096, 0, 16, 40, 10, 14, 2, 255, 0, 0, 44100).Write();
    var prefixed = new byte[4 + bare.Length];
    bare.CopyTo(prefixed.AsSpan(4));
    var parsed = AlacCookie.Parse(prefixed);
    Assert.That(parsed.FrameLength, Is.EqualTo(4096u));
    Assert.That(parsed.SampleRate, Is.EqualTo(44100u));
  }

  // ── Hand-built ESCAPE (uncompressed) frame — independent of the encoder ───────

  [Test, Category("EdgeCase")]
  public void Decode_HandBuiltEscapeFrame_Mono_ExactSamples() {
    // Build a single SCE escape frame carrying 4 explicit 16-bit samples, then decode.
    var values = new short[] { 100, -200, 32000, -32000 };
    var w = new TestBitWriter();
    w.Write(0, 3);             // SCE tag
    w.Write(0, 4);             // element instance tag
    w.Write(0, 12);            // unused
    w.WriteOne(1);             // partial frame
    w.Write(0, 2);             // output shift = 0
    w.WriteOne(1);             // escape = 1 (uncompressed)
    w.Write((uint)values.Length, 32);
    foreach (var v in values)
      w.Write((uint)(ushort)v, 16); // bitDepth - outputShift = 16
    w.Write(7, 3);             // END
    var frame = w.ToArray();

    var cookie = new AlacCookie(4096, 0, 16, 40, 10, 14, 1, 255, 0, 0, 44100);
    var decoded = AlacCodec.Decode(frame, cookie);

    var expected = new byte[values.Length * 2];
    for (var i = 0; i < values.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(expected.AsSpan(i * 2), values[i]);
    Assert.That(decoded, Is.EqualTo(expected));
  }

  // Minimal MSB-first bit writer for the hand-built frame (mirrors the codec's writer).
  private sealed class TestBitWriter {
    private readonly List<byte> _bytes = [];
    private int _current;
    private int _filled;

    public void Write(uint value, int count) {
      for (var i = count - 1; i >= 0; --i) {
        _current = (_current << 1) | (int)((value >> i) & 1);
        if (++_filled != 8) continue;
        _bytes.Add((byte)_current);
        _current = 0; _filled = 0;
      }
    }

    public void WriteOne(uint v) => this.Write(v & 1, 1);

    public byte[] ToArray() {
      if (_filled == 0) return _bytes.ToArray();
      var list = new List<byte>(_bytes) { (byte)(_current << (8 - _filled)) };
      return list.ToArray();
    }
  }
}
