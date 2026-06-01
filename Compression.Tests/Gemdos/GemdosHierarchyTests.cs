#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Gemdos;

namespace Compression.Tests.Gemdos;

/// <summary>
/// Hierarchy + flat round-trip tests for GEMDOS. GEMDOS supports subdirectories
/// (standard FAT12 0x10 dirent flag) so we build a deep tree and verify both
/// listing and extraction preserve the structure.
/// </summary>
[TestFixture]
public class GemdosHierarchyTests {

  private static byte[] BuildHierarchyImage() {
    var w = new GemdosWriter();
    w.AddFile("README.TXT", Encoding.ASCII.GetBytes("Atari ST root file"));
    w.AddFile("NOTES.TXT", Encoding.ASCII.GetBytes("Another root file"));
    w.AddFile("DOCS/INFO.TXT", Encoding.ASCII.GetBytes("Subdir file A"));
    w.AddFile("DOCS/MORE.TXT", Encoding.ASCII.GetBytes("Subdir file B"));
    w.AddFile("DOCS/DEEP/LEAF.TXT", Encoding.ASCII.GetBytes("Deepest file"));
    return w.Build();
  }

  [Test]
  public void List_ReturnsAllFiveFilesWithSubdirPrefixedNames() {
    var d = new GemdosFormatDescriptor();
    using var ms = new MemoryStream(BuildHierarchyImage());
    var entries = d.List(ms, null);
    var fileNames = entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(fileNames, Does.Contain("README.TXT"));
    Assert.That(fileNames, Does.Contain("NOTES.TXT"));
    Assert.That(fileNames, Does.Contain("DOCS/INFO.TXT"));
    Assert.That(fileNames, Does.Contain("DOCS/MORE.TXT"));
    Assert.That(fileNames, Does.Contain("DOCS/DEEP/LEAF.TXT"));
  }

  [Test]
  public void Extract_ReproducesDirectoryTreeOnDisk() {
    var d = new GemdosFormatDescriptor();
    using var ms = new MemoryStream(BuildHierarchyImage());
    var tmp = Path.Combine(Path.GetTempPath(), $"gemdos-hier-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      d.Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "README.TXT")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "DOCS", "INFO.TXT")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "DOCS", "DEEP", "LEAF.TXT")), Is.True);
      Assert.That(Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(tmp, "DOCS", "INFO.TXT"))),
                  Is.EqualTo("Subdir file A"));
      Assert.That(Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(tmp, "DOCS", "DEEP", "LEAF.TXT"))),
                  Is.EqualTo("Deepest file"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test]
  public void JumpByteIsAtari_NotMsDos() {
    var img = BuildHierarchyImage();
    Assert.That(img[0], Is.EqualTo(GemdosBpb.GemdosJump),
                "GEMDOS jump byte must be 0x60 (m68k BRA.S), not 0xEB.");
  }
}
