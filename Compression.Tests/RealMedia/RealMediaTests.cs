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
