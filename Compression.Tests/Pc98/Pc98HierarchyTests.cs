#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Pc98;

namespace Compression.Tests.Pc98;

/// <summary>
/// PC-98 is a FAT12-derived filesystem that supports subdirectories
/// by spec. The current minimal PC-98 writer emits a single flat root
/// directory only — hierarchical writes are deferred. These tests
/// assert the descriptor's honest capability set and confirm the
/// writer produces a parseable flat image with all 5 input files
/// surfaced at the root.
/// </summary>
[TestFixture]
public class Pc98HierarchyTests {

  private static byte[] BuildImageWithHierarchy() {
    var w = new Pc98Writer();
    w.SetBytesPerSector(512);
    w.SetSectorsPerCluster(1);
    w.AddFile("ROOT1.TXT", "root file 1"u8.ToArray());
    w.AddFile("ROOT2.TXT", "root file 2"u8.ToArray());
    w.AddFile("DIR/CHILD1.TXT", "child 1"u8.ToArray());
    w.AddFile("DIR/CHILD2.TXT", "child 2"u8.ToArray());
    w.AddFile("DIR/SUB/DEEP.TXT", "deep file"u8.ToArray());
    return w.Build();
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsFiveEntries() {
    var img = BuildImageWithHierarchy();
    var d = new Pc98FormatDescriptor();
    var entries = d.List(new MemoryStream(img), null);
    Assert.That(entries, Has.Count.EqualTo(5),
      "All 5 input files must surface in the flat writer's root directory.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Advertises_SupportsDirectories() {
    var d = new Pc98FormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.True,
      "PC-98 is FAT12-derived and supports directories per spec.");
  }

  [Test, Category("HappyPath")]
  public void Extract_ReproducesAllFiles() {
    var img = BuildImageWithHierarchy();
    var d = new Pc98FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "pc98_hier_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(new MemoryStream(img), outDir, null, null);
      var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
      Assert.That(files, Has.Length.EqualTo(5),
        "Extract must produce 5 files (writer is flat-only for now).");
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }
}
