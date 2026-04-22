#pragma warning disable CS1591
using FileFormat.Mp4;

namespace Compression.Tests.Video;

[TestFixture]
public class BoxParserTests {

  private static byte[] MakeBox(string type, byte[] body) {
    var size = 8 + body.Length;
    var result = new byte[size];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result, (uint)size);
    System.Text.Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
    body.CopyTo(result, 8);
    return result;
  }

  [Test]
  public void ParsesTopLevelBoxes() {
    using var ms = new MemoryStream();
    ms.Write(MakeBox("ftyp", new byte[16]));
    ms.Write(MakeBox("mdat", new byte[32]));
    var boxes = new BoxParser().Parse(ms.ToArray());
    Assert.That(boxes, Has.Count.EqualTo(2));
    Assert.That(boxes[0].Type, Is.EqualTo("ftyp"));
    Assert.That(boxes[1].Type, Is.EqualTo("mdat"));
  }

  [Test]
  public void DescendsIntoCompoundBoxes() {
    // moov is compound; trak is compound; tkhd is a leaf.
    var tkhd = MakeBox("tkhd", new byte[20]);
    var trak = MakeBox("trak", tkhd);
    var moov = MakeBox("moov", trak);
    var boxes = new BoxParser().Parse(moov);
    Assert.That(boxes, Has.Count.EqualTo(1));
    Assert.That(boxes[0].Children, Is.Not.Null.And.Count.EqualTo(1));
    Assert.That(boxes[0].Children![0].Children!.Single().Type, Is.EqualTo("tkhd"));
  }

  [Test]
  public void FindWalksNestedBoxes() {
    var inner = MakeBox("hdlr", new byte[24]);
    var mdia = MakeBox("mdia", inner);
    var trak = MakeBox("trak", mdia);
    var moov = MakeBox("moov", trak);
    var boxes = new BoxParser().Parse(moov);
    var hdlr = BoxParser.Find(boxes, "hdlr");
    Assert.That(hdlr, Is.Not.Null);
    Assert.That(hdlr!.Type, Is.EqualTo("hdlr"));
  }
}
