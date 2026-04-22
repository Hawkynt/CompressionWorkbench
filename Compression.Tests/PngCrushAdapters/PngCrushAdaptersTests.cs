#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;
using FileFormat.PngCrushAdapters;

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
  public void IcoListAndExtract_ProducesPngEntries() {
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
      var pngs = Directory.GetFiles(outDir, "*.png", SearchOption.AllDirectories);
      Assert.That(pngs, Has.Length.EqualTo(entries.Count), "All entries should extract to PNG files");

      // Verify PNG magic on first file
      var bytes = File.ReadAllBytes(pngs[0]);
      Assert.That(bytes[0], Is.EqualTo(0x89));
      Assert.That(bytes[1], Is.EqualTo(0x50));
      Assert.That(bytes[2], Is.EqualTo(0x4E));
      Assert.That(bytes[3], Is.EqualTo(0x47));
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }
}
