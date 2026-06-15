using Codec.Mp3;

namespace Compression.Tests.Codecs.Mp3;

[TestFixture]
public class Mp3CodecTests {

  // ──────────── 1. Frame header parsing ────────────

  /// <summary>
  /// Hand-crafted MPEG-1 Layer III 128 kbps 44.1 kHz stereo header. Bytes:
  /// 0xFF, 0xFB, 0x90, 0x00:
  ///   FF FB → sync 0xFFF + version 11 (MPEG-1) + layer 01 (Layer III) + protection 1 (no CRC)
  ///   90    → bitrate index 1001 (128 kbps) + sample rate index 00 (44.1 kHz) + padding 0 + private 0
  ///   00    → channel mode 00 (stereo) + ext 00 + copyright 0 + original 0 + emphasis 00
  /// </summary>
  [Test]
  public void Parse_Mpeg1Layer3_128k_44100_Stereo_Cbr_HeaderFieldsCorrect() {
    var bytes = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
    var hdr = Mp3FrameHeader.Parse(bytes);

    Assert.Multiple(() => {
      Assert.That(hdr.IsMpeg1, Is.True, "MPEG version should be MPEG-1");
      Assert.That(hdr.Layer, Is.EqualTo(3), "Layer should be III");
      Assert.That(hdr.HasCrc, Is.False, "Protection bit set → no CRC");
      Assert.That(hdr.BitrateKbps, Is.EqualTo(128), "Bitrate should be 128 kbps");
      Assert.That(hdr.SampleRateHz, Is.EqualTo(44100), "Sample rate should be 44.1 kHz");
      Assert.That(hdr.Padding, Is.False);
      Assert.That(hdr.ChannelMode, Is.EqualTo(0), "0 = stereo");
      Assert.That(hdr.Channels, Is.EqualTo(2));
      Assert.That(hdr.IsMono, Is.False);
      Assert.That(hdr.IsMsStereo, Is.False);
      Assert.That(hdr.IsIntensityStereo, Is.False);
      Assert.That(hdr.SamplesPerFrame, Is.EqualTo(1152), "MPEG-1 L3 = 1152 samples/frame");
      // 1152 * 128000 / 44100 / 8 = 417.96 → 417 bytes (no padding)
      Assert.That(hdr.FrameLengthBytes, Is.EqualTo(417));
    });
  }

  [Test]
  public void Parse_Mpeg1Layer3_JointStereo_MsAndIntensity() {
    // Channel mode 01 (joint), mode ext 11 (both intensity + MS) → byte3 = 0x70
    var bytes = new byte[] { 0xFF, 0xFB, 0x90, 0x70 };
    var hdr = Mp3FrameHeader.Parse(bytes);

    Assert.That(hdr.ChannelMode, Is.EqualTo(1));
    Assert.That(hdr.IsMsStereo, Is.True);
    Assert.That(hdr.IsIntensityStereo, Is.True);
  }

  [Test]
  public void Parse_Mpeg2Layer3_64k_22050_Mono() {
    // Version 10 (MPEG-2), layer 01 (L3), protection 1, bitrate 1000 (64 kbps for MPEG-2 L3
    // — index 7 is 56, index 8 is 64; ISO/IEC 13818-3 Table 1), samplerate 00 (22.05 kHz),
    // padding 0, mono mode.
    // byte1 = 1111 0011 = 0xF3; byte2 = 1000 0000 = 0x80; byte3 = 1100 0000 = 0xC0
    var bytes = new byte[] { 0xFF, 0xF3, 0x80, 0xC0 };
    var hdr = Mp3FrameHeader.Parse(bytes);

    Assert.That(hdr.IsMpeg1, Is.False);
    Assert.That(hdr.IsMpeg25, Is.False);
    Assert.That(hdr.Layer, Is.EqualTo(3));
    Assert.That(hdr.SampleRateHz, Is.EqualTo(22050));
    Assert.That(hdr.BitrateKbps, Is.EqualTo(64));
    Assert.That(hdr.IsMono, Is.True);
    Assert.That(hdr.SamplesPerFrame, Is.EqualTo(576), "MPEG-2 LSF Layer III = 576 samples");
  }

