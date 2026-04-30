#pragma warning disable CS1591
using Compression.Lib;
using FileFormat.Apng;
using FileFormat.Png;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.PngCrushAdapters;

[TestFixture]
public class MultiImageColorSpaceTests {
  [SetUp]
  public void Setup() => FormatRegistration.EnsureInitialized();

  // Build a 2-frame APNG (4x4, RGBA), red frame then green frame.
  private static byte[] BuildTwoFrameApng() {
    static byte[][] SolidScanlines(byte r, byte g, byte b, int w = 4, int h = 4) {
      var rows = new byte[h][];
      for (var y = 0; y < h; y++) {
        var row = new byte[w * 4];
        for (var x = 0; x < w; x++) {
          row[x * 4] = r; row[x * 4 + 1] = g; row[x * 4 + 2] = b; row[x * 4 + 3] = 255;
        }
        rows[y] = row;
      }
      return rows;
    }

    var f0 = new ApngFrame {
      Width = 4, Height = 4, XOffset = 0, YOffset = 0,
      DelayNumerator = 1, DelayDenominator = 10,
      DisposeOp = ApngDisposeOp.None, BlendOp = ApngBlendOp.Source,
      PixelData = SolidScanlines(255, 0, 0),
    };
    var f1 = new ApngFrame {
      Width = 4, Height = 4, XOffset = 0, YOffset = 0,
      DelayNumerator = 1, DelayDenominator = 10,
      DisposeOp = ApngDisposeOp.None, BlendOp = ApngBlendOp.Source,
      PixelData = SolidScanlines(0, 255, 0),
    };
    var apng = new ApngFile {
      Width = 4, Height = 4, BitDepth = 8, ColorType = PngColorType.RGBA,
      NumPlays = 0,
      Frames = new[] { f0, f1 },
    };
    return ApngWriter.ToBytes(apng);
  }

  [Test]
  public void Apng_List_EmitsHierarchicalFrameFolders() {
    var bytes = BuildTwoFrameApng();
    var desc = new ApngFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var entries = desc.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    // Each frame must produce its own folder; composite + alpha (sibling) +
    // colorspace tree all live UNDER that folder. No flat colorspace entries
    // at the top level.
    Assert.Multiple(() => {
      // Composite frame entries inside per-frame folders.
      Assert.That(names, Does.Contain("frame_000_4x4_32bpp/frame_000.png"));
      Assert.That(names, Does.Contain("frame_001_4x4_32bpp/frame_001.png"));

      // Alpha is a SIBLING of the composite frame, NOT inside colorspace/.
      Assert.That(names, Does.Contain("frame_000_4x4_32bpp/Alpha.png"),
        "alpha must be colorspace-agnostic, emitted alongside the composite frame");
      Assert.That(names, Does.Contain("frame_001_4x4_32bpp/Alpha.png"));

      // Alpha must NOT appear inside the colorspace/RGB tree anymore.
      Assert.That(names, Does.Not.Contain("frame_000_4x4_32bpp/colorspace/RGB/A.png"));
      Assert.That(names, Does.Not.Contain("frame_001_4x4_32bpp/colorspace/RGB/A.png"));

      // Colorspace tree nested inside frame 0.
      Assert.That(names, Does.Contain("frame_000_4x4_32bpp/colorspace/RGB/R.png"));
      Assert.That(names, Does.Contain("frame_000_4x4_32bpp/colorspace/YCbCr/Y.png"));
      Assert.That(names, Does.Contain("frame_000_4x4_32bpp/colorspace/HSL/H.png"));
      Assert.That(names, Does.Contain("frame_000_4x4_32bpp/colorspace/CMYK/K.png"));
      Assert.That(names, Does.Contain("frame_000_4x4_32bpp/colorspace/Lab/L.png"));
      Assert.That(names, Does.Contain("frame_000_4x4_32bpp/colorspace/Oklab/L.png"));

      // Independent colorspace tree inside frame 1.
      Assert.That(names, Does.Contain("frame_001_4x4_32bpp/colorspace/RGB/G.png"));
      Assert.That(names, Does.Contain("frame_001_4x4_32bpp/colorspace/YCbCr/Y.png"));
      Assert.That(names, Does.Contain("frame_001_4x4_32bpp/colorspace/HSL/L.png"));
      Assert.That(names, Does.Contain("frame_001_4x4_32bpp/colorspace/Lab/a.png"));
    });

    // Hierarchical contract: NO flat top-level colorspace/alpha entries.
    Assert.That(names.Any(n => n.StartsWith("colorspace/")), Is.False,
      "colorspace tree must live INSIDE per-frame folders, not at the top level");
    Assert.That(names.Any(n => n == "Alpha.png"), Is.False,
      "Alpha must live inside per-frame folders, not flat at the top level");
  }

  [Test]
  public void Apng_Extract_WritesHierarchicalFiles() {
    var bytes = BuildTwoFrameApng();
    var desc = new ApngFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_apng_cs_{Guid.NewGuid():N}");
    try {
      using var ms = new MemoryStream(bytes);
      desc.Extract(ms, outDir, null, null);

      Assert.Multiple(() => {
        Assert.That(File.Exists(Path.Combine(outDir, "frame_000_4x4_32bpp", "frame_000.png")), Is.True);
        Assert.That(File.Exists(Path.Combine(outDir, "frame_000_4x4_32bpp", "colorspace", "RGB", "R.png")), Is.True);
        Assert.That(File.Exists(Path.Combine(outDir, "frame_001_4x4_32bpp", "frame_001.png")), Is.True);
        Assert.That(File.Exists(Path.Combine(outDir, "frame_001_4x4_32bpp", "colorspace", "YCbCr", "Y.png")), Is.True);
        // Alpha sibling, not inside the colorspace tree.
        Assert.That(File.Exists(Path.Combine(outDir, "frame_000_4x4_32bpp", "Alpha.png")), Is.True);
        Assert.That(File.Exists(Path.Combine(outDir, "frame_000_4x4_32bpp", "colorspace", "RGB", "A.png")), Is.False);
      });

      // Frame 0 (pure red) → R.png should be all 255.
      var rPng = File.ReadAllBytes(Path.Combine(outDir, "frame_000_4x4_32bpp", "colorspace", "RGB", "R.png"));
      var raw = PngFile.ToRawImage(PngReader.FromSpan(rPng));
      Assert.That(raw.PixelData, Is.All.EqualTo((byte)255));

      // Alpha must round-trip the source frame's alpha (255 for our opaque test data).
      var aPng = File.ReadAllBytes(Path.Combine(outDir, "frame_000_4x4_32bpp", "Alpha.png"));
      var aRaw = PngFile.ToRawImage(PngReader.FromSpan(aPng));
      Assert.That(aRaw.PixelData, Is.All.EqualTo((byte)255));
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }
}
