#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Matroska;

namespace Compression.Tests.Mkv;

/// <summary>
/// Behaviour tests for Matroska per-track audio channel extraction. A_PCM tracks decode to
/// byte-checked <c>TRACKn_&lt;CHANNEL&gt;.wav</c> entries; A_VORBIS CodecPrivate xiph-lacing
/// is split into three setup headers; block lacing is reassembled into individual frames;
/// unsupported codecs fall back to raw-only with a metadata note.
/// </summary>
[TestFixture]
public class MkvAudioChannelTests {

  // ── EBML writer helpers (mirror MkvFrameExtractionTests) ──────────────────────

  private static void WriteId(MemoryStream ms, ulong id) {
    if (id <= 0xFF) ms.WriteByte((byte)id);
    else if (id <= 0xFFFF) { ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id); }
    else if (id <= 0xFFFFFF) { ms.WriteByte((byte)(id >> 16)); ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id); }
    else { ms.WriteByte((byte)(id >> 24)); ms.WriteByte((byte)(id >> 16)); ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id); }
  }

  private static void WriteSize(MemoryStream ms, int size) {
    if (size <= 127) ms.WriteByte((byte)(0x80 | size));
    else if (size <= 16383) { ms.WriteByte((byte)(0x40 | (size >> 8))); ms.WriteByte((byte)size); }
    else { ms.WriteByte((byte)(0x20 | (size >> 16))); ms.WriteByte((byte)(size >> 8)); ms.WriteByte((byte)size); }
  }

  private static void WriteElement(MemoryStream ms, ulong id, byte[] body) {
    WriteId(ms, id);
    WriteSize(ms, body.Length);
    ms.Write(body);
  }

  private static byte[] Element(ulong id, byte[] body) {
    var ms = new MemoryStream();
    WriteElement(ms, id, body);
    return ms.ToArray();
  }

  private static byte[] EbmlHeader() {
    var inner = new MemoryStream();
    inner.Write(new byte[] { 0x42, 0x86, 0x81, 0x01 });
    inner.WriteByte(0x42); inner.WriteByte(0x82); inner.WriteByte(0x88);
    inner.Write("matroska"u8);
    return inner.ToArray();
  }

  /// <summary>Audio TrackEntry: number, type=audio(2), codecId, optional CodecPrivate, Audio(channels/rate/bits).</summary>
  private static byte[] AudioTrackEntry(int number, string codecId, byte[]? codecPrivate, int channels, double rate, int bits) {
    var entry = new MemoryStream();
    WriteElement(entry, 0xD7, [(byte)number]);    // TrackNumber
    WriteElement(entry, 0x83, [2]);               // TrackType = audio
    WriteElement(entry, 0x86, Encoding.UTF8.GetBytes(codecId)); // CodecId
    if (codecPrivate != null) WriteElement(entry, 0x63A2, codecPrivate); // CodecPrivate

    var audio = new MemoryStream();
    var freq = new byte[8];
    BinaryPrimitives.WriteDoubleBigEndian(freq, rate);
    WriteElement(audio, 0xB5, freq);              // SamplingFrequency (float64)
    WriteElement(audio, 0x9F, [(byte)channels]);  // Channels
    if (bits > 0) WriteElement(audio, 0x6264, [(byte)bits]); // BitDepth
    WriteElement(entry, 0xE1, audio.ToArray());   // Audio

    return Element(0xAE, entry.ToArray());        // TrackEntry
  }

  private static byte[] SimpleBlock(int trackNumber, byte flags, byte[] payload) {
    var body = new byte[1 + 2 + 1 + payload.Length];
    body[0] = (byte)(0x80 | trackNumber); // 1-byte track-number vint
    body[3] = flags;
    payload.CopyTo(body, 4);
    return Element(0xA3, body);
  }

  private static byte[] MakeAudioMkv(byte[] trackEntry, params byte[][] blocks) {
    var ms = new MemoryStream();
    WriteElement(ms, 0x1A45DFA3, EbmlHeader());
    WriteId(ms, 0x18538067); // Segment, unknown size
    ms.Write(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

    WriteElement(ms, 0x1654AE6B, trackEntry); // Tracks

    var cluster = new MemoryStream();
    WriteElement(cluster, 0xE7, [0x00]); // Timecode
    foreach (var b in blocks) cluster.Write(b);
    WriteElement(ms, 0x1F43B675, cluster.ToArray());
    return ms.ToArray();
  }

  // ── A_PCM ─────────────────────────────────────────────────────────────────────

  [Test]
  public void PcmStereo_ProducesLeftRightChannels() {
    var entry = AudioTrackEntry(1, "A_PCM/INT/LIT", null, channels: 2, rate: 48000, bits: 16);
    var pcm = new byte[] { 0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44 };
    var mkv = MakeAudioMkv(entry, SimpleBlock(1, 0x00, pcm));

    using var ms = new MemoryStream(mkv);
    var entries = new MkvFormatDescriptor().List(ms, null);
    var channels = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channels.Count, Is.EqualTo(2));
    Assert.That(channels.Any(e => e.Name == "TRACK0_LEFT.wav"), Is.True);
    Assert.That(channels.Any(e => e.Name == "TRACK0_RIGHT.wav"), Is.True);
  }

  [Test]
  public void PcmStereo_RightChannelMatchesSamples() {
    var entry = AudioTrackEntry(1, "A_PCM/INT/LIT", null, channels: 2, rate: 44100, bits: 16);
    var pcm = new byte[] { 0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44 };
    var mkv = MakeAudioMkv(entry, SimpleBlock(1, 0x00, pcm));

    using var ms = new MemoryStream(mkv);
    using var output = new MemoryStream();
    new MkvFormatDescriptor().ExtractEntry(ms, "TRACK0_RIGHT.wav", output, null);
    var right = output.ToArray().AsSpan(44).ToArray();
    Assert.That(right, Is.EqualTo(new byte[] { 0x22, 0x22, 0x44, 0x44 }));
  }

  [Test]
  public void PcmMono_ProducesSingleMonoChannel() {
    var entry = AudioTrackEntry(1, "A_PCM/INT/LIT", null, channels: 1, rate: 8000, bits: 16);
    var mkv = MakeAudioMkv(entry, SimpleBlock(1, 0x00, new byte[] { 1, 2, 3, 4 }));
    using var ms = new MemoryStream(mkv);
    var entries = new MkvFormatDescriptor().List(ms, null);
    var channels = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channels.Count, Is.EqualTo(1));
    Assert.That(channels[0].Name, Is.EqualTo("TRACK0_MONO.wav"));
  }

  [Test]
  public void RawTrackEntry_AlwaysPreserved() {
    var entry = AudioTrackEntry(1, "A_PCM/INT/LIT", null, channels: 2, rate: 48000, bits: 16);
    var mkv = MakeAudioMkv(entry, SimpleBlock(1, 0x00, new byte[] { 1, 2, 3, 4 }));
    using var ms = new MemoryStream(mkv);
    var entries = new MkvFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Kind == "Track"), Is.True);
  }

  [Test]
  public void UnsupportedCodec_FallsBackWithMetadataNote() {
    var entry = AudioTrackEntry(1, "A_REAL/COOK", null, channels: 2, rate: 44100, bits: 16);
    var mkv = MakeAudioMkv(entry, SimpleBlock(1, 0x00, new byte[] { 1, 2, 3, 4 }));
    using var ms = new MemoryStream(mkv);
    var entries = new MkvFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
    using var output = new MemoryStream();
    ms.Position = 0;
    new MkvFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(text, Does.Contain("track0_decode=unsupported"));
  }

  // ── A_VORBIS CodecPrivate xiph-lacing split ───────────────────────────────────

  [Test]
  public void XiphLacedHeaders_SplitIntoThreeSetupPackets() {
    // CodecPrivate: count-1 = 2, then lengths of the first two headers (Xiph 255-run),
    // then the three concatenated headers (30, 10, 5 bytes here).
    var h0 = new byte[30]; Array.Fill(h0, (byte)0xA0);
    var h1 = new byte[10]; Array.Fill(h1, (byte)0xB0);
    var h2 = new byte[5];  Array.Fill(h2, (byte)0xC0);

    var priv = new MemoryStream();
    priv.WriteByte(2);    // header count - 1
    priv.WriteByte(30);   // len(h0) as single Xiph byte
    priv.WriteByte(10);   // len(h1)
    priv.Write(h0); priv.Write(h1); priv.Write(h2);

    var headers = MkvAudioChannels.SplitXiphLacedHeaders(priv.ToArray());
    Assert.That(headers.Count, Is.EqualTo(3));
    Assert.That(headers[0], Is.EqualTo(h0));
    Assert.That(headers[1], Is.EqualTo(h1));
    Assert.That(headers[2], Is.EqualTo(h2));
  }

  [Test]
  public void XiphLacedHeaders_HandlesLengthsAbove255() {
    // A 300-byte first header laces as 255 + 45.
    var h0 = new byte[300]; Array.Fill(h0, (byte)0x11);
    var h1 = new byte[8];   Array.Fill(h1, (byte)0x22);
    var h2 = new byte[4];   Array.Fill(h2, (byte)0x33);

    var priv = new MemoryStream();
    priv.WriteByte(2);
    priv.WriteByte(255); priv.WriteByte(45); // len(h0) = 300
    priv.WriteByte(8);                        // len(h1) = 8
    priv.Write(h0); priv.Write(h1); priv.Write(h2);

    var headers = MkvAudioChannels.SplitXiphLacedHeaders(priv.ToArray());
    Assert.That(headers.Count, Is.EqualTo(3));
    Assert.That(headers[0].Length, Is.EqualTo(300));
    Assert.That(headers[2].Length, Is.EqualTo(4));
  }

  // ── Block lacing reassembly (Xiph) ────────────────────────────────────────────

  [Test]
  public void XiphLacedBlock_SplitsIntoIndividualFrames() {
    // SimpleBlock with Xiph lacing (flags bit 1 set → 0x02): 3 frames of 2, 3 and 4 bytes.
    var f0 = new byte[] { 0xA1, 0xA2 };
    var f1 = new byte[] { 0xB1, 0xB2, 0xB3 };
    var f2 = new byte[] { 0xC1, 0xC2, 0xC3, 0xC4 };
    var lace = new MemoryStream();
    lace.WriteByte(2);        // frame count - 1
    lace.WriteByte(2);        // len(f0)
    lace.WriteByte(3);        // len(f1); f2 is the remainder
    lace.Write(f0); lace.Write(f1); lace.Write(f2);

    var entry = AudioTrackEntry(1, "A_PCM/INT/LIT", null, channels: 1, rate: 8000, bits: 16);
    var mkv = MakeAudioMkv(entry, SimpleBlock(1, 0x02 /* Xiph lacing */, lace.ToArray()));

    var result = new MkvDemuxer().Demux(mkv);
    var track = result.Tracks.Single();
    Assert.That(track.Frames.Count, Is.EqualTo(3));
    Assert.That(track.Frames[0].Data, Is.EqualTo(f0));
    Assert.That(track.Frames[1].Data, Is.EqualTo(f1));
    Assert.That(track.Frames[2].Data, Is.EqualTo(f2));
  }

  // ── Audio element parse ───────────────────────────────────────────────────────

  [Test]
  public void Demuxer_ParsesAudioChannelsAndRate() {
    var entry = AudioTrackEntry(1, "A_PCM/INT/LIT", null, channels: 2, rate: 44100, bits: 24);
    var mkv = MakeAudioMkv(entry, SimpleBlock(1, 0x00, new byte[] { 1, 2, 3, 4, 5, 6 }));
    var result = new MkvDemuxer().Demux(mkv);
    var track = result.Tracks.Single();
    Assert.That(track.AudioChannels, Is.EqualTo(2));
    Assert.That(track.AudioSampleRate, Is.EqualTo(44100));
    Assert.That(track.AudioBitDepth, Is.EqualTo(24));
  }
}
