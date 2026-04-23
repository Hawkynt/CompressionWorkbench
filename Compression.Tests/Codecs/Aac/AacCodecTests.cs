using Codec.Aac;

namespace Compression.Tests.Codecs.Aac;

[TestFixture]
public class AacCodecTests {

  // ---------- ADTS header parse ----------

  [Test]
  [Category("HappyPath")]
  public void ParseHeader_LcStereo44_1kHzFrame256_DecodesAllFields() {
    // profile=1 (LC, since profile = ObjectType - 1)
    // sample rate index 4 = 44100 Hz
    // channel config 2 = stereo
    // frame length 256 bytes
    var header = AacAdtsReader.BuildHeader(
      profile: 1, sampleRateIndex: 4, channelConfig: 2, frameLength: 256);

    Assert.That(header, Has.Length.EqualTo(7));
    var parsed = AacAdtsReader.ParseHeader(header);

    Assert.Multiple(() => {
      Assert.That(parsed.Profile, Is.EqualTo(1), "profile field");
      Assert.That(parsed.ObjectType, Is.EqualTo(AacObjectType.AacLc), "object type = LC");
      Assert.That(parsed.SampleRateIndex, Is.EqualTo(4));
      Assert.That(parsed.SampleRate, Is.EqualTo(44100));
      Assert.That(parsed.ChannelConfiguration, Is.EqualTo(2));
      Assert.That(parsed.FrameLength, Is.EqualTo(256));
      Assert.That(parsed.NumberOfRawDataBlocks, Is.EqualTo(0));
      Assert.That(parsed.ProtectionAbsent, Is.True);
      Assert.That(parsed.HeaderLengthBytes, Is.EqualTo(7));
      Assert.That(parsed.IsMpeg2, Is.False);
    });
  }

  [Test]
  public void ParseHeader_RejectsMissingSyncWord() {
    var bytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    Assert.That(() => AacAdtsReader.ParseHeader(bytes), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  public void ParseHeader_RejectsNonZeroLayer() {
    var header = AacAdtsReader.BuildHeader(1, 4, 2, 256);
    header[1] |= 0b0000_0010; // poison the layer bits
    Assert.That(() => AacAdtsReader.ParseHeader(header), Throws.InstanceOf<InvalidDataException>());
  }

  // ---------- Profile rejection ----------

  [Test]
  [Category("ProfileRejection")]
  public void Decompress_RejectsMainProfile() {
    var header = AacAdtsReader.BuildHeader(profile: 0, sampleRateIndex: 4, channelConfig: 2, frameLength: 8);
    using var ms = new MemoryStream(header);
    Assert.That(() => AacCodec.Decompress(ms, new MemoryStream()),
      Throws.InstanceOf<NotSupportedException>().With.Message.Contains("Main profile"));
  }

  [Test]
  [Category("ProfileRejection")]
  public void Decompress_RejectsLtpProfile() {
    var header = AacAdtsReader.BuildHeader(profile: 3, sampleRateIndex: 4, channelConfig: 2, frameLength: 8);
    using var ms = new MemoryStream(header);
    Assert.That(() => AacCodec.Decompress(ms, new MemoryStream()),
      Throws.InstanceOf<NotSupportedException>().With.Message.Contains("LTP profile"));
  }

  [Test]
  [Category("ProfileRejection")]
  public void Decompress_RejectsSsrProfile() {
    var header = AacAdtsReader.BuildHeader(profile: 2, sampleRateIndex: 4, channelConfig: 2, frameLength: 8);
    using var ms = new MemoryStream(header);
    Assert.That(() => AacCodec.Decompress(ms, new MemoryStream()),
      Throws.InstanceOf<NotSupportedException>().With.Message.Contains("SSR profile"));
  }

  // ---------- HE-AAC rejection via AudioSpecificConfig ----------

  [Test]
  [Category("ProfileRejection")]
  public void ParseAudioSpecificConfig_RejectsHeAacSbr() {
    // 5 bits object type = 5 (SBR), 4 bits sr idx = 4 (44.1k), 4 bits channel cfg = 2.
    // Bits: 00101 0100 0010 0... -> 0010 1010 0001 0000
    var asc = new byte[] { 0x2A, 0x10 };
    Assert.That(() => AacCodec.ParseAudioSpecificConfig(asc),
      Throws.InstanceOf<NotSupportedException>().With.Message.Contains("HE-AAC"));
  }

  [Test]
  [Category("ProfileRejection")]
  public void ParseAudioSpecificConfig_RejectsHeAacV2Ps() {
    // object type = 31 (escape), then +6 bits = 29 - 32 ... actually: ot escape encodes ot >= 32.
    // Easier: object type = 29 (PS) directly. 5 bits = 29 = 11101.
    // Bits: 11101 0100 0010 0... -> 1110 1010 0001 0000
    var asc = new byte[] { 0xEA, 0x10 };
    Assert.That(() => AacCodec.ParseAudioSpecificConfig(asc),
      Throws.InstanceOf<NotSupportedException>());
  }

  [Test]
  [Category("HappyPath")]
  public void ParseAudioSpecificConfig_AcceptsAacLc() {
    // object type = 2 (LC) = 00010, sr idx = 4 = 0100, ch cfg = 2 = 0010
    // Bits: 00010 0100 0010 0... -> 0001 0010 0001 0000
    var asc = new byte[] { 0x12, 0x10 };
    var (ot, srIdx, ch) = AacCodec.ParseAudioSpecificConfig(asc);
    Assert.Multiple(() => {
      Assert.That(ot, Is.EqualTo(AacObjectType.AacLc));
      Assert.That(srIdx, Is.EqualTo(4));
      Assert.That(ch, Is.EqualTo(2));
    });
  }

  // ---------- ReadStreamInfo ----------

  [Test]
  [Category("HappyPath")]
  public void ReadStreamInfo_FromSingleAdtsFrame_ReportsRateAndChannels() {
    var header = AacAdtsReader.BuildHeader(profile: 1, sampleRateIndex: 4, channelConfig: 2, frameLength: 7);
    using var ms = new MemoryStream(header);
    var info = AacCodec.ReadStreamInfo(ms);
    Assert.Multiple(() => {
      Assert.That(info.SampleRate, Is.EqualTo(44100));
      Assert.That(info.Channels, Is.EqualTo(2));
      Assert.That(info.Profile, Is.EqualTo((int)AacObjectType.AacLc));
      Assert.That(info.DurationSamples, Is.EqualTo(1024));
    });
  }

  // ---------- End-to-end decode ----------
  //
  // Skipped by default: the spectral / IMDCT / Huffman pipeline is not yet
  // implemented (the 11 spectral codebooks + HCB_SF + filter bank tables remain
  // as TODO markers in AacHuffmanTables.cs and AacFilterBank.cs). When a short
  // permissively-licensed .aac (ADTS) clip is added under test-corpus/aac/,
  // change [Ignore] to [Test] and assert sample count + no exceptions.
  [Test]
  [Ignore("End-to-end decode requires spectral pipeline + reference clip; see AacHuffmanTables TODO.")]
  public void Decompress_EndToEnd_ShortClip() {
    var clip = Path.Combine(TestContext.CurrentContext.TestDirectory,
      "..", "..", "..", "..", "test-corpus", "aac", "sample-lc-stereo-44k.aac");
    if (!File.Exists(clip))
      Assert.Inconclusive($"Reference AAC-LC clip not present: {clip}");

    using var input = File.OpenRead(clip);
    using var pcm = new MemoryStream();
    AacCodec.Decompress(input, pcm);
    Assert.That(pcm.Length, Is.GreaterThan(0));
  }
}
