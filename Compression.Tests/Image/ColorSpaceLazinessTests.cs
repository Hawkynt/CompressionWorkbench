#pragma warning disable CS1591
using System.Diagnostics;
using FileFormat.Core;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Image;

/// <summary>
/// Verifies the lazy-listing contract on <see cref="MultiImageArchiveHelper.List"/>:
/// listing a multi-megapixel image returns metadata-only entries with mathematical
/// size estimates, without invoking the per-pixel colorspace projectors.
/// </summary>
[TestFixture]
public class ColorSpaceLazinessTests {

  // 1024×1024 synthetic Rgba32 frame (~4 MB) — proxy for a real multi-MP JPEG.
  private static RawImage BigSynthetic(int w = 1024, int h = 1024) {
    var px = new byte[w * h * 4];
    // Fill with a deterministic pattern so the projector would have actual work to do.
    for (var i = 0; i < w * h; i++) {
      px[i * 4] = (byte)(i & 0xFF);
      px[i * 4 + 1] = (byte)((i >> 1) & 0xFF);
      px[i * 4 + 2] = (byte)((i >> 2) & 0xFF);
      px[i * 4 + 3] = 0xFF;
    }
    return new RawImage { Width = w, Height = h, Format = PixelFormat.Rgba32, PixelData = px };
  }

  private static IReadOnlyList<RawImage> Single(Stream _) => [BigSynthetic()];

  [Test]
  public void List_OnLargeFrame_CompletesWellUnderOneSecond() {
    // The lazy path must NOT iterate W*H pixels — confirm by wall-clock budget.
    using var ms = new MemoryStream();
    var sw = Stopwatch.StartNew();
    var entries = MultiImageArchiveHelper.List(ms, "frame", Single);
    sw.Stop();

    Assert.That(entries, Is.Not.Empty, "list must enumerate frame folders");
    // 1 second is a very loose bound; an eager 1MP × 29 colorspace pass
    // takes multiple seconds even on a fast machine. Lazy = milliseconds.
    Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1000),
      $"lazy List() must be O(catalog), not O(W*H*spaces) — took {sw.ElapsedMilliseconds} ms");
  }

  [Test]
  public void List_EmitsAll85ColorspaceComponentsPerFrame_PlusCompositeAndAlpha() {
    using var ms = new MemoryStream();
    var entries = MultiImageArchiveHelper.List(ms, "frame", Single);

    // 1 composite + 1 alpha (RGBA32 has alpha) + 85 colorspace components = 87
    Assert.That(entries, Has.Count.EqualTo(87));
    Assert.That(entries.Any(e => e.Name.EndsWith("/frame_000.png")), Is.True);
    Assert.That(entries.Any(e => e.Name.EndsWith("/Alpha.png")), Is.True);
    Assert.That(entries.Count(e => e.Name.Contains("/colorspace/")), Is.EqualTo(85));
  }

  [Test]
  public void List_EntrySizeMatchesMathematicalEstimate() {
    using var ms = new MemoryStream();
    var entries = MultiImageArchiveHelper.List(ms, "frame", Single);

    // EstimatePngBytes(1024, 1024) = 1024*1024 + 1024 = 1,049,600.
    var expected = ColorSpaceCatalog.EstimatePngBytes(1024, 1024);
    foreach (var e in entries) {
      Assert.That(e.OriginalSize, Is.EqualTo(expected),
        $"entry {e.Name} should report mathematical estimate, not actual byte count");
      Assert.That(e.CompressedSize, Is.EqualTo(expected));
    }
  }

  [Test]
  public void List_OnSmallFrame_ProducesFastEnumerationVsExtractAll() {
    // Sanity: List should be at least an order of magnitude faster than the
    // full extract on the same image. If a future regression turns List back
    // into eager compute the gap collapses.
    var dir = Path.Combine(Path.GetTempPath(), "cs_lazy_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream();

      var swList = Stopwatch.StartNew();
      _ = MultiImageArchiveHelper.List(ms, "frame", Single);
      swList.Stop();

      var swExtract = Stopwatch.StartNew();
      MultiImageArchiveHelper.Extract(ms, dir, null, "frame", Single);
      swExtract.Stop();

      // Lazy should be markedly cheaper. Exact ratio is platform-dependent;
      // we only assert >2× to keep the test stable across CI/dev hardware.
      Assert.That(swList.ElapsedMilliseconds * 2, Is.LessThan(Math.Max(swExtract.ElapsedMilliseconds, 1)),
        $"List ({swList.ElapsedMilliseconds} ms) should be much faster than Extract ({swExtract.ElapsedMilliseconds} ms)");
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }
}
