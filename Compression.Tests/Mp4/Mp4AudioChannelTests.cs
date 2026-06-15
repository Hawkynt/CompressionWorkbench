#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Aac;
using FileFormat.Mp4;

namespace Compression.Tests.Mp4;

/// <summary>
/// Behaviour tests for MP4 per-track audio channel extraction. A <c>sowt</c>/<c>lpcm</c>
/// PCM audio trak decodes to <c>TRACKn_&lt;CHANNEL&gt;.wav</c> entries that byte-match the
/// source samples; the AAC ADTS-wrap path is pinned bit-exact against a known
/// AudioSpecificConfig; unsupported codecs fall back to raw-only with a metadata note.
/// </summary>
[TestFixture]
public class Mp4AudioChannelTests {

  private static byte[] BuildAtom(string type, byte[] body) {
    var atom = new byte[8 + body.Length];
    BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)atom.Length);
    Encoding.ASCII.GetBytes(type, 0, 4, atom, 4);
    body.CopyTo(atom, 8);
    return atom;
  }

  private static byte[] Container(string type, params byte[][] children) {
    var total = children.Sum(c => c.Length);
    var atom = new byte[8 + total];
    BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)atom.Length);
    Encoding.ASCII.GetBytes(type, 0, 4, atom, 4);
    var off = 8;
    foreach (var c in children) { c.CopyTo(atom, off); off += c.Length; }
    return atom;
  }

  /// <summary>
  /// Builds an audio sample entry (QuickTime SoundDescription v0) carrying the given
  /// fourcc, channel count, bit depth and sample rate, with an optional codec-config child
  /// box (e.g. esds) appended inside the entry.
  /// </summary>
  private static byte[] AudioSampleEntry(string fourcc, int channels, int bits, int sampleRate, byte[]? configBox = null) {
    // 8 (size+type) + 6 reserved + 2 data_ref + 8 (version/rev/vendor) + 2 chan + 2 bits
    // + 2 compID + 2 packetSize + 4 sampleRate(16.16) = 28 bytes of body after the type.
    var body = new byte[28 + (configBox?.Length ?? 0)];
    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(16), (ushort)channels);     // channelcount @ sd+8
    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(18), (ushort)bits);         // samplesize  @ sd+10
    BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(24), (uint)(sampleRate << 16)); // 16.16 @ sd+16
    configBox?.CopyTo(body, 28);
    return BuildAtom(fourcc, body);
  }

  /// <summary>Wraps a sample entry in stsd.</summary>
  private static byte[] Stsd(byte[] sampleEntry) {
    var body = new byte[8 + sampleEntry.Length];
    BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), 1); // entry count
    sampleEntry.CopyTo(body, 8);
    return BuildAtom("stsd", body);
  }

  /// <summary>Builds a complete single-audio-track MP4 with the given sample entry and PCM samples.</summary>
  private static byte[] MakeAudioMp4(byte[] sampleEntry, byte[][] samples) {
    var ftyp = BuildAtom("ftyp", [.."isom"u8, ..new byte[4], .."isom"u8]);

    var mdatPayload = samples.SelectMany(s => s).ToArray();
    var mdat = BuildAtom("mdat", mdatPayload);
    var mdatBodyOffset = (uint)(ftyp.Length + 8);

    var stsd = Stsd(sampleEntry);

    // stsz: variable sizes table
    var stszBody = new byte[12 + samples.Length * 4];
    BinaryPrimitives.WriteUInt32BigEndian(stszBody.AsSpan(8), (uint)samples.Length);
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteUInt32BigEndian(stszBody.AsSpan(12 + i * 4), (uint)samples[i].Length);
    var stsz = BuildAtom("stsz", stszBody);

    var stscBody = new byte[20];
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(8), 1);
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(12), (uint)samples.Length);
    BinaryPrimitives.WriteUInt32BigEndian(stscBody.AsSpan(16), 1);
    var stsc = BuildAtom("stsc", stscBody);

    var stcoBody = new byte[12];
    BinaryPrimitives.WriteUInt32BigEndian(stcoBody.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(stcoBody.AsSpan(8), mdatBodyOffset);
    var stco = BuildAtom("stco", stcoBody);

    var stbl = Container("stbl", stsd, stsc, stsz, stco);
    var minf = Container("minf", Container("dinf"), stbl);

    var hdlrBody = new byte[4 + 4 + 4 + 12 + 5];
    "soun"u8.CopyTo(hdlrBody.AsSpan(8));
    var hdlr = BuildAtom("hdlr", hdlrBody);

    var mdhdBody = new byte[24];
    BinaryPrimitives.WriteUInt32BigEndian(mdhdBody.AsSpan(12), 1000);
    var mdhd = BuildAtom("mdhd", mdhdBody);
    var mdia = Container("mdia", mdhd, hdlr, minf);

    var tkhdBody = new byte[84];
    tkhdBody[3] = 1;
    BinaryPrimitives.WriteUInt32BigEndian(tkhdBody.AsSpan(12), 1);
    var tkhd = BuildAtom("tkhd", tkhdBody);
    var trak = Container("trak", tkhd, mdia);

    var mvhd = BuildAtom("mvhd", new byte[108]);
    var moov = Container("moov", mvhd, trak);

    return [.. ftyp, .. mdat, .. moov];
  }

  // ── PCM (sowt) ─────────────────────────────────────────────────────────────────

  [Test]
  public void SowtStereoPcm_ProducesLeftRightChannels() {
    var sample = new byte[] { 0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44 };
    var entry = AudioSampleEntry("sowt", channels: 2, bits: 16, sampleRate: 48000);
    var mp4 = MakeAudioMp4(entry, [sample]);

    using var ms = new MemoryStream(mp4);
    var entries = new Mp4FormatDescriptor().List(ms, null);
    var channels = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channels.Count, Is.EqualTo(2));
    Assert.That(channels.Any(e => e.Name == "TRACK0_LEFT.wav"), Is.True);
    Assert.That(channels.Any(e => e.Name == "TRACK0_RIGHT.wav"), Is.True);
  }

  [Test]
  public void SowtStereoPcm_LeftChannelMatchesSamples() {
    var sample = new byte[] { 0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44 };
    var entry = AudioSampleEntry("sowt", channels: 2, bits: 16, sampleRate: 48000);
    var mp4 = MakeAudioMp4(entry, [sample]);

    using var ms = new MemoryStream(mp4);
    var desc = new Mp4FormatDescriptor();
    using var output = new MemoryStream();
    desc.ExtractEntry(ms, "TRACK0_LEFT.wav", output, null);
    var left = output.ToArray().AsSpan(44).ToArray();
    Assert.That(left, Is.EqualTo(new byte[] { 0x11, 0x11, 0x33, 0x33 }));
  }

  [Test]
  public void Metadata_RecordsPcmCodec() {
    var entry = AudioSampleEntry("lpcm", channels: 1, bits: 16, sampleRate: 8000);
    var mp4 = MakeAudioMp4(entry, [new byte[] { 1, 2, 3, 4 }]);
    using var ms = new MemoryStream(mp4);
    using var output = new MemoryStream();
    new Mp4FormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(text, Does.Contain("track0_codec=").IgnoreCase);
  }

  [Test]
  public void RawTrackEntry_AlwaysPreservedForAudio() {
    var entry = AudioSampleEntry("sowt", channels: 2, bits: 16, sampleRate: 48000);
    var mp4 = MakeAudioMp4(entry, [new byte[] { 1, 2, 3, 4 }]);
    using var ms = new MemoryStream(mp4);
    var entries = new Mp4FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Kind == "Track" && e.Name.Contains("soun")), Is.True);
  }

  [Test]
  public void UnsupportedAmr_FallsBackWithMetadataNote() {
    var entry = AudioSampleEntry("samr", channels: 1, bits: 16, sampleRate: 8000);
    var mp4 = MakeAudioMp4(entry, [new byte[] { 1, 2, 3, 4 }]);
    using var ms = new MemoryStream(mp4);
    var entries = new Mp4FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
    using var output = new MemoryStream();
    ms.Position = 0;
    new Mp4FormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(text, Does.Contain("track0_decode=unsupported"));
  }

  // ── AAC ADTS wrap (pinned bit-exact) ──────────────────────────────────────────

  [Test]
  public void AacAdtsWrapper_ProducesExpectedHeaderForKnownAsc() {
    // AudioSpecificConfig 0x12 0x10 → object type 2 (AAC-LC), sampleRateIndex 4 (44100),
    // channelConfig 2 (stereo). One 5-byte access unit → ADTS frame length 7+5 = 12.
    var (_, srIdx, channelConfig) = AacCodec.ParseAudioSpecificConfig(new byte[] { 0x12, 0x10 });
    Assert.That(srIdx, Is.EqualTo(4));
    Assert.That(channelConfig, Is.EqualTo(2));

    var au = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 };
    var expectedHeader = AacAdtsReader.BuildHeader(profile: 1, srIdx, channelConfig, frameLength: 7 + au.Length);

    var wrapped = AacAdtsWrapper.Wrap([au], srIdx, channelConfig);
    Assert.That(wrapped.Length, Is.EqualTo(7 + au.Length));
    Assert.That(wrapped.AsSpan(0, 7).ToArray(), Is.EqualTo(expectedHeader));
    Assert.That(wrapped.AsSpan(7).ToArray(), Is.EqualTo(au));

    // Re-parse the header we built: it must round-trip to the same fields.
    var parsed = AacAdtsReader.ParseHeader(wrapped);
    Assert.That(parsed.Profile, Is.EqualTo(1));
    Assert.That(parsed.SampleRateIndex, Is.EqualTo(4));
    Assert.That(parsed.ChannelConfiguration, Is.EqualTo(2));
    Assert.That(parsed.FrameLength, Is.EqualTo(12));
  }

  [Test]
  public void AacAdtsWrapper_WrapsEachAccessUnitIndependently() {
    var (_, srIdx, channelConfig) = AacCodec.ParseAudioSpecificConfig(new byte[] { 0x12, 0x10 });
    var a = new byte[] { 1, 2, 3 };
    var b = new byte[] { 4, 5 };
    var wrapped = AacAdtsWrapper.Wrap([a, b], srIdx, channelConfig);
    // 2 frames: (7+3) + (7+2) = 19 bytes; second frame's payload starts at offset 17.
    Assert.That(wrapped.Length, Is.EqualTo(19));
    Assert.That(wrapped[10], Is.EqualTo(0xFF)); // second ADTS sync
    Assert.That(wrapped.AsSpan(17).ToArray(), Is.EqualTo(b));
  }
}
