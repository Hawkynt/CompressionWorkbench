#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.RealMedia;

namespace Compression.Tests.RealMedia;

/// <summary>
/// Pins the RealMedia descriptor: a hand-crafted .RMF (PROP + one cook audio MDPR +
/// CONT + DATA with two packets) must surface FULL.rm, metadata.ini tags, a stream
/// info entry and the per-stream concatenated payload (with the detected FOURCC as
/// its method). A raw .ra v4 header must surface its codec/rate metadata. Truncated
/// input must degrade gracefully.
/// </summary>
[TestFixture]
public class RealMediaTests {

  [Test]
  public void Rmf_CookAudioWithTwoPackets_SurfacesStreamBlobAndTags() {
    var rm = BuildRmf();
    using var ms = new MemoryStream(rm);
    var entries = new RealMediaFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.rm" && e.Kind == "Container"), Is.True);

    using var metaStream = new MemoryStream();
    new RealMediaFormatDescriptor().ExtractEntry(new MemoryStream(rm), "metadata.ini", metaStream, null);
    var metaText = Encoding.UTF8.GetString(metaStream.ToArray());
    Assert.That(metaText, Does.Contain("title = My Song"));
    Assert.That(metaText, Does.Contain("author = Me"));

    Assert.That(entries.Any(e => e.Name == "streams/stream_00.info.txt"), Is.True);
    using var infoStream = new MemoryStream();
    new RealMediaFormatDescriptor().ExtractEntry(new MemoryStream(rm), "streams/stream_00.info.txt", infoStream, null);
    var infoText = Encoding.UTF8.GetString(infoStream.ToArray());
    Assert.That(infoText, Does.Contain("codec = cook"));
    Assert.That(infoText, Does.Contain("mime_type = audio/x-pn-realaudio"));
    Assert.That(infoText, Does.Contain("packets = 2"));

    Assert.That(entries.Any(e => e.Name == "streams/stream_00.bin" && e.Kind == "Stream" && e.Method == "cook"), Is.True);

    using var blobStream = new MemoryStream();
    new RealMediaFormatDescriptor().ExtractEntry(new MemoryStream(rm), "streams/stream_00.bin", blobStream, null);
    // Two packets concatenated: payload1 (3 bytes) + payload2 (4 bytes).
    Assert.That(blobStream.ToArray(), Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0xAA, 0xBB, 0xCC, 0xDD }));
  }

  [Test]
  public void RawRa_V4Header_ParsesCodecAndRate() {
    var ra = BuildRawRaV4(codec: "cook", sampleRate: 22050, channels: 1, bits: 16, payload: [0x01, 0x02, 0x03]);
    using var ms = new MemoryStream(ra);
    var entries = new RealMediaFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.ra" && e.Kind == "Container"), Is.True);

    using var metaStream = new MemoryStream();
    new RealMediaFormatDescriptor().ExtractEntry(new MemoryStream(ra), "metadata.ini", metaStream, null);
    var metaText = Encoding.UTF8.GetString(metaStream.ToArray());
    Assert.That(metaText, Does.Contain("version = 4"));
    Assert.That(metaText, Does.Contain("codec = cook"));
    Assert.That(metaText, Does.Contain("sample_rate = 22050"));
    Assert.That(metaText, Does.Contain("channels = 1"));

    Assert.That(entries.Any(e => e.Name == "streams/stream_00.bin" && e.Kind == "Stream"), Is.True);
  }

  [Test]
  public void Rmf_LpcJAudio_SurfacesDecodedMonoWav() {
    // Two 20-byte lpcJ blocks → 2 × 160 = 320 samples → mono 8 kHz WAV (44-byte header).
    var blocks = new byte[40];
    for (var i = 0; i < blocks.Length; ++i) blocks[i] = (byte)(i * 13);
    var rm = BuildRmfWithLpcJ(blocks);

    var entries = new RealMediaFormatDescriptor().List(new MemoryStream(rm), null);
    Assert.That(entries.Any(e => e.Name == "streams/stream_00.bin" && e.Method == "lpcJ"), Is.True);

    var wavEntry = entries.FirstOrDefault(e => e.Name == "streams/stream_00.MONO.wav");
    Assert.That(wavEntry, Is.Not.Null);
    Assert.That(wavEntry!.Kind, Is.EqualTo("Channel"));

    using var wavStream = new MemoryStream();
    new RealMediaFormatDescriptor().ExtractEntry(new MemoryStream(rm), "streams/stream_00.MONO.wav", wavStream, null);
    var wav = wavStream.ToArray();
    Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
    // 320 samples × 2 bytes + 44-byte header.
    Assert.That(wav.Length, Is.EqualTo(44 + 320 * 2));
    // Sample rate field at offset 24 must be 8000.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(8000));
  }

  [Test]
  public void RawRa_V3_LpcJ_SurfacesDecodedMonoWav() {
    var blocks = new byte[20];
    for (var i = 0; i < blocks.Length; ++i) blocks[i] = (byte)(i * 11 + 3);
    var ra = BuildRawRaV3Lpcj(blocks);

    var entries = new RealMediaFormatDescriptor().List(new MemoryStream(ra), null);
    Assert.That(entries.Any(e => e.Name == "FULL.ra" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "streams/stream_00.bin"), Is.True);

    var wavEntry = entries.FirstOrDefault(e => e.Name == "streams/stream_00.MONO.wav" && e.Kind == "Channel");
    Assert.That(wavEntry, Is.Not.Null);

    using var wavStream = new MemoryStream();
    new RealMediaFormatDescriptor().ExtractEntry(new MemoryStream(ra), "streams/stream_00.MONO.wav", wavStream, null);
    var wav = wavStream.ToArray();
    // 1 block → 160 samples × 2 bytes + 44-byte header.
    Assert.That(wav.Length, Is.EqualTo(44 + 160 * 2));
  }

  [Test]
  public void RawRa_V4_Atrac3_SurfacesDecodedStereoChannels() {
    // A raw .ra v4 header for ATRAC3 ("atrc"), stereo, sub_packet_size = 192, joint stereo.
    // Two all-zero 192-byte sub-packets descramble to the XOR-key pattern and decode to two
    // bounded 1024-sample-per-channel WAVs.
    var ra = BuildRawRaV4Atrac3(subPacketSize: 192, channels: 2, sampleRate: 44100,
      jointStereo: true, numSubPackets: 2);

    var entries = new RealMediaFormatDescriptor().List(new MemoryStream(ra), null);
    var channelEntries = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channelEntries.Count, Is.EqualTo(2), "stereo ATRAC3 surfaces two channel WAVs");

    using var wavStream = new MemoryStream();
    new RealMediaFormatDescriptor().ExtractEntry(new MemoryStream(ra), channelEntries[0].Name, wavStream, null);
    var wav = wavStream.ToArray();
    Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
    // 2 sub-packets × 1024 samples × 2 bytes + 44-byte header.
    Assert.That(wav.Length, Is.EqualTo(44 + 2 * 1024 * 2));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(44100u));
  }

  [Test]
  public void RawRa_V4_Atrac3_Truncated_DegradesGracefully() {
    // A valid header but a payload shorter than one sub-packet → no channels, blob survives.
    var ra = BuildRawRaV4Atrac3(subPacketSize: 192, channels: 2, sampleRate: 44100,
      jointStereo: true, numSubPackets: 0);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new RealMediaFormatDescriptor().List(new MemoryStream(ra), null),
      Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "FULL.ra"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
  }

  [Test]
  public void Rmf_Truncated_DegradesGracefully() {
    // ".RMF" magic with a chunk size that overruns the buffer.
    using var ms = new MemoryStream();
    ms.Write(".RMF"u8.ToArray());
    WriteU32BE(ms, 9999);
    var rm = ms.ToArray();

    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new RealMediaFormatDescriptor().List(new MemoryStream(rm), null), Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "FULL.rm"), Is.True);
  }

  [Test]
  public void RawRa_Truncated_DegradesGracefully() {
    var ra = new byte[] { 0x2E, 0x72, 0x61, 0xFD, 0x00 }; // ".ra\xFD" + 1 byte (no full version)
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new RealMediaFormatDescriptor().List(new MemoryStream(ra), null), Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "FULL.ra"), Is.True);
  }

  // ── synthetic builders ──────────────────────────────────────────────────────

  private static byte[] BuildRmf() {
    using var ms = new MemoryStream();

    // .RMF header chunk: 4CC + u32 size + u16 version + u32 fileVersion + u32 numHeaders
    WriteChunk(ms, ".RMF", inner => {
      WriteU16BE(inner, 0);   // object version
      WriteU32BE(inner, 0);   // file version
      WriteU32BE(inner, 4);   // num headers
    });

    // PROP chunk
    WriteChunk(ms, "PROP", inner => {
      WriteU16BE(inner, 0);       // object version
      WriteU32BE(inner, 64000);   // max bitrate
      WriteU32BE(inner, 64000);   // avg bitrate
      WriteU32BE(inner, 600);     // max packet size
      WriteU32BE(inner, 600);     // avg packet size
      WriteU32BE(inner, 2);       // num packets
      WriteU32BE(inner, 10000);   // duration
      WriteU32BE(inner, 0);       // preroll
      WriteU32BE(inner, 0);       // index offset
      WriteU32BE(inner, 0);       // data offset
      WriteU16BE(inner, 1);       // num streams
      WriteU16BE(inner, 0);       // flags
    });

    // MDPR chunk for stream 0 (cook audio)
    WriteChunk(ms, "MDPR", inner => {
      WriteU16BE(inner, 0);       // object version
      WriteU16BE(inner, 0);       // stream number
      WriteU32BE(inner, 64000);   // max bitrate
      WriteU32BE(inner, 64000);   // avg bitrate
      WriteU32BE(inner, 600);     // max packet size
      WriteU32BE(inner, 600);     // avg packet size
      WriteU32BE(inner, 0);       // start time
      WriteU32BE(inner, 0);       // preroll
      WriteU32BE(inner, 10000);   // duration
      WriteByteLen(inner, "Audio Stream");
      WriteByteLen(inner, "audio/x-pn-realaudio");
      // type-specific blob containing the RA header with cook FOURCC
      var typeSpecific = BuildRaTypeSpecific("cook");
      WriteU32BE(inner, (uint)typeSpecific.Length);
      inner.Write(typeSpecific);
    });

    // CONT chunk
    WriteChunk(ms, "CONT", inner => {
      WriteU16BE(inner, 0); // object version
      WriteU16LenString(inner, "My Song");
      WriteU16LenString(inner, "Me");
      WriteU16LenString(inner, "(c) 2024");
      WriteU16LenString(inner, "a comment");
    });

    // DATA chunk with 2 packets for stream 0
    WriteChunk(ms, "DATA", inner => {
      WriteU16BE(inner, 0);     // object version
      WriteU32BE(inner, 2);     // num packets
      WriteU32BE(inner, 0);     // next data header
      WritePacket(inner, streamNumber: 0, payload: [0x11, 0x22, 0x33]);
      WritePacket(inner, streamNumber: 0, payload: [0xAA, 0xBB, 0xCC, 0xDD]);
    });

    return ms.ToArray();
  }

  private static byte[] BuildRmfWithLpcJ(byte[] blocks) {
    using var ms = new MemoryStream();

    WriteChunk(ms, ".RMF", inner => {
      WriteU16BE(inner, 0);
      WriteU32BE(inner, 0);
      WriteU32BE(inner, 3);
    });

    WriteChunk(ms, "MDPR", inner => {
      WriteU16BE(inner, 0);       // object version
      WriteU16BE(inner, 0);       // stream number
      WriteU32BE(inner, 14400);   // max bitrate
      WriteU32BE(inner, 14400);   // avg bitrate
      WriteU32BE(inner, 20);      // max packet size
      WriteU32BE(inner, 20);      // avg packet size
      WriteU32BE(inner, 0);       // start time
      WriteU32BE(inner, 0);       // preroll
      WriteU32BE(inner, 1000);    // duration
      WriteByteLen(inner, "Audio Stream");
      WriteByteLen(inner, "audio/x-pn-realaudio");
      var typeSpecific = BuildRaTypeSpecific("lpcJ");
      WriteU32BE(inner, (uint)typeSpecific.Length);
      inner.Write(typeSpecific);
    });

    // DATA chunk: one packet per 20-byte lpcJ block.
    var numPackets = blocks.Length / 20;
    WriteChunk(ms, "DATA", inner => {
      WriteU16BE(inner, 0);
      WriteU32BE(inner, (uint)numPackets);
      WriteU32BE(inner, 0);
      for (var i = 0; i < numPackets; ++i)
        WritePacket(inner, streamNumber: 0, payload: blocks[(i * 20)..((i + 1) * 20)]);
    });

    return ms.ToArray();
  }

  private static byte[] BuildRawRaV3Lpcj(byte[] blocks) {
    using var ms = new MemoryStream();
    ms.Write([0x2E, 0x72, 0x61, 0xFD]); // ".ra\xFD"
    WriteU16BE(ms, 3);                  // version (offset 4)
    // header size at offset 6: the v3 header bytes that follow before the audio data.
    // We use a small fixed header that carries the lpcJ FOURCC for the scan.
    var header = new MemoryStream();
    header.Write(Encoding.ASCII.GetBytes("lpcJ"));
    header.Write(new byte[12]);
    var headerBytes = header.ToArray();
    WriteU16BE(ms, (ushort)headerBytes.Length);
    ms.Write(headerBytes);
    ms.Write(blocks);
    return ms.ToArray();
  }

  [Test]
  public void Rmf_CookAudio_DeinterleavesAndDecodesPerChannelWavs() {
    // A mono cook stream with the Int0 (no-reorder) interleaver: each data packet is one
    // coded frame. Two frames decode to two channel WAVs (after the discard prelude the
    // samples are zero, but the entries must be present with the right length).
    var rm = BuildCookRmf(channels: 1, samplesPerFrame: 1024, subbands: 20, blockAlign: 96,
      jsSubbandStart: 0, jsVlcBits: 0, frames: 4, cookVersion: 0x1000001);

    var entries = new RealMediaFormatDescriptor().List(new MemoryStream(rm), null);

    // Stream blob still present with the cook method.
    Assert.That(entries.Any(e => e.Name == "streams/stream_00.bin" && e.Method == "cook"), Is.True);

    // Exactly one decoded mono channel WAV.
    var channelEntries = entries.Where(e => e.Kind == "Channel" && e.Name.StartsWith("streams/stream_00.")).ToList();
    Assert.That(channelEntries.Count, Is.EqualTo(1));

    using var wavStream = new MemoryStream();
    new RealMediaFormatDescriptor().ExtractEntry(new MemoryStream(rm), channelEntries[0].Name, wavStream, null);
    var wav = wavStream.ToArray();
    Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
    // 4 frames * 1024 samples * 2 bytes + 44-byte header.
    Assert.That(wav.Length, Is.EqualTo(44 + 4 * 1024 * 2));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(44100));
  }

  [Test]
  public void Rmf_CookStereo_DegradesGracefully_AndKeepsBlob() {
    // A crafted (non-encoder) joint-stereo stream may hit the decoder's invalid-decouple
    // guard; the descriptor must then fall back to the blob-only view without throwing.
    // When the crafted bits happen to decode, exactly two channel WAVs of the right length
    // appear. Either outcome is acceptable; a throw is not.
    var rm = BuildCookRmf(channels: 2, samplesPerFrame: 2048, subbands: 20, blockAlign: 120,
      jsSubbandStart: 5, jsVlcBits: 5, frames: 3, cookVersion: 0x1000003);

    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new RealMediaFormatDescriptor().List(new MemoryStream(rm), null), Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "streams/stream_00.bin" && e.Method == "cook"), Is.True);

    var channelEntries = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channelEntries.Count, Is.AnyOf(0, 2));
    if (channelEntries.Count == 2)
      Assert.That(channelEntries.All(e => e.OriginalSize == 44 + 3 * 1024 * 2), Is.True);
  }

  [Test]
  public void Rmf_CookUnsupported_FallsBackToBlobOnly() {
    // samples_per_frame = 700 is not a supported cook frame size → no Channel entries.
    var rm = BuildCookRmf(channels: 1, samplesPerFrame: 700, subbands: 20, blockAlign: 96,
      jsSubbandStart: 0, jsVlcBits: 0, frames: 2, cookVersion: 0x1000001);

    var entries = new RealMediaFormatDescriptor().List(new MemoryStream(rm), null);
    Assert.That(entries.Any(e => e.Name == "streams/stream_00.bin" && e.Method == "cook"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
  }

  /// <summary>
  /// Builds a synthetic .RMF with a single cook audio stream: an MDPR whose type-specific
  /// blob is a proper RA v5 header (carrying the deinterleaver id, framing and cook
  /// extradata) plus a DATA chunk with <paramref name="frames"/> coded-frame packets.
  /// </summary>
  private static byte[] BuildCookRmf(int channels, int samplesPerFrame, int subbands,
      int blockAlign, int jsSubbandStart, int jsVlcBits, int frames, long cookVersion) {
    using var ms = new MemoryStream();

    WriteChunk(ms, ".RMF", inner => {
      WriteU16BE(inner, 0);
      WriteU32BE(inner, 0);
      WriteU32BE(inner, 3);
    });

    WriteChunk(ms, "MDPR", inner => {
      WriteU16BE(inner, 0);       // object version
      WriteU16BE(inner, 0);       // stream number
      WriteU32BE(inner, 64000);
      WriteU32BE(inner, 64000);
      WriteU32BE(inner, 600);
      WriteU32BE(inner, 600);
      WriteU32BE(inner, 0);
      WriteU32BE(inner, 0);
      WriteU32BE(inner, 10000);
      WriteByteLen(inner, "Audio Stream");
      WriteByteLen(inner, "audio/x-pn-realaudio");
      var typeSpecific = BuildCookRaV5Header(channels, samplesPerFrame, subbands, blockAlign,
        jsSubbandStart, jsVlcBits, cookVersion);
      WriteU32BE(inner, (uint)typeSpecific.Length);
      inner.Write(typeSpecific);
    });

    WriteChunk(ms, "DATA", inner => {
      WriteU16BE(inner, 0);
      WriteU32BE(inner, (uint)frames);
      WriteU32BE(inner, 0);
      var rng = 1u;
      for (var i = 0; i < frames; ++i) {
        var payload = new byte[blockAlign];
        for (var j = 0; j < payload.Length; ++j) {
          rng = rng * 1103515245 + 12345;
          payload[j] = (byte)(rng >> 16);
        }
        WritePacket(inner, streamNumber: 0, payload: payload);
      }
    });

    return ms.ToArray();
  }

  private static byte[] BuildCookRaV5Header(int channels, int samplesPerFrame, int subbands,
      int blockAlign, int jsSubbandStart, int jsVlcBits, long cookVersion) {
    using var ms = new MemoryStream();
    ms.Write([0x2E, 0x72, 0x61, 0xFD]);  // ".ra\xFD"
    WriteU16BE(ms, 5);                   // +4 version
    WriteU16BE(ms, 0);                   // +6 unused
    ms.Write(Encoding.ASCII.GetBytes(".ra5")); // +8
    WriteU32BE(ms, 0);                   // +12 data size
    WriteU16BE(ms, 5);                   // +16 version2
    WriteU32BE(ms, 0);                   // +18 header size
    WriteU16BE(ms, 0);                   // +22 flavor
    WriteU32BE(ms, (uint)blockAlign);    // +24 coded_framesize
    WriteU32BE(ms, 0);                   // +28 ???
    WriteU32BE(ms, 64000);               // +32 bytes_per_minute
    WriteU32BE(ms, 0);                   // +36 ???
    WriteU16BE(ms, 1);                   // +40 sub_packet_h (1 = no interleave grouping)
    WriteU16BE(ms, (ushort)blockAlign);  // +42 frame size (audio_framesize)
    WriteU16BE(ms, (ushort)blockAlign);  // +44 sub_packet_size
    WriteU16BE(ms, 0);                   // +46 ???
    WriteU16BE(ms, 0); WriteU16BE(ms, 0); WriteU16BE(ms, 0); // +48 v5 triple u16
    WriteU16BE(ms, 44100);               // +54 sample rate
    WriteU32BE(ms, 0);                   // +56 ???
    WriteU16BE(ms, (ushort)channels);    // +60 channels
    // +62 deint id (LE) "Int0"
    ms.Write([(byte)'I', (byte)'n', (byte)'t', (byte)'0']);
    ms.Write(Encoding.ASCII.GetBytes("cook")); // +66 interleaver/codec tag
    WriteU16BE(ms, 0);                   // +70 ???
    ms.WriteByte(0);                     // +72 ??? u8
    ms.WriteByte(0);                     // +73 ??? u8 (v5)
    // +74 cook extradata length + extradata
    var ed = new MemoryStream();
    WriteU32BE(ed, (uint)cookVersion);
    WriteU16BE(ed, (ushort)samplesPerFrame);
    WriteU16BE(ed, (ushort)subbands);
    WriteU32BE(ed, 0);                   // unused
    WriteU16BE(ed, (ushort)jsSubbandStart);
    WriteU16BE(ed, (ushort)jsVlcBits);
    var edBytes = ed.ToArray();
    WriteU32BE(ms, (uint)edBytes.Length);
    ms.Write(edBytes);
    return ms.ToArray();
  }

  private static byte[] BuildRaTypeSpecific(string fourcc) {
    // A small RA header stub whose only job is to carry the codec FOURCC for the scan.
    using var ms = new MemoryStream();
    ms.Write([0x2E, 0x72, 0x61, 0xFD]); // ".ra\xFD"
    WriteU16BE(ms, 5);                  // version
    ms.Write(new byte[40]);             // padding
    ms.Write(Encoding.ASCII.GetBytes(fourcc));
    ms.Write(new byte[4]);
    return ms.ToArray();
  }

  private static void WritePacket(MemoryStream ms, int streamNumber, byte[] payload) {
    var length = 12 + payload.Length;
    WriteU16BE(ms, 0);                       // version
    WriteU16BE(ms, (ushort)length);          // length (incl. 12-byte header)
    WriteU16BE(ms, (ushort)streamNumber);    // stream number
    WriteU32BE(ms, 0);                       // timestamp
    ms.WriteByte(0);                         // packet group
    ms.WriteByte(0);                         // flags
    ms.Write(payload);
  }

  private static byte[] BuildRawRaV4(string codec, int sampleRate, int channels, int bits, byte[] payload) {
    using var ms = new MemoryStream();
    ms.Write([0x2E, 0x72, 0x61, 0xFD]); // ".ra\xFD"
    WriteU16BE(ms, 4);                  // version (offset 4)
    // Pad up to data offset; we put data at offset 64.
    WriteU16BE(ms, 0);                  // offset 6
    ms.Write(Encoding.ASCII.GetBytes(".ra4")); // offset 8 marker
    WriteU32BE(ms, 64);                 // offset 12: data/header offset
    // pad to offset 48
    while (ms.Length < 48) ms.WriteByte(0);
    WriteU16BE(ms, (ushort)sampleRate); // offset 48: sample rate
    WriteU16BE(ms, 0);                  // offset 50
    WriteU16BE(ms, (ushort)bits);       // offset 52: sample size
    WriteU16BE(ms, (ushort)channels);   // offset 54: channels
    // place the codec FOURCC somewhere in the header for the scan to find
    ms.Write(Encoding.ASCII.GetBytes(codec));
    while (ms.Length < 64) ms.WriteByte(0);
    ms.Write(payload);
    return ms.ToArray();
  }

  private static byte[] BuildRawRaV4Atrac3(int subPacketSize, int channels, int sampleRate,
      bool jointStereo, int numSubPackets) {
    using var ms = new MemoryStream();
    ms.Write([0x2E, 0x72, 0x61, 0xFD]); // ".ra\xFD"
    WriteU16BE(ms, 4);                  // version (offset 4)

    // RA v4 header body (matches the reader's ParseRaAtrac3Config layout).
    WriteU16BE(ms, 0);                  // unused
    ms.Write(Encoding.ASCII.GetBytes(".ra4")); // ".ra4"
    WriteU32BE(ms, 0);                  // data size
    WriteU16BE(ms, 4);                  // version2
    WriteU32BE(ms, 0);                  // header size
    WriteU16BE(ms, 0);                  // flavor
    WriteU32BE(ms, 96);                 // coded frame size
    WriteU32BE(ms, 0);                  // ???
    WriteU32BE(ms, 0);                  // bytes per minute
    WriteU32BE(ms, 0);                  // ???
    WriteU16BE(ms, 1);                  // sub_packet_h
    WriteU16BE(ms, (ushort)(subPacketSize)); // container frame size
    WriteU16BE(ms, (ushort)subPacketSize);   // sub packet size (= block align)
    WriteU16BE(ms, 0);                  // ???
    WriteU16BE(ms, (ushort)sampleRate); // sample rate
    WriteU32BE(ms, 0);                  // ???
    WriteU16BE(ms, (ushort)channels);   // channels

    // Codec FOURCC + atrac3 config block (be32 version=4, be16 samples, be16 delay=0x88E,
    // be16 coding_mode).
    ms.Write(Encoding.ASCII.GetBytes("atrc"));
    WriteU32BE(ms, 4);                          // version
    WriteU16BE(ms, (ushort)(1024 * channels));  // samples per frame
    WriteU16BE(ms, 0x88E);                      // delay
    WriteU16BE(ms, (ushort)(jointStereo ? 1 : 0)); // coding mode

    var headerLen = (int)ms.Length;
    // dataOffset (u32 @ 12) drives where the raw reader slices the payload.
    var rebuilt = ms.ToArray();
    BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(12), (uint)headerLen);

    using var full = new MemoryStream();
    full.Write(rebuilt);
    full.Write(new byte[subPacketSize * numSubPackets]); // all-zero sub-packets
    return full.ToArray();
  }

  private static void WriteChunk(MemoryStream ms, string fourcc, Action<MemoryStream> body) {
    using var inner = new MemoryStream();
    body(inner);
    var bodyBytes = inner.ToArray();
    var size = 8 + bodyBytes.Length;
    ms.Write(Encoding.ASCII.GetBytes(fourcc));
    WriteU32BE(ms, (uint)size);
    ms.Write(bodyBytes);
  }

  private static void WriteByteLen(MemoryStream ms, string s) {
    var bytes = Encoding.Latin1.GetBytes(s);
    ms.WriteByte((byte)bytes.Length);
    ms.Write(bytes);
  }

  private static void WriteU16LenString(MemoryStream ms, string s) {
    var bytes = Encoding.Latin1.GetBytes(s);
    WriteU16BE(ms, (ushort)bytes.Length);
    ms.Write(bytes);
  }

  private static void WriteU16BE(MemoryStream ms, ushort v) {
    Span<byte> b = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(b, v);
    ms.Write(b);
  }

  private static void WriteU32BE(MemoryStream ms, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(b, v);
    ms.Write(b);
  }
}
