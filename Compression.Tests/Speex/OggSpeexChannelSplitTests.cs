#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Speex;
using FileFormat.Ogg;

namespace Compression.Tests.Speex;

/// <summary>
/// Pins the Ogg descriptor's Speex branch: a synthetic Ogg-Speex stream (header packet
/// + comment packet + crafted narrowband audio packets in OggS pages) surfaces a mono
/// channel WAV, and an undecodable stream falls back to the raw packet blobs without
/// throwing. Mirrors the Opus channel-split test wiring.
/// </summary>
[TestFixture]
public class OggSpeexChannelSplitTests {

  [Test]
  public void OggDescriptor_DecodableSpeex_SurfacesMonoChannel() {
    var ogg = BuildNarrowbandSpeexOgg(framesPerPacket: 1);
    using var ms = new MemoryStream(ogg);
    var entries = new OggFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.ogg"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True,
      "expected a decoded MONO.wav for the Speex stream");
    Assert.That(entries.First(e => e.Name == "MONO.wav").Method, Is.EqualTo("pcm"));
  }

  [Test]
  public void OggDescriptor_ExtractedSpeexChannelIsMonoWav() {
    var ogg = BuildNarrowbandSpeexOgg(framesPerPacket: 1);
    using var ms = new MemoryStream(ogg);
    using var output = new MemoryStream();
    new OggFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
  }

  [Test]
  public void OggDescriptor_UndecodableSpeex_FallsBackWithoutThrowing() {
    // Valid header but a bogus bitstream version inside would already fail Parse; here
    // we use a header whose body is fine but feed it as a non-Speex magic so the Speex
    // branch is taken yet ReadStreamInfo throws → descriptor swallows and falls back.
    var stream = new MemoryStream();
    var header = SpeexCodecTests.BuildHeader(8000, 0, 1, -1, 160, 0, 1, 0);
    // Corrupt the channel count to 5 → SpeexHeader.Parse throws, branch swallows it.
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28 + 20, 4), 5);
    WriteOggPage(stream, 11, 0, 0x02, [header]);
    WriteOggPage(stream, 11, 1, 0x00, [BuildCommentPacket("Codec.Speex")]);
    WriteOggPage(stream, 11, 2, 0x04, [new byte[8]]);
    var ogg = stream.ToArray();

    using var ms = new MemoryStream(ogg);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new OggFormatDescriptor().List(ms, null), Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "FULL.ogg"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
  }

  [Test]
  public void OggSpeexReader_RoundTripsHeaderAndPackets() {
    var ogg = BuildNarrowbandSpeexOgg(framesPerPacket: 1);
    using var ms = new MemoryStream(ogg);
    var reader = new OggSpeexReader(ms);
    var header = reader.ReadHeader();
    Assert.That(header.Rate, Is.EqualTo(8000));
    Assert.That(header.NbChannels, Is.EqualTo(1));
    _ = reader.TryReadComments();
    Assert.That(reader.TryReadPacket(out var audio), Is.True);
    Assert.That(audio.Length, Is.GreaterThan(0));
  }

  // ── synthetic narrowband Ogg-Speex ────────────────────────────────────────────────

  private static byte[] BuildNarrowbandSpeexOgg(int framesPerPacket) {
    var stream = new MemoryStream();
    var header = SpeexCodecTests.BuildHeader(8000, 0, 1, -1, 160, 0, framesPerPacket, 0);
    WriteOggPage(stream, 13, 0, 0x02, [header]);
    WriteOggPage(stream, 13, 1, 0x00, [BuildCommentPacket("Codec.Speex")]);
    // A submode-1 (comfort-noise) audio packet repeated; bounded near-silence frames.
    WriteOggPage(stream, 13, 2, 0x00, [BuildSubmode1Packet()]);
    WriteOggPage(stream, 13, 3, 0x04, [BuildSubmode1Packet()]);
    return stream.ToArray();
  }

  private static byte[] BuildSubmode1Packet() {
    // wideband(1)=0, m(4)=1, 3x lsp 6-bit ids=0, ol_pitch(7)=0, coef(4)=0,
    // ol_gain(5)=0, dtx(4)=0  → all-zero indices.
    Span<int> widths = stackalloc int[] { 1, 4, 6, 6, 6, 7, 4, 5, 4 };
    Span<int> vals = stackalloc int[] { 0, 1, 0, 0, 0, 0, 0, 0, 0 };
    var bits = new List<int>();
    for (var k = 0; k < widths.Length; k++)
      for (var b = widths[k] - 1; b >= 0; b--)
        bits.Add((vals[k] >> b) & 1);
    var bytes = new byte[(bits.Count + 7) / 8];
    for (var i = 0; i < bits.Count; i++)
      if (bits[i] != 0) bytes[i >> 3] |= (byte)(1 << (7 - (i & 7)));
    return bytes;
  }

  private static byte[] BuildCommentPacket(string vendor) {
    // A bare Vorbis-comment block: vendor length + vendor + comment count (0).
    var vb = Encoding.UTF8.GetBytes(vendor);
    var pkt = new byte[4 + vb.Length + 4];
    BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(0, 4), (uint)vb.Length);
    vb.CopyTo(pkt, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(4 + vb.Length, 4), 0);
    return pkt;
  }

  private static void WriteOggPage(Stream stream, uint serial, uint sequence, byte headerType, byte[][] packets) {
    var segments = new List<byte>();
    var body = new MemoryStream();
    foreach (var pkt in packets) {
      var remaining = pkt.Length;
      var written = 0;
      while (remaining >= 255) {
        segments.Add(255);
        body.Write(pkt, written, 255);
        written += 255;
        remaining -= 255;
      }
      segments.Add((byte)remaining);
      if (remaining > 0) body.Write(pkt, written, remaining);
    }

    Span<byte> header = stackalloc byte[27];
    Encoding.ASCII.GetBytes("OggS").CopyTo(header);
    header[5] = headerType;
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(14, 4), serial);
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(18, 4), sequence);
    header[26] = (byte)segments.Count;
    stream.Write(header);
    stream.Write(segments.ToArray(), 0, segments.Count);
    body.Position = 0;
    body.CopyTo(stream);
  }
}
