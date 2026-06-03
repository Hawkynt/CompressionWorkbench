#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Asf;

namespace Compression.Tests.Asf;

/// <summary>
/// Pins the ASF descriptor: a hand-crafted minimal ASF (Header Object with File
/// Properties + Content Description + a WMA v2 audio Stream Properties, followed by
/// a small Data Object) must surface FULL.asf, metadata.ini fields, a per-stream
/// info entry and the Data Object payload as data/packets.bin. Truncated objects
/// must degrade gracefully.
/// </summary>
[TestFixture]
public class AsfTests {

  private static readonly byte[] HeaderObject =
    [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] FilePropertiesObject =
    [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] StreamPropertiesObject =
    [0x91, 0x07, 0xDC, 0xB7, 0xB7, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] ContentDescriptionObject =
    [0x33, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] ExtendedContentDescriptionObject =
    [0x40, 0xA4, 0xD0, 0xD2, 0x07, 0xE3, 0xD2, 0x11, 0x97, 0xF0, 0x00, 0xA0, 0xC9, 0x5E, 0xA8, 0x50];
  private static readonly byte[] DataObject =
    [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] AudioStreamType =
    [0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

  [Test]
  public void Asf_MinimalFile_SurfacesMetadataStreamAndPackets() {
    var asf = BuildMinimalAsf(out var dataPayload);
    using var ms = new MemoryStream(asf);
    var entries = new AsfFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.asf" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);

    using var metaStream = new MemoryStream();
    new AsfFormatDescriptor().ExtractEntry(new MemoryStream(asf), "metadata.ini", metaStream, null);
    var metaText = Encoding.UTF8.GetString(metaStream.ToArray());
    Assert.That(metaText, Does.Contain("title = Hello World"));
    Assert.That(metaText, Does.Contain("author = Tester"));
    Assert.That(metaText, Does.Contain("data_packets = 1"));
    Assert.That(metaText, Does.Contain("max_bitrate = 128000"));

    Assert.That(entries.Any(e => e.Name == "streams/stream_03.info.txt"), Is.True, "expected stream number 3 info");

    using var siStream = new MemoryStream();
    new AsfFormatDescriptor().ExtractEntry(new MemoryStream(asf), "streams/stream_03.info.txt", siStream, null);
    var siText = Encoding.UTF8.GetString(siStream.ToArray());
    Assert.That(siText, Does.Contain("type = audio"));
    Assert.That(siText, Does.Contain("codec = wmav2"));
    Assert.That(siText, Does.Contain("format_tag = 0x0161"));
    Assert.That(siText, Does.Contain("channels = 2"));
    Assert.That(siText, Does.Contain("sample_rate = 44100"));

    Assert.That(entries.Any(e => e.Name == "data/packets.bin" && e.Kind == "Stream"), Is.True);
    using var pkStream = new MemoryStream();
    new AsfFormatDescriptor().ExtractEntry(new MemoryStream(asf), "data/packets.bin", pkStream, null);
    Assert.That(pkStream.ToArray(), Is.EqualTo(dataPayload));
  }

  [Test]
  public void Asf_ExtendedContentDescription_SurfacesTags() {
    var asf = BuildAsfWithExtendedTags();
    using var ms = new MemoryStream(asf);
    var entries = new AsfFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "metadata/tags.ini"), Is.True);
    using var tagStream = new MemoryStream();
    new AsfFormatDescriptor().ExtractEntry(new MemoryStream(asf), "metadata/tags.ini", tagStream, null);
    var tagText = Encoding.UTF8.GetString(tagStream.ToArray());
    Assert.That(tagText, Does.Contain("WM/AlbumTitle = Greatest Hits"));
    Assert.That(tagText, Does.Contain("WM/Year = 2024"));
  }

  [Test]
  public void Asf_TruncatedHeaderObject_DegradesGracefully() {
    // A header object that claims 3 children but contains only a truncated stub.
    using var ms = new MemoryStream();
    ms.Write(HeaderObject);
    WriteU64(ms, 64);   // header size (larger than actual content)
    WriteU32(ms, 3);    // numObjects
    ms.WriteByte(1);
    ms.WriteByte(2);
    // A single child GUID + a size that overruns the buffer.
    ms.Write(FilePropertiesObject);
    WriteU64(ms, 9999); // bogus oversized child
    var asf = ms.ToArray();

    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new AsfFormatDescriptor().List(new MemoryStream(asf), null), Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "FULL.asf"), Is.True);
  }

  // ── synthetic ASF builders ──────────────────────────────────────────────────

  private static byte[] BuildMinimalAsf(out byte[] dataPayload) {
    var fileProps = BuildFilePropertiesBody();
    var contentDesc = BuildContentDescriptionBody("Hello World", "Tester", "(c) 2024", "desc", "");
    var streamProps = BuildAudioStreamPropertiesBody(streamNumber: 3, formatTag: 0x0161, channels: 2, sampleRate: 44100, byteRate: 16000, bits: 16);

    var children = new List<byte[]> {
      WrapObject(FilePropertiesObject, fileProps),
      WrapObject(ContentDescriptionObject, contentDesc),
      WrapObject(StreamPropertiesObject, streamProps),
    };

    var header = BuildHeaderObject(children);

    dataPayload = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
    var data = BuildDataObject(dataPayload, totalPackets: 1);

    using var ms = new MemoryStream();
    ms.Write(header);
    ms.Write(data);
    return ms.ToArray();
  }

  private static byte[] BuildAsfWithExtendedTags() {
    var fileProps = BuildFilePropertiesBody();
    var ext = BuildExtendedContentDescriptionBody();
    var children = new List<byte[]> {
      WrapObject(FilePropertiesObject, fileProps),
      WrapObject(ExtendedContentDescriptionObject, ext),
    };
    var header = BuildHeaderObject(children);
    var data = BuildDataObject([0x01, 0x02], totalPackets: 1);
    using var ms = new MemoryStream();
    ms.Write(header);
    ms.Write(data);
    return ms.ToArray();
  }

  private static byte[] BuildHeaderObject(List<byte[]> children) {
    using var body = new MemoryStream();
    foreach (var c in children) body.Write(c);
    var bodyBytes = body.ToArray();
    var size = (ulong)(16 + 8 + 4 + 1 + 1 + bodyBytes.Length);
    using var ms = new MemoryStream();
    ms.Write(HeaderObject);
    WriteU64(ms, size);
    WriteU32(ms, (uint)children.Count);
    ms.WriteByte(0);
    ms.WriteByte(0);
    ms.Write(bodyBytes);
    return ms.ToArray();
  }

  private static byte[] WrapObject(byte[] guid, byte[] body) {
    var size = (ulong)(16 + 8 + body.Length);
    using var ms = new MemoryStream();
    ms.Write(guid);
    WriteU64(ms, size);
    ms.Write(body);
    return ms.ToArray();
  }

  private static byte[] BuildFilePropertiesBody() {
    using var ms = new MemoryStream();
    ms.Write(new byte[16]);            // file id GUID
    WriteU64(ms, 5000);                // file size
    WriteU64(ms, 132537600000000000);  // creation date (FILETIME)
    WriteU64(ms, 1);                   // data packets count
    WriteU64(ms, 100000000);           // play duration (100ns) = 10s
    WriteU64(ms, 100000000);           // send duration
    WriteU64(ms, 0);                   // preroll
    WriteU32(ms, 2);                   // flags
    WriteU32(ms, 100);                 // min packet size
    WriteU32(ms, 200);                 // max packet size
    WriteU32(ms, 128000);              // max bitrate
    return ms.ToArray();
  }

  private static byte[] BuildContentDescriptionBody(string title, string author, string copyright, string desc, string rating) {
    var t = Utf16(title);
    var a = Utf16(author);
    var c = Utf16(copyright);
    var d = Utf16(desc);
    var r = Utf16(rating);
    using var ms = new MemoryStream();
    WriteU16(ms, (ushort)t.Length);
    WriteU16(ms, (ushort)a.Length);
    WriteU16(ms, (ushort)c.Length);
    WriteU16(ms, (ushort)d.Length);
    WriteU16(ms, (ushort)r.Length);
    ms.Write(t); ms.Write(a); ms.Write(c); ms.Write(d); ms.Write(r);
    return ms.ToArray();
  }

  private static byte[] BuildExtendedContentDescriptionBody() {
    using var ms = new MemoryStream();
    WriteU16(ms, 2); // count
    WriteExtTag(ms, "WM/AlbumTitle", valueType: 0, Utf16("Greatest Hits"));
    WriteExtTag(ms, "WM/Year", valueType: 0, Utf16("2024"));
    return ms.ToArray();
  }

  private static void WriteExtTag(MemoryStream ms, string name, int valueType, byte[] value) {
    var n = Utf16(name);
    WriteU16(ms, (ushort)n.Length);
    ms.Write(n);
    WriteU16(ms, (ushort)valueType);
    WriteU16(ms, (ushort)value.Length);
    ms.Write(value);
  }

  private static byte[] BuildAudioStreamPropertiesBody(int streamNumber, int formatTag, int channels, int sampleRate, int byteRate, int bits) {
    using var typeSpecific = new MemoryStream();
    WriteU16(typeSpecific, (ushort)formatTag);
    WriteU16(typeSpecific, (ushort)channels);
    WriteU32(typeSpecific, (uint)sampleRate);
    WriteU32(typeSpecific, (uint)byteRate);
    WriteU16(typeSpecific, (ushort)(channels * bits / 8)); // block align
    WriteU16(typeSpecific, (ushort)bits);
    WriteU16(typeSpecific, 0); // cbSize
    var ts = typeSpecific.ToArray();

    using var ms = new MemoryStream();
    ms.Write(AudioStreamType);            // stream type GUID
    ms.Write(new byte[16]);               // error correction GUID
    WriteU64(ms, 0);                      // time offset
    WriteU32(ms, (uint)ts.Length);        // type-specific length
    WriteU32(ms, 0);                      // error-correction data length
    WriteU16(ms, (ushort)(streamNumber & 0x7F)); // flags (stream number)
    WriteU32(ms, 0);                      // reserved
    ms.Write(ts);
    return ms.ToArray();
  }

  private static byte[] BuildDataObject(byte[] payload, ulong totalPackets) {
    var size = (ulong)(16 + 8 + 16 + 8 + 2 + payload.Length);
    using var ms = new MemoryStream();
    ms.Write(DataObject);
    WriteU64(ms, size);
    ms.Write(new byte[16]); // file id GUID
    WriteU64(ms, totalPackets);
    WriteU16(ms, 0);        // reserved
    ms.Write(payload);
    return ms.ToArray();
  }

  private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s + "\0");

  private static void WriteU16(MemoryStream ms, ushort v) {
    Span<byte> b = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(b, v);
    ms.Write(b);
  }

  private static void WriteU32(MemoryStream ms, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    ms.Write(b);
  }

  private static void WriteU64(MemoryStream ms, ulong v) {
    Span<byte> b = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(b, v);
    ms.Write(b);
  }
}
