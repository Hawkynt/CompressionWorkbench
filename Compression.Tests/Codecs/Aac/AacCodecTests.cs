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

  // ---------- End-to-end decode (hand-crafted ADTS frames) ----------
  //
  // These build minimal but spec-valid AAC-LC raw_data_blocks by hand and run the
  // full Huffman/dequant/IMDCT/overlap-add pipeline. A "silence" frame codes every
  // scale-factor band with codebook 0 (ZERO_HCB), so no spectral bits are present
  // and the spectrum is all zeros — which the filter bank must turn into exactly
  // 1024 zero samples per channel.

  [Test]
  [Category("HappyPath")]
  public void Decompress_SilenceMonoFrame_Produces1024Zeros() {
    var frame = AacTestFrames.SilenceFrame(channelConfig: 1, sampleRateIndex: 4);
    using var input = new MemoryStream(frame);
    using var pcm = new MemoryStream();
    AacCodec.Decompress(input, pcm);

    Assert.That(pcm.Length, Is.EqualTo(1024 * 2), "1024 mono samples × 2 bytes");
    Assert.That(pcm.ToArray(), Is.All.EqualTo((byte)0), "silence decodes to zeros");
  }

  [Test]
  [Category("HappyPath")]
  public void Decompress_TwoSilenceFrames_Produces2048Zeros() {
    var frame = AacTestFrames.SilenceFrame(channelConfig: 1, sampleRateIndex: 4);
    var two = new byte[frame.Length * 2];
    frame.CopyTo(two, 0);
    frame.CopyTo(two, frame.Length);

    using var input = new MemoryStream(two);
    using var pcm = new MemoryStream();
    AacCodec.Decompress(input, pcm);

    Assert.That(pcm.Length, Is.EqualTo(2048 * 2));
    Assert.That(pcm.ToArray(), Is.All.EqualTo((byte)0));
  }

  [Test]
  [Category("HappyPath")]
  public void Decompress_SilenceStereoFrame_ProducesInterleavedZeros() {
    var frame = AacTestFrames.SilenceFrame(channelConfig: 2, sampleRateIndex: 4);
    using var input = new MemoryStream(frame);
    using var pcm = new MemoryStream();
    AacCodec.Decompress(input, pcm);

    Assert.That(pcm.Length, Is.EqualTo(1024 * 2 /*ch*/ * 2 /*bytes*/));
    Assert.That(pcm.ToArray(), Is.All.EqualTo((byte)0));
  }

  [Test]
  [Category("HappyPath")]
  public void Decompress_FrameWithOneNonzeroCoefficient_HasEnergyAndIsDeterministic() {
    // A mono frame coding a single non-zero quantised coefficient in sfb 0 via the
    // escape codebook (cb 11). Hand-computing the exact IMDCT output is impractical,
    // so we assert (a) the output is non-trivial (non-zero energy) and (b) repeated
    // decodes are bit-identical (determinism).
    var frame = AacTestFrames.SingleCoefficientFrame(sampleRateIndex: 4);

    static byte[] Decode(byte[] f) {
      using var input = new MemoryStream(f);
      using var pcm = new MemoryStream();
      AacCodec.Decompress(input, pcm);
      return pcm.ToArray();
    }

    var first = Decode(frame);
    var second = Decode(frame);

    Assert.That(first, Has.Length.EqualTo(1024 * 2));
    Assert.That(first.Any(b => b != 0), Is.True, "a non-zero coefficient must yield audible energy");
    Assert.That(second, Is.EqualTo(first), "decode must be deterministic");
  }

  [Test]
  public void Decompress_TruncatedSpectralData_Throws() {
    // Take a valid single-coefficient frame and lop off its trailing payload byte
    // so the bit reader runs off the end mid-codeword.
    var frame = AacTestFrames.SingleCoefficientFrame(sampleRateIndex: 4);
    var truncated = frame[..^1];
    // Fix the ADTS frame_length so the header still claims the (now missing) byte,
    // forcing the decoder to read past the available data.
    using var input = new MemoryStream(truncated);
    using var pcm = new MemoryStream();
    Assert.That(() => AacCodec.Decompress(input, pcm),
      Throws.InstanceOf<InvalidDataException>());
  }
}
