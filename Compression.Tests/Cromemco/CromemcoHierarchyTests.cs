#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Cromemco;

namespace Compression.Tests.Cromemco;

/// <summary>
/// Cromemco RDOS is a CP/M-derived <i>flat-only</i> filesystem — its
/// directory area is one fixed region holding 32-byte entries with no
/// recursion. These tests assert the flat-only contract: when a caller
/// asks for "2 root files + a subdir with 2 files + a deeper subdir with
/// 1 file", every entry surfaces flat (basename only), no entry reports
/// <see cref="ArchiveEntryInfo.IsDirectory"/>, and Extract reproduces
/// every basename at the root of the output directory.
/// </summary>
[TestFixture]
public class CromemcoHierarchyTests {

  private static byte[] BuildImageWithHierarchy() {
    var w = new CromemcoWriter();
    w.SetGeometry(77, 26); // double-density, plenty of room.
    w.AddFile("ROOT1.TXT", "root file 1"u8.ToArray());
    w.AddFile("ROOT2.TXT", "root file 2"u8.ToArray());
    // Cromemco is flat-only: even when callers pass "DIR/CHILD.TXT" the
    // writer stores it as "CHILD.TXT" (basename-only). That's the spec.
    w.AddFile("DIR/CHILD1.TXT", "child 1"u8.ToArray());
    w.AddFile("DIR/CHILD2.TXT", "child 2"u8.ToArray());
    w.AddFile("DIR/SUB/DEEP.TXT", "deep file"u8.ToArray());
    return w.Build();
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsFiveFlatEntries_NoDirectories() {
    var img = BuildImageWithHierarchy();
    var d = new CromemcoFormatDescriptor();
    var entries = d.List(new MemoryStream(img), null);

    Assert.That(entries, Has.Count.EqualTo(5),
      "Cromemco is flat — all 5 input files must appear as 5 entries.");
    Assert.That(entries.All(e => !e.IsDirectory), Is.True,
      "Cromemco RDOS is a flat filesystem: no entry must report IsDirectory.");
  }

  [Test, Category("HappyPath")]
  public void Extract_ReproducesAllFiles_FlatLayout() {
    var img = BuildImageWithHierarchy();
    var d = new CromemcoFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "cromemco_hier_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(new MemoryStream(img), outDir, null, null);

      // Every file must land flat at the root (no subdirectory recursion).
      var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
      Assert.That(files, Has.Length.EqualTo(5),
        "Extract must produce 5 files (flat-only filesystem).");
      Assert.That(files.All(f => Path.GetDirectoryName(f) == outDir), Is.True,
        "Cromemco RDOS is flat: every extracted file must sit at the root of outputDir.");
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Reports_FlatOnly_NoSupportsDirectories() {
    var d = new CromemcoFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.False,
      "Cromemco RDOS is flat-only by spec — must not advertise SupportsDirectories.");
  }
}
