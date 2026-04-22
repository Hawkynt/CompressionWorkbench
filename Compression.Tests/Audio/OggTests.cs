#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Ogg;

namespace Compression.Tests.Audio;

[TestFixture]
public class OggTests {

  // Builds one Ogg page containing segments covering `payload`. The trailing
  // 0-length terminator is only emitted when payload length is NOT a multiple
  // of 255 — otherwise the packet is in continuation state into the next page.
  private static byte[] MakePage(uint serial, byte flags, uint pageSeq, byte[] payload) {
    var lengths = new List<byte>();
    var remain = payload.Length;
    while (remain >= 255) { lengths.Add(255); remain -= 255; }
    if (remain > 0 || payload.Length == 0) lengths.Add((byte)remain);

    using var ms = new MemoryStream();
    ms.Write("OggS"u8);
    ms.WriteByte(0); // stream structure version
    ms.WriteByte(flags);
    Span<byte> le8 = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(le8, 0); ms.Write(le8);      // granule position
    Span<byte> le4 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(le4, serial); ms.Write(le4); // serial
    BinaryPrimitives.WriteUInt32LittleEndian(le4, pageSeq); ms.Write(le4);
    BinaryPrimitives.WriteUInt32LittleEndian(le4, 0); ms.Write(le4);      // checksum (unused by our parser)
    ms.WriteByte((byte)lengths.Count);
    foreach (var l in lengths) ms.WriteByte(l);
    ms.Write(payload);
    return ms.ToArray();
  }

  [Test]
  public void PageParser_EnumeratesPages() {
    var bytes = MakePage(serial: 1, flags: 0x02, pageSeq: 0, payload: [0xDE, 0xAD, 0xBE, 0xEF]);
    var pages = new OggPageParser().Pages(bytes);
    Assert.That(pages, Has.Count.EqualTo(1));
    Assert.That(pages[0].Serial, Is.EqualTo(1u));
    Assert.That(pages[0].Segments[0], Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
  }

  [Test]
  public void StreamPackets_ReassemblesContinuations() {
    // First page: 255 bytes (run → continues), second page: 4 bytes (terminates).
    var big = new byte[255];
    for (var i = 0; i < big.Length; ++i) big[i] = (byte)i;

    using var ms = new MemoryStream();
    ms.Write(MakePage(serial: 42, flags: 0x00, pageSeq: 0, payload: big));
    ms.Write(MakePage(serial: 42, flags: 0x00, pageSeq: 1, payload: [0xAA, 0xBB, 0xCC, 0xDD]));

    var packets = new OggPageParser().StreamPackets(ms.ToArray(), 42).ToArray();
    Assert.That(packets, Has.Length.EqualTo(1));
    Assert.That(packets[0].Length, Is.EqualTo(255 + 4));
    Assert.That(packets[0][0], Is.EqualTo((byte)0));
    Assert.That(packets[0][^1], Is.EqualTo((byte)0xDD));
  }

  [Test]
  public void VorbisCommentReader_ParsesVendorAndComments() {
    using var ms = new MemoryStream();
    Span<byte> le4 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(le4, 12); ms.Write(le4);
    ms.Write(Encoding.UTF8.GetBytes("libmyCodec/1"));
    BinaryPrimitives.WriteUInt32LittleEndian(le4, 2); ms.Write(le4);
    // Comment 1: "ARTIST=Alice"
    var c1 = Encoding.UTF8.GetBytes("ARTIST=Alice");
    BinaryPrimitives.WriteUInt32LittleEndian(le4, (uint)c1.Length); ms.Write(le4);
    ms.Write(c1);
    // Comment 2: "TITLE=Hello"
    var c2 = Encoding.UTF8.GetBytes("TITLE=Hello");
    BinaryPrimitives.WriteUInt32LittleEndian(le4, (uint)c2.Length); ms.Write(le4);
    ms.Write(c2);

    var parsed = new VorbisCommentReader().Read(ms.ToArray());
    Assert.That(parsed.Vendor, Is.EqualTo("libmyCodec/1"));
    Assert.That(parsed.Comments, Has.Count.EqualTo(2));
    Assert.That(parsed.Comments[0].Key, Is.EqualTo("ARTIST"));
    Assert.That(parsed.Comments[0].Value, Is.EqualTo("Alice"));
  }
}
