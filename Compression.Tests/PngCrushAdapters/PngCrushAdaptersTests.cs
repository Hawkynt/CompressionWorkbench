#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;
using CompressionWorkbench.FileFormat.Ico;

namespace Compression.Tests.PngCrushAdapters;

[TestFixture]
public class PngCrushAdaptersTests {
  [SetUp]
  public void Setup() => FormatRegistration.EnsureInitialized();

  [Test]
  public void FormatEnumHasIco() {
    var values = Enum.GetNames(typeof(FormatDetector.Format)).ToList();
    Assert.That(values, Does.Contain("Ico"), $"Format enum values: {string.Join(", ", values.OrderBy(x => x))}");
  }

  [Test]
  public void RegistryHasIcoDescriptor() {
    var ids = FormatRegistry.All.Select(d => d.Id).ToList();
    Assert.That(ids, Does.Contain("Ico"), $"Registered ids: {string.Join(", ", ids.OrderBy(x => x))}");
  }

  [Test]
  public void RegistryHasAllAdapterDescriptors() {
    var ids = FormatRegistry.All.Select(d => d.Id).ToHashSet();

    // Apng/Tiff/BigTiff/Mng/Fli/Dcx/Icns/Mpo come from the cross-repo
    // FileFormat.PngCrushAdapters project, which is conditional on the sibling
    // PngCrushCS repo being present at ../../PNGCrushCS. On CI runners where
    // only this repo is cloned, those descriptors are intentionally absent
    // and the project compiles to an empty assembly. Skip cleanly there —
    // Ico/Cur/Ani live in our own native projects and are always present.
    if (!ids.Contains("Apng"))
      Assert.Ignore("Cross-repo PngCrushAdapters not loaded — sibling PngCrushCS repo " +
                    "absent at ../../PNGCrushCS. Ico/Cur/Ani still verified by " +
                    "RegistryHasIcoDescriptor + adjacent tests.");

    foreach (var expected in new[] { "Ico", "Cur", "Ani", "Apng", "Tiff", "BigTiff", "Mng", "Fli", "Dcx", "Icns", "Mpo" })
      Assert.That(ids, Does.Contain(expected), $"Missing: {expected}");
  }

  [Test]
  public void IcoExtension_Routes_ToIcoAdapter() {
    if (!File.Exists(@"C:\Windows\System32\OneDrive.ico"))
      Assert.Ignore("Test ICO not present on this system");

    var format = FormatDetector.Detect(@"C:\Windows\System32\OneDrive.ico");
    Assert.That(format.ToString(), Is.EqualTo("Ico"));
  }

  [Test]
  public void IcoListAndExtract_ProducesImageEntries() {
    var src = @"C:\Windows\System32\OneDrive.ico";
    if (!File.Exists(src)) Assert.Ignore("Test ICO not present on this system");

    using var fs = File.OpenRead(src);
    var desc = new IcoFormatDescriptor();
    var entries = desc.List(fs, null);
    Assert.That(entries, Is.Not.Empty);

    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_ico_{Guid.NewGuid():N}");
    try {
      fs.Position = 0;
      desc.Extract(fs, outDir, null, null);
      // Native ICO reader preserves the on-disk encoding: PNG entries become .png,
      // DIB entries become .bmp (with reconstructed BITMAPFILEHEADER).
      var images = Directory.GetFiles(outDir, "*.png", SearchOption.AllDirectories)
        .Concat(Directory.GetFiles(outDir, "*.bmp", SearchOption.AllDirectories))
        .ToArray();
      Assert.That(images, Has.Length.EqualTo(entries.Count), "All entries should extract to PNG or BMP files");

      // Verify the first extracted file has either a PNG or BMP magic.
      var bytes = File.ReadAllBytes(images[0]);
      var isPng = bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
      var isBmp = bytes[0] == (byte)'B' && bytes[1] == (byte)'M';
      Assert.That(isPng || isBmp, Is.True, $"First file magic: {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}");
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }
}