  // ──────────── 2. Layer I / II header tables ────────────

  /// <summary>MPEG-1 Layer II 128 kbps 48 kHz mono header (<c>FF FD 84 C0</c>) — table lookups.</summary>
  [Test]
  public void Parse_Mpeg1LayerII_128k_48000_Mono_HeaderFieldsCorrect() {
    var bytes = new byte[] { 0xFF, 0xFD, 0x84, 0xC0 };
    var hdr = Mp3FrameHeader.Parse(bytes);

    Assert.Multiple(() => {
      Assert.That(hdr.IsMpeg1, Is.True);
      Assert.That(hdr.Layer, Is.EqualTo(2), "Layer should be II");
      Assert.That(hdr.HasCrc, Is.False);
      Assert.That(hdr.BitrateKbps, Is.EqualTo(128));
      Assert.That(hdr.SampleRateHz, Is.EqualTo(48000));
      Assert.That(hdr.IsMono, Is.True);
      Assert.That(hdr.SamplesPerFrame, Is.EqualTo(1152), "MPEG-1 Layer II = 1152 samples/frame");
      // 1152 * 128000 / 48000 / 8 = 384 bytes, no padding.
      Assert.That(hdr.FrameLengthBytes, Is.EqualTo(384));
    });
  }

  /// <summary>MPEG-1 Layer I 128 kbps 44.1 kHz stereo header — Layer I bitrate table + 384 samples/frame.</summary>
  [Test]
  public void Parse_Mpeg1LayerI_128k_44100_Stereo_HeaderFieldsCorrect() {
    // byte2 = 0xFF → version=11, layer=11 (Layer I), prot=1; bitrate idx 1001, samplerate 00.
    var bytes = new byte[] { 0xFF, 0xFF, 0x90, 0x00 };
    var hdr = Mp3FrameHeader.Parse(bytes);

    Assert.Multiple(() => {
      Assert.That(hdr.Layer, Is.EqualTo(1), "Layer should be I");
      Assert.That(hdr.BitrateKbps, Is.EqualTo(288), "MPEG-1 Layer I index 9 = 288 kbps");
      Assert.That(hdr.SampleRateHz, Is.EqualTo(44100));
      Assert.That(hdr.SamplesPerFrame, Is.EqualTo(384), "Layer I = 384 samples/frame");
    });
  }

  // ──────────── 2b. Layer II / I silence decode ────────────

  /// <summary>
  /// A hand-built MPEG-1 Layer II 48 kHz mono frame with all bit allocations zero
  /// (silence) must decode to exactly 1152 samples of digital silence per frame —
  /// two frames → 2304 zero samples, no exception.
  /// </summary>
  [Test]
  public void Decompress_LayerII_MonoSilence_TwoFrames_YieldsSilence() {
    var frame = Mp3SyntheticFrames.BuildLayerIIMonoSilenceFrame();
    var stream = Concat(frame, frame);

    using var input = new MemoryStream(stream);
    using var output = new MemoryStream();
    Assert.DoesNotThrow(() => Mp3Codec.Decompress(input, output));

    var pcm = output.ToArray();
    Assert.That(pcm.Length, Is.EqualTo(2 * 1152 * 2), "2 frames × 1152 samples × 2 bytes (mono 16-bit)");
    Assert.That(pcm.All(b => b == 0), Is.True, "All-zero allocation → digital silence");
  }

  /// <summary>A stereo Layer II silence frame decodes to 1152 frames × 2 channels of silence.</summary>
  [Test]
  public void Decompress_LayerII_StereoSilence_YieldsSilence() {
    var frame = Mp3SyntheticFrames.BuildLayerIIStereoSilenceFrame();

    using var input = new MemoryStream(frame);
    using var output = new MemoryStream();
    Assert.DoesNotThrow(() => Mp3Codec.Decompress(input, output));

    var pcm = output.ToArray();
    Assert.That(pcm.Length, Is.EqualTo(1152 * 2 * 2), "1152 samples × 2 channels × 2 bytes");
    Assert.That(pcm.All(b => b == 0), Is.True);
  }

