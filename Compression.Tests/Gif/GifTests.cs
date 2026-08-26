#pragma warning disable CS1591
using FileFormat.Gif;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Gif;

[TestFixture]
public class GifTests {

  private static byte[] MakeTwoFrameGif() {
    var ms = new MemoryStream();
    var bw = new BinaryWriter(ms);

    bw.Write("GIF89a"u8.ToArray());
    bw.Write((ushort)2); bw.Write((ushort)1); bw.Write((byte)0x80); bw.Write((byte)0); bw.Write((byte)0);
    bw.Write(new byte[] { 0xFF, 0x00, 0x00 });
    bw.Write(new byte[] { 0x00, 0xFF, 0x00 });

    bw.Write((byte)0x21); bw.Write((byte)0xFF); bw.Write((byte)0x0B);
    bw.Write("NETSCAPE2.0"u8.ToArray());
    bw.Write((byte)0x03); bw.Write((byte)0x01); bw.Write((ushort)0); bw.Write((byte)0);

    WriteGce(bw, delay: 10);
    WriteImage(bw, width: 2, lzwMin: 2, imageData: [0x44, 0x02]);
    WriteGce(bw, delay: 10);
    WriteImage(bw, width: 2, lzwMin: 2, imageData: [0x44, 0x02]);

    bw.Write((byte)0x3B);
    return ms.ToArray();
  }

  private static void WriteGce(BinaryWriter bw, int delay) {
    bw.Write((byte)0x21); bw.Write((byte)0xF9);
    bw.Write((byte)0x04); bw.Write((byte)0x00); bw.Write((ushort)delay); bw.Write((byte)0x00);
    bw.Write((byte)0x00);
  }

  private static void WriteImage(BinaryWriter bw, int width, byte lzwMin, byte[] imageData) {
    bw.Write((byte)0x2C);
    bw.Write((ushort)0); bw.Write((ushort)0);
    bw.Write((ushort)width); bw.Write((ushort)1);
    bw.Write((byte)0x00);
    bw.Write(lzwMin);
    bw.Write((byte)imageData.Length); bw.Write(imageData);
    bw.Write((byte)0x00);
  }

  [Test]
  public void DecoderComposesTwoFrames() {
    var frames = new GifPixelDecoder().Decode(MakeTwoFrameGif());

    Assert.That(frames, Has.Count.EqualTo(2));
    Assert.That(frames[0].Width, Is.EqualTo(2));
    Assert.That(frames[0].Height, Is.EqualTo(1));
    Assert.That(frames[0].DelayMs, Is.EqualTo(100));
    Assert.That(frames[0].Rgba32, Has.Length.EqualTo(8));
    Assert.That(frames[0].Rgba32[..4], Is.EqualTo(new byte[] { 255, 0, 0, 255 }));
    Assert.That(frames[0].Rgba32[4..8], Is.EqualTo(new byte[] { 0, 255, 0, 255 }));
  }

  [Test]
  public void LayoutMapUsesPackageChunkBoundaries() {
    var data = MakeTwoFrameGif();
    using var stream = new MemoryStream(data);
    var blocks = GifLayoutMap.Enumerate(stream).OrderBy(b => b.Offset).ToList();

    Assert.That(blocks, Is.Not.Empty);
    Assert.That(blocks.Sum(b => b.Length), Is.EqualTo(data.Length));
    Assert.That(blocks.Any(b => b.Kind == Compression.Registry.DefragBlockKind.Used), Is.True);
  }

  [Test]
  public void DescriptorListReturnsFrameFoldersWithColorspaceTree() {
    var data = MakeTwoFrameGif();
    var desc = new GifFormatDescriptor();
    using var ms = new MemoryStream(data);
    var names = desc.List(ms, null).Select(e => e.Name).ToList();

    Assert.That(names, Has.Some.StartsWith("frame_000_2x1_32bpp/"));
    Assert.That(names, Has.Some.StartsWith("frame_001_2x1_32bpp/"));
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/frame_000.png"));
    Assert.That(names, Does.Contain("frame_001_2x1_32bpp/frame_001.png"));
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/colorspace/RGB/R.png"));
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/colorspace/YCbCr/Y.png"));
    Assert.That(names, Does.Contain("frame_001_2x1_32bpp/colorspace/RGB/R.png"));
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/Alpha.png"));
  }

  [Test]
  public void DescriptorExtractWritesPngFiles() {
    var data = MakeTwoFrameGif();
    var dir = Path.Combine(Path.GetTempPath(), "gif_test_" + Guid.NewGuid().ToString("N"));
    try {
      using (var ms = new MemoryStream(data))
        new GifFormatDescriptor().Extract(ms, dir, null, ["frame_000_2x1_32bpp/frame_000.png"]);
      var p0 = Path.Combine(dir, "frame_000_2x1_32bpp", "frame_000.png");
      Assert.That(File.Exists(p0), Is.True);
      var bytes = File.ReadAllBytes(p0);
      Assert.That(bytes[..8], Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }
}
