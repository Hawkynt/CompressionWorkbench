#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.WavPack;

namespace Compression.Tests.Codecs.WavPack;

[TestFixture]
public class WavPackCodecTests {

  private static byte[] Encode(byte[] pcm, int channels, int sampleRate, int bits) {
    using var input = new MemoryStream(pcm);
    using var output = new MemoryStream();
    WavPackCodec.Compress(input, output, channels, sampleRate, bits);
    return output.ToArray();
  }

  private static byte[] Decode(byte[] wv) {
    using var input = new MemoryStream(wv);
    using var output = new MemoryStream();
    WavPackCodec.Decompress(input, output);
    return output.ToArray();
  }

  private static byte[] RoundTrip(byte[] pcm, int channels, int sampleRate, int bits)
    => Decode(Encode(pcm, channels, sampleRate, bits));

  // ── Lossless round-trips ─────────────────────────────────────────────────────

  [Test]
  public void RoundTrip_16BitStereo_SineAndNoise_IsLossless() {
    const int frames = 8192;
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
  public void RoundTrip_16BitMono_IsLossless() {
    const int frames = 6000;
    var rng = new Random(99);
    var pcm = new byte[frames * 2];
    for (var i = 0; i < frames; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2),
        (short)(Math.Sin(i * 0.1) * 9000 + (rng.Next(401) - 200)));

    Assert.That(RoundTrip(pcm, 1, 22050, 16), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_16BitStereo_PureNoise_IsLossless() {
    const int frames = 4000;
    var rng = new Random(2024);
    var pcm = new byte[frames * 2 * 2];
    rng.NextBytes(pcm);
    Assert.That(RoundTrip(pcm, 2, 48000, 16), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_16BitStereo_Silence_IsLossless() {
    const int frames = 3000;
    var pcm = new byte[frames * 2 * 2]; // all zero
    Assert.That(RoundTrip(pcm, 2, 44100, 16), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_8BitMono_IsLossless() {
    const int frames = 5000;
    var rng = new Random(7);
    var pcm = new byte[frames];
    for (var i = 0; i < frames; ++i)
      pcm[i] = (byte)(128 + (int)(Math.Sin(i * 0.1) * 60) + (rng.Next(11) - 5));

    Assert.That(RoundTrip(pcm, 1, 8000, 8), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_24BitStereo_IsLossless() {
    const int frames = 3000;
    var rng = new Random(55);
    var pcm = new byte[frames * 2 * 3];
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < 2; ++c) {
        var v = (int)(Math.Sin(i * 0.02 + c) * 4_000_000) + rng.Next(20001) - 10000;
        var off = (i * 2 + c) * 3;
        pcm[off] = (byte)(v & 0xFF);
        pcm[off + 1] = (byte)((v >> 8) & 0xFF);
        pcm[off + 2] = (byte)((v >> 16) & 0xFF);
      }

    Assert.That(RoundTrip(pcm, 2, 96000, 24), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_5Point1SixChannel_IsLossless() {
    const int frames = 2000;
    const int ch = 6;
    var rng = new Random(321);
    var pcm = new byte[frames * ch * 2];
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < ch; ++c)
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * ch + c) * 2),
          (short)(Math.Sin(i * (0.01 * (c + 1))) * 12000 + (rng.Next(801) - 400)));

    // Six channels => three stereo sub-blocks chained, final flagged.
    Assert.That(RoundTrip(pcm, ch, 48000, 16), Is.EqualTo(pcm));
  }

  // ── Header / flag parsing ─────────────────────────────────────────────────────

  [Test]
  public void Encoded_Stream_StartsWith_WvpkMagic() {
    var pcm = new byte[1000 * 4];
    var wv = Encode(pcm, 2, 44100, 16);
    Assert.That(System.Text.Encoding.ASCII.GetString(wv, 0, 4), Is.EqualTo("wvpk"));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wv.AsSpan(8)), Is.EqualTo(0x0410), "version");
  }

  [Test]
  public void ReadStreamInfo_ReturnsHeaderFields_Stereo() {
    const int frames = 1234;
    var pcm = new byte[frames * 2 * 2];
    var wv = Encode(pcm, 2, 44100, 16);

    using var ms = new MemoryStream(wv);
    var info = WavPackCodec.ReadStreamInfo(ms);

    Assert.That(info.Channels, Is.EqualTo(2));
    Assert.That(info.SampleRate, Is.EqualTo(44100));
    Assert.That(info.BitsPerSample, Is.EqualTo(16));
    Assert.That(info.SampleCount, Is.EqualTo(frames));
  }

  [Test]
  public void ReadStreamInfo_ReturnsHeaderFields_SixChannel() {
    const int frames = 500;
    var pcm = new byte[frames * 6 * 2];
    var wv = Encode(pcm, 6, 48000, 16);

    using var ms = new MemoryStream(wv);
    var info = WavPackCodec.ReadStreamInfo(ms);

    Assert.That(info.Channels, Is.EqualTo(6));
    Assert.That(info.SampleRate, Is.EqualTo(48000));
    Assert.That(info.SampleCount, Is.EqualTo(frames));
  }

  // ── Rejection paths ───────────────────────────────────────────────────────────

  [Test]
  public void CorruptedMagic_Throws() {
    var pcm = new byte[1000 * 4];
    var wv = Encode(pcm, 2, 44100, 16);
    wv[0] = (byte)'X';

    using var input = new MemoryStream(wv);
    using var output = new MemoryStream();
    Assert.That(() => WavPackCodec.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void HybridFlag_Rejected() {
    var pcm = new byte[1000 * 4];
    var wv = Encode(pcm, 2, 44100, 16);
    // Set hybrid bit (bit 3) in the flags word at offset 24.
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(wv.AsSpan(24));
    flags |= 0x8;
    BinaryPrimitives.WriteUInt32LittleEndian(wv.AsSpan(24), flags);

    using var input = new MemoryStream(wv);
    using var output = new MemoryStream();
    Assert.That(() => WavPackCodec.Decompress(input, output), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void FloatFlag_Rejected() {
    var pcm = new byte[1000 * 4];
    var wv = Encode(pcm, 2, 44100, 16);
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(wv.AsSpan(24));
    flags |= 0x80; // float data
    BinaryPrimitives.WriteUInt32LittleEndian(wv.AsSpan(24), flags);

    using var input = new MemoryStream(wv);
    using var output = new MemoryStream();
    Assert.That(() => WavPackCodec.Decompress(input, output), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void NonWavPackInput_Throws() {
    var junk = new byte[128];
    new Random(1).NextBytes(junk);
    junk[0] = (byte)'X'; junk[1] = (byte)'Y';

    using var input = new MemoryStream(junk);
    using var output = new MemoryStream();
    Assert.That(() => WavPackCodec.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }
}
