#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Shorten;

namespace Compression.Tests.Codecs.Shorten;

[TestFixture]
public class ShortenCodecTests {

  private static byte[] MakeStereo16(int frames) {
    var pcm = new byte[frames * 2 * 2];
    for (var i = 0; i < frames; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(i * 97 % 30000 - 15000));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(-(i * 53 % 20000)));
    }
    return pcm;
  }

  private static byte[] MakeMono8Unsigned(int frames) {
    var pcm = new byte[frames];
    for (var i = 0; i < frames; ++i)
      pcm[i] = (byte)((i * 13 + 7) & 0xFF);
    return pcm;
  }

  private static byte[] Encode(byte[] pcm, int channels, int bits) {
    using var inp = new MemoryStream(pcm);
    using var shn = new MemoryStream();
    ShortenCodec.Compress(inp, shn, channels, 44100, bits);
    return shn.ToArray();
  }

  private static byte[] Decode(byte[] shn) {
    using var inp = new MemoryStream(shn);
    using var outp = new MemoryStream();
    ShortenCodec.Decompress(inp, outp);
    return outp.ToArray();
  }

  [Test]
  public void RoundTrip_Stereo16_OneBlock() {
    var pcm = MakeStereo16(256);
    Assert.That(Decode(Encode(pcm, 2, 16)), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_Stereo16_PartialFinalBlock() {
    var pcm = MakeStereo16(300); // 256 + 44: crosses the block boundary
    Assert.That(Decode(Encode(pcm, 2, 16)), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_Stereo16_MultipleBlocks() {
    var pcm = MakeStereo16(1000);
    Assert.That(Decode(Encode(pcm, 2, 16)), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_Mono8_Unsigned() {
    var pcm = MakeMono8Unsigned(700);
    Assert.That(Decode(Encode(pcm, 1, 8)), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_Mono16_Silence() {
    // All-zero input exercises the FN_DIFF0/low-energy path and the partial trailing byte.
    var pcm = new byte[400 * 2];
    Assert.That(Decode(Encode(pcm, 1, 16)), Is.EqualTo(pcm));
  }

  [Test]
  public void Header_StartsWithMagicAndVersion() {
    var shn = Encode(MakeStereo16(256), 2, 16);
    Assert.That(shn.AsSpan(0, 4).ToArray(), Is.EqualTo("ajkg"u8.ToArray()));
    Assert.That(shn[4], Is.EqualTo(2));
  }

  [Test]
  public void ReadStreamInfo_ReportsChannelsAndBits_NoSampleRate() {
    var shn = Encode(MakeStereo16(256), 2, 16);
    using var ms = new MemoryStream(shn);
    var info = ShortenCodec.ReadStreamInfo(ms);

    Assert.Multiple(() => {
      Assert.That(info.Channels, Is.EqualTo(2));
      Assert.That(info.BitsPerSample, Is.EqualTo(16));
      Assert.That(info.FileType, Is.EqualTo(5)); // signed 16-bit LE
      Assert.That(info.SampleRate, Is.EqualTo(0), "Shorten stores no sample rate.");
    });
  }

  [Test]
  public void ReadStreamInfo_Mono8_ReportsUnsigned8Type() {
    var shn = Encode(MakeMono8Unsigned(256), 1, 8);
    using var ms = new MemoryStream(shn);
    var info = ShortenCodec.ReadStreamInfo(ms);

    Assert.Multiple(() => {
      Assert.That(info.Channels, Is.EqualTo(1));
      Assert.That(info.BitsPerSample, Is.EqualTo(8));
      Assert.That(info.FileType, Is.EqualTo(2)); // unsigned 8-bit
    });
  }

  [Test]
  public void Decompress_RejectsBadMagic() {
    var bad = new byte[64];
    "nope"u8.CopyTo(bad);
    using var inp = new MemoryStream(bad);
    using var outp = new MemoryStream();
    Assert.That(() => ShortenCodec.Decompress(inp, outp), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void Decompress_RejectsUnsupportedVersion() {
    var shn = Encode(MakeStereo16(256), 2, 16);
    shn[4] = 1; // downgrade the version byte
    using var inp = new MemoryStream(shn);
    using var outp = new MemoryStream();
    Assert.That(() => ShortenCodec.Decompress(inp, outp), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void Compress_RejectsUnsupportedBitDepth() {
    using var inp = new MemoryStream(new byte[12]);
    using var outp = new MemoryStream();
    Assert.That(() => ShortenCodec.Compress(inp, outp, 1, 44100, 24),
      Throws.TypeOf<ArgumentOutOfRangeException>());
  }
}