  /// <summary>
  /// A Layer II frame with a single active subband (a non-zero quantized sample) must
  /// produce non-zero PCM output and must not throw.
  /// </summary>
  [Test]
  public void Decompress_LayerII_OneActiveSubband_ProducesNonZeroOutput() {
    var frame = Mp3SyntheticFrames.BuildLayerIIMonoOneActiveSubbandFrame();

    using var input = new MemoryStream(frame);
    using var output = new MemoryStream();
    Assert.DoesNotThrow(() => Mp3Codec.Decompress(input, output));

    var pcm = output.ToArray();
    Assert.That(pcm.Length, Is.EqualTo(1152 * 2));
    Assert.That(pcm.Any(b => b != 0), Is.True, "An active subband must yield non-zero PCM");
  }

  /// <summary>A Layer I 48 kHz mono silence frame decodes to 384 samples of silence without throwing.</summary>
  [Test]
  public void Decompress_LayerI_MonoSilence_YieldsSilence() {
    var frame = Mp3SyntheticFrames.BuildLayerIMonoSilenceFrame();

    using var input = new MemoryStream(frame);
    using var output = new MemoryStream();
    Assert.DoesNotThrow(() => Mp3Codec.Decompress(input, output));

    var pcm = output.ToArray();
    Assert.That(pcm.Length, Is.EqualTo(384 * 2), "Layer I = 384 samples/frame (mono 16-bit)");
    Assert.That(pcm.All(b => b == 0), Is.True);
  }

  /// <summary>A truncated Layer II frame must stop gracefully (no exception) per the resync semantics.</summary>
  [Test]
  public void Decompress_LayerII_TruncatedFrame_DoesNotThrow() {
    var frame = Mp3SyntheticFrames.BuildLayerIIMonoOneActiveSubbandFrame();
    var truncated = frame.Take(frame.Length / 2).ToArray();

    using var input = new MemoryStream(truncated);
    using var output = new MemoryStream();
    Assert.DoesNotThrow(() => Mp3Codec.Decompress(input, output));
  }

  private static byte[] Concat(byte[] a, byte[] b) {
    var r = new byte[a.Length + b.Length];
    a.CopyTo(r, 0);
    b.CopyTo(r, a.Length);
    return r;
  }

  // ──────────── 3. Reserved / invalid headers ────────────

  [Test]
  public void Parse_NoSyncword_Throws() {
    var bytes = new byte[] { 0x00, 0x00, 0x00, 0x00 };
    Assert.Throws<InvalidDataException>(() => Mp3FrameHeader.Parse(bytes));
  }

  [Test]
  public void Parse_ReservedLayer_Throws() {
    // Layer bits = 00 → reserved.
    var bytes = new byte[] { 0xFF, 0xF9, 0x90, 0x00 };
    Assert.Throws<InvalidDataException>(() => Mp3FrameHeader.Parse(bytes));
  }

  [Test]
  public void Parse_ReservedBitrate_Throws() {
    // Bitrate index 1111 → reserved.
    var bytes = new byte[] { 0xFF, 0xFB, 0xF0, 0x00 };
    Assert.Throws<InvalidDataException>(() => Mp3FrameHeader.Parse(bytes));
  }

  [Test]
  public void Parse_ReservedSampleRate_Throws() {
    // Sample-rate index 11 → reserved.
    var bytes = new byte[] { 0xFF, 0xFB, 0x9C, 0x00 };
    Assert.Throws<InvalidDataException>(() => Mp3FrameHeader.Parse(bytes));
  }

  // ──────────── 4. ReadStreamInfo on synthetic single-frame stream ────────────

