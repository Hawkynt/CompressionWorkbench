#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.MonkeysAudio;

namespace Compression.Tests.Codecs.MonkeysAudio;

[TestFixture]
public class MonkeysAudioCodecTests {

  private static byte[] Encode(byte[] pcm, int channels, int sampleRate, int bits) {
    using var input = new MemoryStream(pcm);
    using var output = new MemoryStream();
    MonkeysAudioCodec.Compress(input, output, channels, sampleRate, bits);
    return output.ToArray();
  }

  private static byte[] Decode(byte[] ape) {
    using var input = new MemoryStream(ape);
    using var output = new MemoryStream();
    MonkeysAudioCodec.Decompress(input, output);
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
  public void RoundTrip_MultiFrame_StereoNoise_IsLossless() {
    // Exceeds blocks-per-frame (73728) so the stream carries several frames and a
    // shorter trailing frame.
    const int frames = 150000;
    var rng = new Random(31337);
    var pcm = new byte[frames * 2 * 2];
    rng.NextBytes(pcm);
    Assert.That(RoundTrip(pcm, 2, 44100, 16), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_SingleSample_IsLossless() {
    var pcm = new byte[2 * 2];
    BinaryPrimitives.WriteInt16LittleEndian(pcm, 12345);
    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2), -9999);
    Assert.That(RoundTrip(pcm, 2, 44100, 16), Is.EqualTo(pcm));
  }

  // ── Header parsing ────────────────────────────────────────────────────────────

  [Test]
  public void Encoded_Stream_StartsWith_MacMagic_And_Version() {
    var pcm = new byte[1000 * 4];
    var ape = Encode(pcm, 2, 44100, 16);
    Assert.That(System.Text.Encoding.ASCII.GetString(ape, 0, 4), Is.EqualTo("MAC "));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(ape.AsSpan(4)), Is.EqualTo(3990), "version");
  }

  [Test]
  public void ReadStreamInfo_ReturnsHeaderFields_Stereo() {
    const int frames = 1234;
    var pcm = new byte[frames * 2 * 2];
    var ape = Encode(pcm, 2, 44100, 16);

    using var ms = new MemoryStream(ape);
    var info = MonkeysAudioCodec.ReadStreamInfo(ms);

    Assert.That(info.Channels, Is.EqualTo(2));
    Assert.That(info.SampleRate, Is.EqualTo(44100));
    Assert.That(info.BitsPerSample, Is.EqualTo(16));
    Assert.That(info.TotalSamples, Is.EqualTo(frames));
    Assert.That(info.CompressionLevel, Is.EqualTo(1000));
    Assert.That(info.Version, Is.EqualTo(3990));
  }

  [Test]
  public void ReadStreamInfo_ReturnsHeaderFields_Mono() {
    const int frames = 500;
    var pcm = new byte[frames * 2];
    var ape = Encode(pcm, 1, 22050, 16);

    using var ms = new MemoryStream(ape);
    var info = MonkeysAudioCodec.ReadStreamInfo(ms);

    Assert.That(info.Channels, Is.EqualTo(1));
    Assert.That(info.SampleRate, Is.EqualTo(22050));
    Assert.That(info.TotalSamples, Is.EqualTo(frames));
  }

  // ── Rejection paths ───────────────────────────────────────────────────────────

  [Test]
  public void HigherCompressionLevel_Rejected_OnDecode() {
    var pcm = new byte[1000 * 4];
    var ape = Encode(pcm, 2, 44100, 16);
    // Overwrite the compression level in APE_HEADER (offset = descriptor 52).
    BinaryPrimitives.WriteUInt16LittleEndian(ape.AsSpan(52), 2000); // "normal"

    using var input = new MemoryStream(ape);
    using var output = new MemoryStream();
    Assert.That(() => MonkeysAudioCodec.Decompress(input, output), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void CorruptedMagic_Rejected() {
    var pcm = new byte[1000 * 4];
    var ape = Encode(pcm, 2, 44100, 16);
    ape[0] = (byte)'X';

    using var input = new MemoryStream(ape);
    using var output = new MemoryStream();
    Assert.That(() => MonkeysAudioCodec.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void NonApeInput_Throws() {
    var junk = new byte[128];
    new Random(1).NextBytes(junk);
    junk[0] = (byte)'X'; junk[1] = (byte)'Y';

    using var input = new MemoryStream(junk);
    using var output = new MemoryStream();
    Assert.That(() => MonkeysAudioCodec.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void LegacyVersion_Rejected() {
    var pcm = new byte[1000 * 4];
    var ape = Encode(pcm, 2, 44100, 16);
    BinaryPrimitives.WriteUInt16LittleEndian(ape.AsSpan(4), 3950); // pre-3.98 descriptor layout

    using var input = new MemoryStream(ape);
    using var output = new MemoryStream();
    Assert.That(() => MonkeysAudioCodec.Decompress(input, output), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void Compress_RejectsBadChannelCount() {
    using var input = new MemoryStream(new byte[12]);
    using var output = new MemoryStream();
    Assert.That(() => MonkeysAudioCodec.Compress(input, output, 3, 44100, 16),
      Throws.TypeOf<ArgumentOutOfRangeException>());
  }
}
