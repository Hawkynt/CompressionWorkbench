#pragma warning disable CS1591
using FileFormat.Gif;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Gif;

[TestFixture]
public class GifTests {

  // Minimal hand-assembled 2-frame GIF89a: 2x1 pixel, global palette, no GCE.
  // Frame 0 pixel = color 0 (red), Frame 1 pixel = color 1 (green).
  private static byte[] MakeTwoFrameGif() {
    var ms = new MemoryStream();
    var bw = new BinaryWriter(ms);

    // Header + LSD: "GIF89a", width=2, height=1, packed=0x80 (global CT, size=0 → 2 entries), bg=0, aspect=0
    bw.Write("GIF89a"u8.ToArray());
    bw.Write((ushort)2); bw.Write((ushort)1); bw.Write((byte)0x80); bw.Write((byte)0); bw.Write((byte)0);

    // Global color table: 2*3 bytes (red, green)
    bw.Write(new byte[] { 0xFF, 0x00, 0x00 });
    bw.Write(new byte[] { 0x00, 0xFF, 0x00 });

    // Netscape loop extension: 0x21 0xFF 0x0B "NETSCAPE2.0" 0x03 0x01 0x00 0x00 0x00 (should be dropped from frames)
    bw.Write((byte)0x21); bw.Write((byte)0xFF); bw.Write((byte)0x0B);
    bw.Write("NETSCAPE2.0"u8.ToArray());
    bw.Write((byte)0x03); bw.Write((byte)0x01); bw.Write((ushort)0); bw.Write((byte)0);

    // Frame 0: GCE + Image Descriptor with VALID LZW: [clear=4, idx0, idx1, eoi=5]
    // packed LSB-first into bytes 0x44 0x02 (3-bit codes from lzwMin=2).
    WriteGce(bw, delay: 10);
    WriteImage(bw, width: 2, lzwMin: 2, imageData: [0x44, 0x02]);

    // Frame 1
    WriteGce(bw, delay: 10);
    WriteImage(bw, width: 2, lzwMin: 2, imageData: [0x44, 0x02]);

    bw.Write((byte)0x3B); // Trailer
    return ms.ToArray();
  }

  private static void WriteGce(BinaryWriter bw, int delay) {
    bw.Write((byte)0x21); bw.Write((byte)0xF9);
    bw.Write((byte)0x04); bw.Write((byte)0x00); bw.Write((ushort)delay); bw.Write((byte)0x00);
    bw.Write((byte)0x00);
  }

  private static void WriteImage(BinaryWriter bw, int width, byte lzwMin, byte[] imageData) {
    bw.Write((byte)0x2C);
    bw.Write((ushort)0); bw.Write((ushort)0);          // left, top
    bw.Write((ushort)width); bw.Write((ushort)1);      // w, h
    bw.Write((byte)0x00);                              // packed: no local CT
    bw.Write(lzwMin);
    bw.Write((byte)imageData.Length); bw.Write(imageData);
    bw.Write((byte)0x00);                              // block terminator
  }

  [Test]
  public void ReadsTwoFrames() {
    var data = MakeTwoFrameGif();
    var frames = new GifReader().Read(data);
    Assert.That(frames, Has.Count.EqualTo(2));
  }

  [Test]
  public void EachFrameIsStandaloneGif() {
    var data = MakeTwoFrameGif();
    var frames = new GifReader().Read(data);
    foreach (var f in frames) {
      Assert.That(f.Data[0], Is.EqualTo((byte)'G'));
      Assert.That(f.Data[1], Is.EqualTo((byte)'I'));
      Assert.That(f.Data[2], Is.EqualTo((byte)'F'));
      Assert.That(f.Data[^1], Is.EqualTo((byte)0x3B));
    }
  }

  [Test]
  public void FrameContainsGceBeforeImageDescriptor() {
    var data = MakeTwoFrameGif();
    var frames = new GifReader().Read(data);
    var first = frames[0].Data;
    // Header+LSD+GCT = 6+7+6 = 19, next should be GCE (0x21 0xF9) and then Image Descriptor (0x2C)
    Assert.That(first[19], Is.EqualTo((byte)0x21));
    Assert.That(first[20], Is.EqualTo((byte)0xF9));
    var idPos = Array.IndexOf(first, (byte)0x2C, 19);
    Assert.That(idPos, Is.GreaterThan(19));
  }

  [Test]
  public void DescriptorListReturnsFrameFoldersWithColorspaceTree() {
    // After Phase 35 lazy-rewrite the GIF descriptor now decodes frames to RGBA32 and
    // emits a frame folder containing the composite frame, alpha, and the full
    // colorspace tree — matching APNG/TIFF/MPO layout.
    var data = MakeTwoFrameGif();
    var desc = new GifFormatDescriptor();
    using var ms = new MemoryStream(data);
    var entries = desc.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    // Two frame folders, each prefixed with "frame_NNN_WxH_BBpp/".
    Assert.That(names, Has.Some.StartsWith("frame_000_2x1_32bpp/"));
    Assert.That(names, Has.Some.StartsWith("frame_001_2x1_32bpp/"));

    // Composite frame PNG present.
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/frame_000.png"));
    Assert.That(names, Does.Contain("frame_001_2x1_32bpp/frame_001.png"));

    // Colorspace tree present (sample a few).
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/colorspace/RGB/R.png"));
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/colorspace/YCbCr/Y.png"));
    Assert.That(names, Does.Contain("frame_001_2x1_32bpp/colorspace/RGB/R.png"));
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/Alpha.png"),
      "RGBA32 frame format must announce its colorspace-agnostic Alpha.png");
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
      // PNG signature: 89 50 4E 47 0D 0A 1A 0A
      Assert.That(bytes[..8], Is.EquivalentTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }
}
