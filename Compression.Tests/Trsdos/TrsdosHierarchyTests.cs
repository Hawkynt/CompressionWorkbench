#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Trsdos;

namespace Compression.Tests.Trsdos;

/// <summary>
/// TRSDOS / LDOS is a <i>flat-only</i> filesystem — every file lives in
/// the single directory area on track 17 with no subdirectory support.
/// These tests assert the flat-only contract: 5 input files surface as
/// 5 flat entries (no <see cref="ArchiveEntryInfo.IsDirectory"/>), and
/// Extract reproduces every basename at the root of the output dir.
/// </summary>
[TestFixture]
public class TrsdosHierarchyTests {

  private static byte[] BuildImageWithHierarchy() {
    var w = new TrsdosWriter();
    w.SetGeometry(40, 18);
    w.AddFile("ROOT1.TXT", "root file 1"u8.ToArray());
    w.AddFile("ROOT2.TXT", "root file 2"u8.ToArray());
    // TRSDOS is flat-only: writer normalises "DIR/CHILD.TXT" to basename only.
    w.AddFile("DIR/CHILD1.TXT", "child 1"u8.ToArray());
    w.AddFile("DIR/CHILD2.TXT", "child 2"u8.ToArray());
    w.AddFile("DIR/SUB/DEEP.TXT", "deep file"u8.ToArray());
    return w.Build();
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsFiveFlatEntries_NoDirectories() {
    var img = BuildImageWithHierarchy();
    var d = new TrsdosFormatDescriptor();
    var entries = d.List(new MemoryStream(img), null);

    Assert.That(entries, Has.Count.EqualTo(5),
      "TRSDOS is flat — all 5 input files must appear as 5 entries.");
    Assert.That(entries.All(e => !e.IsDirectory), Is.True,
      "TRSDOS is a flat filesystem: no entry must report IsDirectory.");
  }

  [Test, Category("HappyPath")]
  public void Extract_ReproducesAllFiles_FlatLayout() {
    var img = BuildImageWithHierarchy();
    var d = new TrsdosFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "trsdos_hier_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(new MemoryStream(img), outDir, null, null);
      var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
      Assert.That(files, Has.Length.EqualTo(5),
        "Extract must produce 5 files (flat-only filesystem).");
      Assert.That(files.All(f => Path.GetDirectoryName(f) == outDir), Is.True,
        "TRSDOS is flat: every extracted file must sit at the root of outputDir.");
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Reports_FlatOnly_NoSupportsDirectories() {
    var d = new TrsdosFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.False,
      "TRSDOS is flat-only by spec — must not advertise SupportsDirectories.");
  }
}
