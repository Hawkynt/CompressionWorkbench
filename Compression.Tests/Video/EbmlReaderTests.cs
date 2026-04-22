#pragma warning disable CS1591
using FileFormat.Matroska;

namespace Compression.Tests.Video;

[TestFixture]
public class EbmlReaderTests {

  // Build an EBML element: (id bytes) (size vint) (body bytes).
  // For simplicity all IDs here are single-byte (0x80..0xFF) and sizes are
  // 1-byte vints (0x80-masked).
  private static byte[] Element(byte id, byte[] body) {
    var size = body.Length;
    if (size >= 0x80) throw new ArgumentException("only 1-byte sizes supported in this helper");
    var buf = new byte[2 + size];
    buf[0] = id;
    buf[1] = (byte)(size | 0x80);
    body.CopyTo(buf, 2);
    return buf;
  }

  [Test]
  public void ReadSingleElement() {
    var bytes = Element(0xA3, [0xDE, 0xAD]);
    var reader = new EbmlReader(bytes);
    long pos = 0;
    var el = reader.Read(ref pos);
    Assert.That(el, Is.Not.Null);
    Assert.That(el!.Value.Id, Is.EqualTo(0xA3u));
    Assert.That(el.Value.BodyLength, Is.EqualTo(2));
    Assert.That(reader.Body(el.Value).ToArray(), Is.EqualTo(new byte[] { 0xDE, 0xAD }));
  }

  [Test]
  public void ReadUnsignedIntegerElement() {
    // 3-byte big-endian value: 0x010203 = 66051
    var bytes = Element(0xD7, [0x01, 0x02, 0x03]);
    var reader = new EbmlReader(bytes);
    long pos = 0;
    var el = reader.Read(ref pos)!.Value;
    Assert.That(reader.ReadUnsigned(el), Is.EqualTo(0x010203UL));
  }

  [Test]
  public void ReadStringTrimsTrailingNul() {
    var bytes = Element(0x86, [(byte)'V', (byte)'_', (byte)'X', 0, 0]);
    var reader = new EbmlReader(bytes);
    long pos = 0;
    var el = reader.Read(ref pos)!.Value;
    Assert.That(reader.ReadString(el), Is.EqualTo("V_X"));
  }
}
