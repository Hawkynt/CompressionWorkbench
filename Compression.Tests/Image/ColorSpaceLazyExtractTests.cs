#pragma warning disable CS1591
using FileFormat.Core;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Image;

/// <summary>
/// Verifies that <see cref="MultiImageArchiveHelper.Extract"/> with a non-null
/// <c>files</c> filter only computes/writes the requested colorspace components,
/// skipping the other 28 spaces × ~3 components.
/// </summary>
[TestFixture]
public class ColorSpaceLazyExtractTests {

  private static RawImage Synthetic(int w = 32, int h = 32) {
    var px = new byte[w * h * 4];
    for (var i = 0; i < w * h; i++) {
      px[i * 4] = (byte)(i & 0xFF);
      px[i * 4 + 1] = (byte)((i + 50) & 0xFF);
      px[i * 4 + 2] = (byte)((i + 100) & 0xFF);
      px[i * 4 + 3] = 0xFF;
    }
    return new RawImage { Width = w, Height = h, Format = PixelFormat.Rgba32, PixelData = px };
  }

  private static IReadOnlyList<RawImage> Single(Stream _) => [Synthetic()];

  [Test]
  public void Extract_SingleComponent_OnlyThatFileExists() {
    var dir = Path.Combine(Path.GetTempPath(), "cs_lazy_extract_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream();
      MultiImageArchiveHelper.Extract(ms, dir, ["frame_000_32x32_32bpp/colorspace/YCbCr/Y.png"],
        "frame", Single);

      // Only the requested file should land on disk.
      var folder = Path.Combine(dir, "frame_000_32x32_32bpp", "colorspace", "YCbCr");
      Assert.That(File.Exists(Path.Combine(folder, "Y.png")), Is.True,
        "the requested component must be written");

      // None of the OTHER 84 colorspace components should exist.
      var allWritten = Directory.Exists(dir)
        ? Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories)
        : Array.Empty<string>();
      Assert.That(allWritten, Has.Length.EqualTo(1),
        "exactly one PNG should be on disk (the requested Y.png)");
      Assert.That(allWritten[0].EndsWith("Y.png"), Is.True);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void Extract_OnlyComposite_DoesNotComputeColorspaces() {
    var dir = Path.Combine(Path.GetTempPath(), "cs_lazy_extract_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream();
      MultiImageArchiveHelper.Extract(ms, dir, ["frame_000_32x32_32bpp/frame_000.png"],
        "frame", Single);
      var written = Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories);
      Assert.That(written, Has.Length.EqualTo(1));
      Assert.That(written[0].EndsWith("frame_000.png"), Is.True);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void Extract_EntireRgbSpace_WritesAllThreeChannels() {
    // Filtering by component path (R.png, G.png, B.png) should compute the
    // RGB space ONCE and emit all three planes.
    var dir = Path.Combine(Path.GetTempPath(), "cs_lazy_extract_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream();
      MultiImageArchiveHelper.Extract(ms, dir, [
        "frame_000_32x32_32bpp/colorspace/RGB/R.png",
        "frame_000_32x32_32bpp/colorspace/RGB/G.png",
        "frame_000_32x32_32bpp/colorspace/RGB/B.png",
      ], "frame", Single);

      var rgbDir = Path.Combine(dir, "frame_000_32x32_32bpp", "colorspace", "RGB");
      Assert.That(File.Exists(Path.Combine(rgbDir, "R.png")), Is.True);
      Assert.That(File.Exists(Path.Combine(rgbDir, "G.png")), Is.True);
      Assert.That(File.Exists(Path.Combine(rgbDir, "B.png")), Is.True);

      // No other space materialised.
      var totalPng = Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories);
      Assert.That(totalPng, Has.Length.EqualTo(3));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void Extract_NullFilter_FallsBackToFullExtract() {
    // When files == null the user opted into the full slow path; verify it still works.
    var dir = Path.Combine(Path.GetTempPath(), "cs_lazy_extract_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream();
      MultiImageArchiveHelper.Extract(ms, dir, null, "frame", Single);
      var written = Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories);
      // 1 composite + 1 alpha + 85 colorspace planes = 87 distinct names. On a
      // case-insensitive filesystem (Windows/macOS default) the XyY space's
      // "y.png" and "Y.png" collide and the second write overwrites the first,
      // so we end up with 86 files. Assert the lower bound to stay portable
      // across filesystems.
      Assert.That(written.Length, Is.GreaterThanOrEqualTo(86));
      Assert.That(written.Length, Is.LessThanOrEqualTo(87));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }
}
