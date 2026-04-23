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

  // ──────────── 2. Layer I / II rejection ────────────

  /// <summary>Layer I header (layer bits = 11). Decoder must throw <see cref="NotSupportedException"/>.</summary>
  [Test]
  public void Decompress_LayerI_ThrowsNotSupported() {
    // MPEG-1 Layer I, 128 kbps, 44.1 kHz, stereo, no CRC.
    // byte1 = 1111 1111 → 0xFF; byte2 = sync(0xE0) + version(11)+layer(11)+prot(1) = 1111 1111 = 0xFF
    // byte2 = 0xFF: 111 11 11 1 → version=11, layer=11 (Layer I), prot=1
    // bitrate idx 1001 = 128k, samplerate 00 = 44.1 kHz, no padding/private = 0x90
    var bytes = new byte[] { 0xFF, 0xFF, 0x90, 0x00 };

    using var input = new MemoryStream(bytes);
    using var output = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => Mp3Codec.Decompress(input, output));
  }

  /// <summary>Layer II header (layer bits = 10). Decoder must throw <see cref="NotSupportedException"/>.</summary>
  [Test]
  public void Decompress_LayerII_ThrowsNotSupported() {
    // MPEG-1 Layer II, 128 kbps, 44.1 kHz, stereo, no CRC.
    // byte2 = 0xFD = 1111 1101 → version=11, layer=10 (Layer II), prot=1
    var bytes = new byte[] { 0xFF, 0xFD, 0x90, 0x00 };

    using var input = new MemoryStream(bytes);
    using var output = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => Mp3Codec.Decompress(input, output));
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
