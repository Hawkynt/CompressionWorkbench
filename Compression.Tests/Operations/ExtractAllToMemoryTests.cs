#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Validates <see cref="ArchiveOperations.ExtractAllToMemory(string, string?)"/>:
/// per-entry byte extraction straight into <see cref="ArchiveInputInfo.InMemory(string, byte[])"/>
/// inputs with no tempdir in sight. Used by the in-memory ConvertArchive
/// pipeline as its source feed.
/// </summary>
[TestFixture]
public class ExtractAllToMemoryTests {

  private static string MakeTempDir() {
    var p = Path.Combine(Path.GetTempPath(), "cwb_test_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(p);
    return p;
  }

  [Test, Category("InMemoryPipeline")]
  public void ExtractAllToMemory_FatSource_ReturnsAllEntriesAsInMemoryInputs() {
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "src.img");
      var w = new FileSystem.Fat.FatWriter();
      w.AddFile("ALPHA.TXT", "alpha-content"u8.ToArray());
      w.AddFile("BETA.TXT", "beta-content"u8.ToArray());
      File.WriteAllBytes(src, w.BuildAutoSized());

      var inputs = ArchiveOperations.ExtractAllToMemory(src, password: null);

      var files = inputs.Where(i => !i.IsDirectory).ToList();
      Assert.That(files.Count, Is.GreaterThanOrEqualTo(2));
      Assert.That(files.All(i => i.InMemoryContent != null), Is.True,
        "Every non-directory entry must have InMemoryContent populated.");

      var alpha = files.FirstOrDefault(i => i.ArchiveName.Contains("ALPHA", StringComparison.OrdinalIgnoreCase));
      Assert.That(alpha, Is.Not.Null);
      Assert.That(alpha!.InMemoryContent, Is.EqualTo("alpha-content"u8.ToArray()).AsCollection);
    } finally {
      try { Directory.Delete(dir, true); } catch { }
    }
  }

  [Test, Category("InMemoryPipeline")]
  public void ExtractAllToMemory_ZipSource_ReturnsAllEntriesAsInMemoryInputs() {
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "src.zip");
      using (var fs = File.Create(src)) {
        var w = new FileFormat.Zip.ZipWriter(fs, leaveOpen: true);
        w.AddEntry("one.txt", "one-data"u8.ToArray());
        w.AddEntry("two.txt", "two-data"u8.ToArray());
        w.Finish();
      }

      var inputs = ArchiveOperations.ExtractAllToMemory(src, password: null);

      var files = inputs.Where(i => !i.IsDirectory).ToList();
      Assert.That(files.Count, Is.EqualTo(2));
      Assert.That(files.All(i => i.InMemoryContent != null), Is.True);

      var one = files.First(i => i.ArchiveName == "one.txt");
      var two = files.First(i => i.ArchiveName == "two.txt");
      Assert.That(one.InMemoryContent, Is.EqualTo("one-data"u8.ToArray()).AsCollection);
      Assert.That(two.InMemoryContent, Is.EqualTo("two-data"u8.ToArray()).AsCollection);
    } finally {
      try { Directory.Delete(dir, true); } catch { }
    }
  }

  [Test, Category("InMemoryPipeline")]
  public void ExtractAllToMemory_PreservesDirectoryEntries() {
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "src.zip");
      using (var fs = File.Create(src)) {
        var w = new FileFormat.Zip.ZipWriter(fs, leaveOpen: true);
        w.AddDirectory("sub/");
        w.AddEntry("sub/file.txt", "nested"u8.ToArray());
        w.Finish();
      }

      var inputs = ArchiveOperations.ExtractAllToMemory(src, password: null);

      var dirs = inputs.Where(i => i.IsDirectory).ToList();
      Assert.That(dirs.Count, Is.EqualTo(1));
      Assert.That(dirs[0].ArchiveName, Is.EqualTo("sub/"));
    } finally {
      try { Directory.Delete(dir, true); } catch { }
    }
  }
}
