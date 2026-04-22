#pragma warning disable CS1591
using FileFormat.PeResources;
using FileFormat.ResourceDll;

#pragma warning disable CA1416 // System DLLs are Windows-only (test is guarded by File.Exists)

namespace Compression.Tests.PeResources;

[TestFixture]
public class PeResourcesTests {
  // user32.dll is the most resource-rich target on modern Windows: ~200 resources
  // including RT_GROUP_ICON + RT_ICON (the in-box test for icon reassembly),
  // RT_BITMAP (header-wrap test), RT_CURSOR, RT_VERSION, RT_MANIFEST.
  // shell32.dll on recent Windows has most resources moved to shell32.dll.mui.
  private static readonly string[] WellKnownSystemDlls = [
    @"C:\Windows\System32\user32.dll",
    @"C:\Windows\System32\shell32.dll",
    @"C:\Windows\System32\imageres.dll",
    @"C:\Windows\System32\kernel32.dll",
  ];

  private static string? FindSystemDll() {
    foreach (var p in WellKnownSystemDlls)
      if (File.Exists(p)) return p;
    return null;
  }

  [Test]
  public void ListWellKnownSystemDll_ReturnsEntries() {
    var path = FindSystemDll();
    if (path == null) Assert.Ignore("No suitable system DLL found on this platform");

    using var fs = File.OpenRead(path!);
    var desc = new PeResourcesFormatDescriptor();
    var entries = desc.List(fs, null);

    Assert.That(entries, Is.Not.Empty, "A system DLL should have at least one resource");
  }

  [Test]
  public void ExtractShell32Icons_ProducesValidIcoFiles() {
    var path = FindSystemDll();
    if (path == null) Assert.Ignore("No suitable system DLL found on this platform");

    using var fs = File.OpenRead(path!);
    var entries = new PeResourcesFormatDescriptor().List(fs, null);
    var icoEntries = entries.Where(e => e.Name.StartsWith("GROUP_ICON/") && e.Name.EndsWith(".ico")).ToList();
    if (icoEntries.Count == 0) Assert.Ignore($"{path} has no RT_GROUP_ICON entries");

    fs.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_peicons_{Guid.NewGuid():N}");
    try {
      new PeResourcesFormatDescriptor().Extract(fs, outDir, null, null);
      var icoFiles = Directory.GetFiles(outDir, "*.ico", SearchOption.AllDirectories);
      Assert.That(icoFiles, Is.Not.Empty, "At least one .ico should be extracted");

      // Verify the first ICO has the correct magic (reserved=0, type=1).
      var ico = File.ReadAllBytes(icoFiles[0]);
      Assert.That(ico.Length, Is.GreaterThan(22));
      Assert.That(ico[0], Is.EqualTo(0));
      Assert.That(ico[1], Is.EqualTo(0));
      Assert.That(ico[2], Is.EqualTo(1));
      Assert.That(ico[3], Is.EqualTo(0));
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void ExtractToDirectory_WritesRealFiles() {
    var path = FindSystemDll();
    if (path == null) Assert.Ignore("No suitable system DLL found on this platform");

    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_peresources_{Guid.NewGuid():N}");
    try {
      using var fs = File.OpenRead(path!);
      new PeResourcesFormatDescriptor().Extract(fs, outDir, null, null);

      var written = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
      Assert.That(written, Is.Not.Empty);
      Assert.That(written.Any(f => f.EndsWith(".ico") || f.EndsWith(".bmp") || f.EndsWith(".manifest") || f.EndsWith(".bin")),
        Is.True, "Expect at least one recognisable extension");
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void ReadsOurOwnResourceDll_AsGenericPe() {
    // Round-trip: writer emits a PE, PeResources reader sees the RT_RCDATA.
    var w = new ResourceDllWriter();
    w.AddFile("data.bin", [1, 2, 3, 4, 5]);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var entries = new PeResourcesFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Does.StartWith("RCDATA/"));
    Assert.That(entries[0].Name, Does.EndWith(".bin"));
  }
}
