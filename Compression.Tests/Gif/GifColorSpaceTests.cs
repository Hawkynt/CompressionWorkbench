#pragma warning disable CS1591
using FileFormat.Gif;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Gif;

/// <summary>
/// Integration tests that confirm a GIF goes through <see cref="MultiImageArchiveHelper"/>
/// and exposes per-frame folders with the full colorspace tree (Issue 2 in Phase 35).
/// </summary>
[TestFixture]
public class GifColorSpaceTests {

  // 2-frame 2x1 GIF89a with palette {red, green}. Each frame's LZW stream
  // encodes indices [0, 1] as [clear=4, 0, 1, eoi=5] — valid 3-bit LSB-first code stream
  // packing to bytes 0x44 0x02. (See appendix F of the GIF89a spec.)
  private static byte[] MakeTwoFrameGif() {
    var ms = new MemoryStream();
    var bw = new BinaryWriter(ms);
    bw.Write("GIF89a"u8.ToArray());
    bw.Write((ushort)2); bw.Write((ushort)1);                 // logical screen 2x1
    bw.Write((byte)0x80); bw.Write((byte)0); bw.Write((byte)0); // global CT, size=0 => 2 entries
    bw.Write(new byte[] { 0xFF, 0x00, 0x00 });                // palette[0] = red
    bw.Write(new byte[] { 0x00, 0xFF, 0x00 });                // palette[1] = green

    WriteFrame(bw);
    WriteFrame(bw);

    bw.Write((byte)0x3B); // trailer
    return ms.ToArray();
  }

  private static void WriteFrame(BinaryWriter bw) {
    bw.Write((byte)0x2C);
    bw.Write((ushort)0); bw.Write((ushort)0);                 // left, top
    bw.Write((ushort)2); bw.Write((ushort)1);                 // w, h
    bw.Write((byte)0x00);                                     // packed (no local CT, no interlace)
    bw.Write((byte)2);                                        // lzwMin
    bw.Write((byte)2); bw.Write(new byte[] { 0x44, 0x02 });   // valid LZW: [clear,0,1,eoi]
    bw.Write((byte)0);                                        // sub-block terminator
  }

  [Test]
  public void Decoder_ReturnsRgba32Frames() {
    var data = MakeTwoFrameGif();
    var frames = new GifPixelDecoder().Decode(data);
    Assert.That(frames, Has.Count.EqualTo(2));
    Assert.That(frames[0].Width, Is.EqualTo(2));
    Assert.That(frames[0].Height, Is.EqualTo(1));
    Assert.That(frames[0].Rgba32, Has.Length.EqualTo(8));
    // Pixel 0 = red (palette index 0), Pixel 1 = green (palette index 1).
    Assert.That(frames[0].Rgba32[0], Is.EqualTo((byte)0xFF));
    Assert.That(frames[0].Rgba32[1], Is.EqualTo((byte)0x00));
    Assert.That(frames[0].Rgba32[2], Is.EqualTo((byte)0x00));
    Assert.That(frames[0].Rgba32[4], Is.EqualTo((byte)0x00));
    Assert.That(frames[0].Rgba32[5], Is.EqualTo((byte)0xFF));
  }

  [Test]
  public void DescriptorList_HasFrameFoldersAndColorspaceTree() {
    var data = MakeTwoFrameGif();
    var desc = new GifFormatDescriptor();
    using var ms = new MemoryStream(data);
    var entries = desc.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    // Per-frame folder + composite + alpha + 85 colorspace planes for each of 2 frames.
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/frame_000.png"));
    Assert.That(names, Does.Contain("frame_001_2x1_32bpp/frame_001.png"));
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/Alpha.png"));
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/colorspace/RGB/R.png"));
    Assert.That(names, Does.Contain("frame_000_2x1_32bpp/colorspace/Lab/L.png"));
    Assert.That(names, Does.Contain("frame_001_2x1_32bpp/colorspace/YCbCr/Y.png"));

    // Each frame: 1 composite + 1 alpha + 85 colorspace = 87. Two frames = 174.
    Assert.That(entries, Has.Count.EqualTo(174));
  }

  [Test]
  public void DescriptorExtract_FrameAndColorspaceComponent_AreByteValid() {
    var data = MakeTwoFrameGif();
    var dir = Path.Combine(Path.GetTempPath(), "gif_cs_" + Guid.NewGuid().ToString("N"));
    try {
      using (var ms = new MemoryStream(data)) {
        new GifFormatDescriptor().Extract(ms, dir, null, [
          "frame_000_2x1_32bpp/frame_000.png",
          "frame_000_2x1_32bpp/colorspace/RGB/R.png",
        ]);
      }

      var compositePath = Path.Combine(dir, "frame_000_2x1_32bpp", "frame_000.png");
      var rPath = Path.Combine(dir, "frame_000_2x1_32bpp", "colorspace", "RGB", "R.png");
      Assert.That(File.Exists(compositePath), Is.True);
      Assert.That(File.Exists(rPath), Is.True);

      // Both files must start with the PNG signature.
      var sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
      Assert.That(File.ReadAllBytes(compositePath)[..8], Is.EquivalentTo(sig));
      Assert.That(File.ReadAllBytes(rPath)[..8], Is.EquivalentTo(sig));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }
}
