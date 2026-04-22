#pragma warning disable CS1591
using FileFormat.Gif;

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

    // Frame 0: GCE (0x21 0xF9 4 flags delay*2 transp 0) + Image Descriptor (0x2C, left=0, top=0, w=2, h=1, packed=0)
    WriteGce(bw, delay: 10);
    WriteImage(bw, width: 2, lzwMin: 2, imageData: [0x02, 0x44, 0x01]); // 2-pixel LZW stream for indices [0,1]

    // Frame 1
    WriteGce(bw, delay: 10);
    WriteImage(bw, width: 2, lzwMin: 2, imageData: [0x02, 0x44, 0x01]);

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
  public void DescriptorListReturnsFrameEntries() {
    var data = MakeTwoFrameGif();
    var desc = new GifFormatDescriptor();
    using var ms = new MemoryStream(data);
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("frame_000.gif"));
    Assert.That(entries[1].Name, Is.EqualTo("frame_001.gif"));
  }

  [Test]
  public void DescriptorExtractWritesFiles() {
    var data = MakeTwoFrameGif();
    var dir = Path.Combine(Path.GetTempPath(), "gif_test_" + Guid.NewGuid().ToString("N"));
    try {
      using (var ms = new MemoryStream(data))
        new GifFormatDescriptor().Extract(ms, dir, null, null);
      Assert.That(File.Exists(Path.Combine(dir, "frame_000.gif")), Is.True);
      Assert.That(File.Exists(Path.Combine(dir, "frame_001.gif")), Is.True);
      var f0 = File.ReadAllBytes(Path.Combine(dir, "frame_000.gif"));
      Assert.That(f0[..6], Is.EquivalentTo("GIF89a"u8.ToArray()));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }
}
