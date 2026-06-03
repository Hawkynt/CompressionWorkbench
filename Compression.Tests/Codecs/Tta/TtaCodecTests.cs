#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Tta;

namespace Compression.Tests.Codecs.Tta;

[TestFixture]
public class TtaCodecTests {

  private static byte[] Encode(byte[] pcm, int channels, int sampleRate, int bits) {
    using var input = new MemoryStream(pcm);
    using var output = new MemoryStream();
    TtaCodec.Compress(input, output, channels, sampleRate, bits);
    return output.ToArray();
  }

  private static byte[] Decode(byte[] tta) {
    using var input = new MemoryStream(tta);
    using var output = new MemoryStream();
    TtaCodec.Decompress(input, output);
    return output.ToArray();
  }

  private static byte[] RoundTrip(byte[] pcm, int channels, int sampleRate, int bits)
    => Decode(Encode(pcm, channels, sampleRate, bits));

  // ── Lossless round-trips across depths and channel counts ──────────────────

  [Test]
  public void RoundTrip_16BitStereo_SineAndNoise_IsLossless() {
    const int frames = 4096;
    var rng = new Random(1234);
    var pcm = new byte[frames * 2 * 2];
    for (var i = 0; i < frames; ++i) {
      var l = (short)(Math.Sin(i * 0.05) * 20000 + (rng.Next(2001) - 1000));
      var r = (short)(Math.Cos(i * 0.03) * 15000 + (rng.Next(401) - 200));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), l);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), r);
    }

    Assert.That(RoundTrip(pcm, 2, 44100, 16), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_8BitMono_IsLossless() {
    const int frames = 5000;
    var rng = new Random(99);
    var pcm = new byte[frames];
    for (var i = 0; i < frames; ++i)
      pcm[i] = (byte)(128 + (int)(Math.Sin(i * 0.1) * 60) + (rng.Next(11) - 5));

    Assert.That(RoundTrip(pcm, 1, 22050, 8), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_24BitStereo_IsLossless() {
    const int frames = 3000;
    var rng = new Random(7);
    var pcm = new byte[frames * 2 * 3];
    for (var i = 0; i < frames; ++i) {
      for (var c = 0; c < 2; ++c) {
        var v = (int)(Math.Sin(i * 0.02 + c) * 4_000_000) + rng.Next(20001) - 10000;
        var off = (i * 2 + c) * 3;
        pcm[off] = (byte)(v & 0xFF);
        pcm[off + 1] = (byte)((v >> 8) & 0xFF);
        pcm[off + 2] = (byte)((v >> 16) & 0xFF);
      }
    }

    Assert.That(RoundTrip(pcm, 2, 48000, 24), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_5Point1SixChannel_IsLossless() {
    const int frames = 2000;
    var rng = new Random(2024);
    const int ch = 6;
    var pcm = new byte[frames * ch * 2];
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < ch; ++c) {
        var v = (short)(Math.Sin(i * (0.01 * (c + 1))) * 12000 + (rng.Next(801) - 400));
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * ch + c) * 2), v);
      }

    Assert.That(RoundTrip(pcm, ch, 48000, 16), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_32BitStereo_IsLossless() {
    const int frames = 1500;
    var rng = new Random(55);
    var pcm = new byte[frames * 2 * 4];
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < 2; ++c) {
        var v = (int)(Math.Sin(i * 0.02 + c) * 1_000_000_000) + rng.Next(40001) - 20000;
        BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan((i * 2 + c) * 4), v);
      }

    Assert.That(RoundTrip(pcm, 2, 96000, 32), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_MultipleFrames_SpansSeekTable() {
    // > 1 s of audio at 44.1 kHz forces several frames + a shorter trailing frame.
    const int frames = 44100 * 3 + 123;
    var pcm = new byte[frames * 2];
    for (var i = 0; i < frames; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)((i * 37) % 5000 - 2500));

    var tta = Encode(pcm, 1, 44100, 16);
    Assert.That(Decode(tta), Is.EqualTo(pcm));
  }

  // ── Header field assertions ─────────────────────────────────────────────────

  [Test]
  public void Header_HasExpectedTta1Layout() {
    const int frames = 1000;
    var pcm = new byte[frames * 2 * 2];
    var tta = Encode(pcm, 2, 44100, 16);

    Assert.That(System.Text.Encoding.ASCII.GetString(tta, 0, 4), Is.EqualTo("TTA1"));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(tta.AsSpan(4)), Is.EqualTo(1), "audio format = integer PCM");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(tta.AsSpan(6)), Is.EqualTo(2), "channels");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(tta.AsSpan(8)), Is.EqualTo(16), "bits per sample");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(tta.AsSpan(10)), Is.EqualTo(44100u), "sample rate");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(tta.AsSpan(14)), Is.EqualTo((uint)frames), "data length");
  }

  [Test]
  public void ReadStreamInfo_ReturnsHeaderFields() {
    const int frames = 1234;
    var pcm = new byte[frames * 2 * 3];
    var tta = Encode(pcm, 2, 48000, 24);

    using var ms = new MemoryStream(tta);
    var info = TtaCodec.ReadStreamInfo(ms);

    Assert.That(info.Channels, Is.EqualTo(2));
    Assert.That(info.SampleRate, Is.EqualTo(48000));
    Assert.That(info.BitsPerSample, Is.EqualTo(24));
    Assert.That(info.SampleCount, Is.EqualTo(frames));
  }

  // ── Corruption detection ────────────────────────────────────────────────────

  [Test]
  public void CorruptedHeaderCrc_Throws() {
    var pcm = new byte[2000];
    var tta = Encode(pcm, 1, 44100, 16);
    tta[6] ^= 0xFF; // flip channel count without fixing the header CRC

    using var input = new MemoryStream(tta);
    using var output = new MemoryStream();
    Assert.That(() => TtaCodec.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void CorruptedFrameCrc_Throws() {
    const int frames = 2000;
    var pcm = new byte[frames * 2];
    for (var i = 0; i < frames; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(i % 1000));
    var tta = Encode(pcm, 1, 44100, 16);

    // Flip a byte inside the first frame's coded payload (past header + seek table).
    var frameStart = 18 + 4 + 1 * 4 + 4;
    tta[frameStart + 2] ^= 0x80;

    using var input = new MemoryStream(tta);
    using var output = new MemoryStream();
    Assert.That(() => TtaCodec.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void NonTtaInput_Throws() {
    var junk = new byte[64];
    new Random(1).NextBytes(junk);
    junk[0] = (byte)'X';

    using var input = new MemoryStream(junk);
    using var output = new MemoryStream();
    Assert.That(() => TtaCodec.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }
}