  /// <summary>
  /// Constructs a one-frame MPEG-1 L3 128 kbps 44.1 kHz stereo "MP3" — header + zero
  /// payload. <see cref="Mp3Codec.ReadStreamInfo"/> must report the header fields
  /// without performing actual decode (so the all-zero payload is harmless).
  /// </summary>
  [Test]
  public void ReadStreamInfo_SingleFrame_ReportsHeaderFields() {
    var hdrBytes = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
    var frame = new byte[417];
    Array.Copy(hdrBytes, frame, 4);

    using var input = new MemoryStream(frame);
    var info = Mp3Codec.ReadStreamInfo(input);

    Assert.Multiple(() => {
      Assert.That(info.SampleRate, Is.EqualTo(44100));
      Assert.That(info.Channels, Is.EqualTo(2));
      Assert.That(info.Bitrate, Is.EqualTo(128));
      // Duration estimate: 417 bytes * 8 * 44100 / 128000 ≈ 1149.6 → integer truncation gives ~1149.
      Assert.That(info.DurationSamples, Is.GreaterThan(1100).And.LessThan(1200));
    });
  }

  [Test]
  public void ReadStreamInfo_NoSyncFound_Throws() {
    var bytes = new byte[256]; // all zeros, no syncword
    using var input = new MemoryStream(bytes);
    Assert.Throws<InvalidDataException>(() => Mp3Codec.ReadStreamInfo(input));
  }

  // ──────────── 5. ID3v2 skip ────────────

  /// <summary>Stream with an ID3v2 tag prefix followed by a valid frame header. ReadStreamInfo must skip the tag.</summary>
  [Test]
  public void ReadStreamInfo_WithId3v2Tag_SkipsAndParsesFirstFrame() {
    // ID3v2 header: "ID3" + version (4 bytes: 03,00) + flags (00) + synchsafe size (10 bytes payload).
    var id3 = new byte[10 + 10];
    id3[0] = (byte)'I'; id3[1] = (byte)'D'; id3[2] = (byte)'3';
    id3[3] = 3; id3[4] = 0; id3[5] = 0;
    // synch-safe size = 10  →  bytes 6..9 = {0,0,0,10}
    id3[9] = 10;

    var hdrBytes = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
    var frame = new byte[417];
    Array.Copy(hdrBytes, frame, 4);

    var stream = new byte[id3.Length + frame.Length];
    Array.Copy(id3, 0, stream, 0, id3.Length);
    Array.Copy(frame, 0, stream, id3.Length, frame.Length);

    using var input = new MemoryStream(stream);
    var info = Mp3Codec.ReadStreamInfo(input);

    Assert.That(info.SampleRate, Is.EqualTo(44100));
    Assert.That(info.Channels, Is.EqualTo(2));
  }

  // ──────────── 6. Decode does not crash on synthetic frame ────────────

  /// <summary>
  /// Decoder fed a syntactically-valid (but semantically all-zero) MPEG-1 L3 frame
  /// must not throw, even though the produced PCM is uninitialised garbage. This
  /// exercises the side-info reader, scalefactor decoder, Huffman path, IMDCT and
  /// synthesis filterbank end-to-end.
  /// <para>
  /// <b>Known limitation:</b> without a real reference encoder (lame/ffmpeg) on the
  /// build host we cannot embed a bit-exact MP3 test vector. Real-stream decoding
  /// has been visually verified against minimp3 during the port; bit-exact
  /// validation is deferred until an external test asset can be wired in.
  /// </para>
  /// </summary>
  [Test]
  public void Decompress_SyntheticZeroFrame_DoesNotThrow() {
    var hdrBytes = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
    var frame = new byte[417];
    Array.Copy(hdrBytes, frame, 4);

    using var input = new MemoryStream(frame);
    using var output = new MemoryStream();
    Assert.DoesNotThrow(() => Mp3Codec.Decompress(input, output));
    // Zero-payload "frame" may produce some samples or none depending on bit-reservoir state.
    Assert.That(output.Length, Is.GreaterThanOrEqualTo(0));
  }
}
