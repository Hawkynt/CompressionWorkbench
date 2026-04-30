#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;
using FileFormat.Core;
using FileFormat.Jpeg;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.PngCrushAdapters;

[TestFixture]
public class JpegFormatDescriptorTests {

  [SetUp]
  public void Setup() => FormatRegistration.EnsureInitialized();

  // Build a tiny 8x8 RGB JPEG via the sibling JpegFile encoder so we don't
  // depend on a fixture file shipped with the repo.
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
  public void Registry_HasJpegDescriptor() {
    var desc = FormatRegistry.GetById("Jpeg");
    if (desc == null)
      Assert.Ignore("FileFormat.PngCrushAdapters not loaded — sibling PngCrushCS absent.");
    Assert.That(desc!.Category, Is.EqualTo(FormatCategory.Image));
    Assert.That(desc.Extensions, Does.Contain(".jpg"));
    Assert.That(desc.Extensions, Does.Contain(".jpeg"));
  }

  [Test]
  public void JpegMagic_RoutesToJpegDescriptor_NotJpegArchive() {
    if (FormatRegistry.GetById("Jpeg") == null)
      Assert.Ignore("FileFormat.PngCrushAdapters not loaded — sibling PngCrushCS absent.");

    var bytes = BuildTinyJpeg();
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_jpeg_route_{Guid.NewGuid():N}.jpg");
    try {
      File.WriteAllBytes(tmp, bytes);
      var format = FormatDetector.Detect(tmp);
      Assert.That(format.ToString(), Is.EqualTo("Jpeg"));
    } finally {
      try { File.Delete(tmp); } catch { /* best effort */ }
    }
  }

  [Test]
  public void JpegArchive_StaysReachableByExplicitId() {
    // The legacy descriptor (APP-marker / EXIF thumbnail) must remain registered
    // so `cwb list --format JpegArchive` keeps working — its empty Extensions
    // and MagicSignatures lists make sure it never auto-resolves.
    var legacy = FormatRegistry.GetById("JpegArchive");
    Assert.That(legacy, Is.Not.Null);
    Assert.That(legacy!.Extensions, Is.Empty);
    Assert.That(legacy.MagicSignatures, Is.Empty);
  }

  [Test]
  public void List_ProducesHierarchicalImageFolder() {
    if (FormatRegistry.GetById("Jpeg") == null)
      Assert.Ignore("FileFormat.PngCrushAdapters not loaded — sibling PngCrushCS absent.");

    var bytes = BuildTinyJpeg();
    var desc = new JpegFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var entries = desc.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.Multiple(() => {
      // Composite image entry inside the per-image folder.
      Assert.That(names, Has.Some.Match(@"^image_000_8x8_24bpp/image_000\.png$"),
        $"Composite image entry missing. Got:\n  {string.Join("\n  ", names.Take(8))}");
      // Colorspace tree under the same folder. Pick a stable known component.
      Assert.That(names, Has.Some.Match(@"^image_000_8x8_24bpp/colorspace/[^/]+/[^/]+\.png$"),
        $"Colorspace plane entries missing. Got {names.Count} total, sample:\n  {string.Join("\n  ", names.Take(5))}");
      // No alpha — Rgb24 has no alpha channel.
      Assert.That(names, Has.None.EndsWith("/Alpha.png"),
        "Alpha entry must be absent for Rgb24 source.");
    });
  }

  [Test]
  public void Extract_WithFilter_OnlyMaterializesMatchingEntries() {
    if (FormatRegistry.GetById("Jpeg") == null)
      Assert.Ignore("FileFormat.PngCrushAdapters not loaded — sibling PngCrushCS absent.");

    var bytes = BuildTinyJpeg();
    var desc = new JpegFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_jpeg_x_{Guid.NewGuid():N}");
    try {
      using var ms = new MemoryStream(bytes);
      // Just request the composite frame.
      desc.Extract(ms, outDir, null, ["image_000_8x8_24bpp/image_000.png"]);
      var pngs = Directory.GetFiles(outDir, "*.png", SearchOption.AllDirectories);
      Assert.That(pngs.Length, Is.EqualTo(1),
        $"Filter should restrict to exactly one composite frame; got {pngs.Length}: " +
        $"{string.Join(", ", pngs)}");
      var head = File.ReadAllBytes(pngs[0]);
      Assert.That(head[0], Is.EqualTo(0x89), "Output must be a real PNG.");
      Assert.That(head[1], Is.EqualTo(0x50));
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }
}
