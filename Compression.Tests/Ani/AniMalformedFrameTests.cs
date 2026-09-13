using System.Buffers.Binary;
using System.Text;
using CompressionWorkbench.FileFormat.Ani;
using CompressionWorkbench.FileFormat.Ico;

namespace Compression.Tests.Ani;

/// <summary>
/// What the ANI pseudo-archive does with a frame it cannot unpack. The descriptor surfaces such a
/// frame's raw bytes rather than dropping it or failing the whole listing, and that only works if
/// the reader hands the frame over instead of refusing the file.
/// </summary>
[TestFixture]
public class AniMalformedFrameTests {

  /// <summary>A frame that is not an ICO or a CUR: its type field is neither 1 nor 2.</summary>
  private static byte[] UnparseableFrame() {
    var frame = new byte[22];
    BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0), 0);   // reserved
    BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), 99);  // type
    BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), 1);   // count
    return frame;
  }

  private static byte[] MinimalBmp() {
    const int fileHeader = 14, infoHeader = 40, pixelBytes = 8 * 8 * 4;
    var fileLen = fileHeader + infoHeader + pixelBytes;
    var data = new byte[fileLen];
    data[0] = (byte)'B'; data[1] = (byte)'M';
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2, 4), (uint)fileLen);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10, 4), fileHeader + infoHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14, 4), infoHeader);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18, 4), 8);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22, 4), 8);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28, 2), 32);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(34, 4), pixelBytes);
    for (var i = fileHeader + infoHeader; i < fileLen; i++) data[i] = 0x55;
    return data;
  }

  private static byte[] GoodFrame() => IcoWriter.BuildCur([new IcoWriter.Image(MinimalBmp())]);

  /// <summary>Wraps the given frames in a RIFF "ACON" container with a 36-byte anih.</summary>
  private static byte[] BuildAni(params byte[][] frames) {
    static void Chunk(Stream to, string id, byte[] body) {
      to.Write(Encoding.ASCII.GetBytes(id));
      Span<byte> size = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
      to.Write(size);
      to.Write(body);
      if ((body.Length & 1) != 0) to.WriteByte(0);
    }

    var anih = new byte[36];
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(0, 4), 36);
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(4, 4), (uint)frames.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(8, 4), (uint)frames.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(28, 4), 6);
    BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(32, 4), 0x01);

    using var body = new MemoryStream();
    Chunk(body, "anih", anih);

    using var fram = new MemoryStream();
    fram.Write("fram"u8);
    foreach (var frame in frames)
      Chunk(fram, "icon", frame);
    Chunk(body, "LIST", fram.ToArray());

    var inner = body.ToArray();
    using var file = new MemoryStream();
    file.Write("RIFF"u8);
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(4 + inner.Length));
    file.Write(size);
    file.Write("ACON"u8);
    file.Write(inner);
    return file.ToArray();
  }

  [Test, Category("EdgeCase")]
  public void Read_FrameThatIsNotAnIconOrCursor_StillYieldsTheFile() {
    var ani = AniReader.Read(BuildAni(GoodFrame(), UnparseableFrame()));

    Assert.That(ani.Frames, Has.Count.EqualTo(2), "one frame that cannot be unpacked does not lose the other");
  }

  [Test, Category("EdgeCase")]
  public void Read_FrameThatIsNotAnIconOrCursor_KeepsItsBytesVerbatim() {
    var rubbish = UnparseableFrame();

    var ani = AniReader.Read(BuildAni(rubbish));

    Assert.That(ani.Frames[0], Is.EqualTo(rubbish).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void List_FrameThatCannotBeUnpacked_IsSurfacedAsRawBytes() {
    using var ms = new MemoryStream(BuildAni(GoodFrame(), UnparseableFrame()));

    var entries = new AniFormatDescriptor().List(ms, null);

    var raw = entries.Where(e => e.Name.EndsWith("raw_frame.bin")).ToList();
    Assert.That(raw, Has.Count.EqualTo(1), "the frame that would not unpack is listed as its own bytes");
    Assert.That(raw[0].Name, Does.StartWith("frame_001/"));
    Assert.That(entries.Any(e => e.Name.StartsWith("frame_000/") && e.Name.EndsWith(".bmp")), Is.True,
      "and the frame that would unpack still is");
  }

  [Test, Category("EdgeCase")]
  public void Extract_FrameThatCannotBeUnpacked_WritesTheRawBytesToDisk() {
    var rubbish = UnparseableFrame();
    using var ms = new MemoryStream(BuildAni(rubbish));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      new AniFormatDescriptor().Extract(ms, tmp, null, null);
      var raws = Directory.GetFiles(tmp, "raw_frame.bin", SearchOption.AllDirectories);
      Assert.That(raws, Has.Length.EqualTo(1));
      Assert.That(File.ReadAllBytes(raws[0]), Is.EqualTo(rubbish).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }
}
