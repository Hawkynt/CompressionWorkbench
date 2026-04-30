#pragma warning disable CS1591
using System.Diagnostics;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Jpeg;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.PngCrushAdapters;

/// <summary>
/// Regression tests for the JPEG <c>List()</c> freeze. The descriptor must
/// produce its full entry list from the SOF marker alone — not by running
/// libjpeg's full DCT/IDCT pipeline. These tests pin both the cheap-list
/// guarantee (with a wall-clock bound and a size check on a padded fixture)
/// and that <c>Extract()</c> still produces a valid PNG when components are
/// requested.
/// </summary>
[TestFixture]
public class JpegLazyMetadataTests {

  [SetUp]
  public void Setup() => FormatRegistration.EnsureInitialized();

  // Build an 8×8 RGB JPEG via the sibling encoder (mirrors the existing
  // JpegFormatDescriptorTests fixture-builder).
  private static byte[] BuildTinyJpeg() {
    var pixels = new byte[8 * 8 * 3];
    for (var i = 0; i < pixels.Length; i += 3) {
      pixels[i] = 200; pixels[i + 1] = 100; pixels[i + 2] = 50;
    }
    var img = new RawImage {
      Width = 8, Height = 8, Format = PixelFormat.Rgb24, PixelData = pixels,
    };
    return FormatIO.Encode<JpegFile>(img);
  }

  [Test]
  public void Scanner_ParsesSofFromTinyJpeg() {
    if (FormatRegistry.GetById("Jpeg") == null)
      Assert.Ignore("FileFormat.PngCrushAdapters not loaded — sibling PngCrushCS absent.");
    var jpeg = BuildTinyJpeg();
    var meta = JpegMetadataScanner.ScanBytes(jpeg);
    Assert.Multiple(() => {
      Assert.That(meta.Width, Is.EqualTo(8));
      Assert.That(meta.Height, Is.EqualTo(8));
      // 8 bits/sample × 3 components for the RGB-encoded fixture.
      Assert.That(meta.BitsPerPixel, Is.EqualTo(24));
      // JPEG never carries alpha.
      Assert.That(meta.HasAlpha, Is.False);
    });
  }

  [Test]
  public void Scanner_GracefullyHandlesNonJpegBytes() {
    // Scan something that isn't a JPEG — must not throw and must not hang.
    var notJpeg = new byte[1024];
    var meta = JpegMetadataScanner.ScanBytes(notJpeg);
    // Fallback metadata so the helper still emits stable entry names.
    Assert.That(meta, Is.EqualTo(new FrameMetadata(0, 0, 24, false)));
  }

  [Test]
  public void List_PaddedJpeg_CompletesUnderHundredMs_AndDoesNotDecodePixels() {
    // The legacy code path called JpegReader.FromStream from List(), which
    // forces a full pixel decode. To prove that path is dead, we build a JPEG
    // that's structurally valid (SOI + SOF + EOI happen in the first ~1 KB)
    // BUT followed by 100 MB of padding tail. Listing must be bounded by the
    // SOF scan limit, not by the tail size.
    if (FormatRegistry.GetById("Jpeg") == null)
      Assert.Ignore("FileFormat.PngCrushAdapters not loaded — sibling PngCrushCS absent.");

    var jpeg = BuildTinyJpeg();
    const int padding = 100 * 1024 * 1024; // 100 MB
    var padded = new byte[jpeg.Length + padding];
    Array.Copy(jpeg, padded, jpeg.Length);
    // The padding doesn't have to be valid JPEG; we just need to prove that
    // the SOF scan stopped well before reaching it.

    var desc = new JpegFormatDescriptor();
    using var ms = new MemoryStream(padded);

    var sw = Stopwatch.StartNew();
    var entries = desc.List(ms, null);
    sw.Stop();

    Assert.That(entries.Count, Is.GreaterThan(50),
      "List must still produce composite + colorspace component entries.");
    Assert.That(entries[0].Name, Does.Match(@"^image_000_8x8_24bpp/image_000\.png$"),
      "First entry must be the composite frame named from the SOF metadata.");
    Assert.That(sw.ElapsedMilliseconds, Is.LessThan(100),
      $"List() of a SOF-prefixed 100 MB blob must complete in <100 ms (the SOF scan reads at most ~16 KB). Took {sw.ElapsedMilliseconds} ms.");
  }

  [Test]
  public void Extract_StillDecodesPixelsAndProducesPng() {
    if (FormatRegistry.GetById("Jpeg") == null)
      Assert.Ignore("FileFormat.PngCrushAdapters not loaded — sibling PngCrushCS absent.");

    var bytes = BuildTinyJpeg();
    var desc = new JpegFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_jpeg_lazy_{Guid.NewGuid():N}");
    try {
      using var ms = new MemoryStream(bytes);
      // Request a single colorspace component: the lazy-extract path must
      // still call the underlying decoder (once) and output a real PNG.
      desc.Extract(ms, outDir, null, ["image_000_8x8_24bpp/colorspace/RGB/R.png"]);
      var pngs = Directory.GetFiles(outDir, "*.png", SearchOption.AllDirectories);
      Assert.That(pngs.Length, Is.EqualTo(1),
        $"Filtering to one colorspace component should produce exactly one PNG; got {pngs.Length}.");
      var head = File.ReadAllBytes(pngs[0]);
      Assert.That(head[0], Is.EqualTo(0x89), "Output must be a real PNG.");
      Assert.That(head[1], Is.EqualTo(0x50));
      Assert.That(head[2], Is.EqualTo(0x4E));
      Assert.That(head[3], Is.EqualTo(0x47));
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void FrameSource_CachesFrameAcrossMultipleExtractCalls() {
    // Different from the freeze fix but cheap to check: GetFrame on the same
    // JpegFrameSource instance must reuse the cached decode rather than
    // running libjpeg twice.
    if (FormatRegistry.GetById("Jpeg") == null)
      Assert.Ignore("FileFormat.PngCrushAdapters not loaded — sibling PngCrushCS absent.");

    var bytes = BuildTinyJpeg();
    using var ms = new MemoryStream(bytes);
    var src = new JpegFrameSource(ms);
    var f1 = src.GetFrame(0);
    var f2 = src.GetFrame(0);
    Assert.That(f1, Is.SameAs(f2), "Frame must be cached across calls within one source instance.");
  }
}
