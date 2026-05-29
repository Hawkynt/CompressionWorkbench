#pragma warning disable CS1591
using Compression.Lib;

namespace Compression.Tests.Dedup;

[TestFixture]
public class DeduplicationTests {

  [Test]
  public void Analyze_ZipWithDuplicates_FindsDuplicates() {
    // Create a ZIP with two identical files
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_dedup_test_" + Guid.NewGuid().ToString("N")[..8]);
    var zipPath = Path.Combine(Path.GetTempPath(), "cwb_dedup_test_" + Guid.NewGuid().ToString("N")[..8] + ".zip");
    try {
      Directory.CreateDirectory(tempDir);
      var content = new byte[256];
      new Random(42).NextBytes(content);
      File.WriteAllBytes(Path.Combine(tempDir, "file1.bin"), content);
      File.WriteAllBytes(Path.Combine(tempDir, "file2.bin"), content); // duplicate
      File.WriteAllBytes(Path.Combine(tempDir, "file3.bin"), [1, 2, 3]); // unique

      var inputs = new List<ArchiveInput> {
        new(Path.Combine(tempDir, "file1.bin"), "file1.bin"),
        new(Path.Combine(tempDir, "file2.bin"), "file2.bin"),
        new(Path.Combine(tempDir, "file3.bin"), "file3.bin"),
      };
      ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

      var report = DeduplicationScanner.Analyze(zipPath);

      Assert.That(report.TotalFiles, Is.EqualTo(3));
      Assert.That(report.DuplicateFiles, Is.EqualTo(1));
      Assert.That(report.UniqueFiles, Is.EqualTo(2));
      Assert.That(report.Groups, Has.Count.EqualTo(1));
      Assert.That(report.Groups[0].FileNames, Has.Count.EqualTo(2));
      Assert.That(report.Groups[0].Size, Is.EqualTo(256));
      Assert.That(report.WastedBytes, Is.EqualTo(256));
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
      if (File.Exists(zipPath)) File.Delete(zipPath);
    }
  }

  [Test]
  public void Analyze_ZipNoDuplicates_ReportsNone() {
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_dedup_nodup_" + Guid.NewGuid().ToString("N")[..8]);
    var zipPath = Path.Combine(Path.GetTempPath(), "cwb_dedup_nodup_" + Guid.NewGuid().ToString("N")[..8] + ".zip");
    try {
      Directory.CreateDirectory(tempDir);
      File.WriteAllBytes(Path.Combine(tempDir, "a.bin"), [1, 2, 3]);
      File.WriteAllBytes(Path.Combine(tempDir, "b.bin"), [4, 5, 6]);

      var inputs = new List<ArchiveInput> {
        new(Path.Combine(tempDir, "a.bin"), "a.bin"),
        new(Path.Combine(tempDir, "b.bin"), "b.bin"),
      };
      ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

      var report = DeduplicationScanner.Analyze(zipPath);

      Assert.That(report.DuplicateFiles, Is.EqualTo(0));
      Assert.That(report.Groups, Is.Empty);
      Assert.That(report.WastedBytes, Is.EqualTo(0));
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
      if (File.Exists(zipPath)) File.Delete(zipPath);
    }
  }

  [Test]
  public void Execute_ZipWithDuplicates_RemovesDuplicates() {
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_dedup_exec_" + Guid.NewGuid().ToString("N")[..8]);
    var zipPath = Path.Combine(Path.GetTempPath(), "cwb_dedup_exec_" + Guid.NewGuid().ToString("N")[..8] + ".zip");
    try {
      Directory.CreateDirectory(tempDir);
      var content = new byte[512];
      new Random(42).NextBytes(content);
      File.WriteAllBytes(Path.Combine(tempDir, "file1.bin"), content);
      File.WriteAllBytes(Path.Combine(tempDir, "file2.bin"), content); // duplicate
      File.WriteAllBytes(Path.Combine(tempDir, "unique.bin"), [10, 20, 30]);

      var inputs = new List<ArchiveInput> {
        new(Path.Combine(tempDir, "file1.bin"), "file1.bin"),
        new(Path.Combine(tempDir, "file2.bin"), "file2.bin"),
        new(Path.Combine(tempDir, "unique.bin"), "unique.bin"),
      };
      ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

      var saved = DeduplicationScanner.Execute(zipPath, DeduplicationStrategy.KeepFirst);
      Assert.That(saved, Is.GreaterThan(0));

      // Verify the archive now has only 2 files
      var entries = ArchiveOperations.List(zipPath, password: null);
      Assert.That(entries.Count, Is.EqualTo(2));

      // file1.bin should be kept (first occurrence)
      Assert.That(entries.Any(e => e.Name == "file1.bin"), Is.True);
      Assert.That(entries.Any(e => e.Name == "unique.bin"), Is.True);
      Assert.That(entries.Any(e => e.Name == "file2.bin"), Is.False);
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
      if (File.Exists(zipPath)) File.Delete(zipPath);
    }
  }

  [Test]
  public void Execute_KeepLargestPath_KeepsShallowest() {
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_dedup_shallow_" + Guid.NewGuid().ToString("N")[..8]);
    var zipPath = Path.Combine(Path.GetTempPath(), "cwb_dedup_shallow_" + Guid.NewGuid().ToString("N")[..8] + ".zip");
    try {
      Directory.CreateDirectory(tempDir);
      var subDir = Path.Combine(tempDir, "sub");
      Directory.CreateDirectory(subDir);

      var content = new byte[128];
      new Random(99).NextBytes(content);
      File.WriteAllBytes(Path.Combine(subDir, "deep.bin"), content);
      File.WriteAllBytes(Path.Combine(tempDir, "shallow.bin"), content); // duplicate, shallower

      var inputs = new List<ArchiveInput> {
        new(Path.Combine(subDir, "deep.bin"), "sub/deep.bin"),
        new(Path.Combine(tempDir, "shallow.bin"), "shallow.bin"),
      };
      ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

      DeduplicationScanner.Execute(zipPath, DeduplicationStrategy.KeepLargestPath);

      var entries = ArchiveOperations.List(zipPath, password: null);
      var files = entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(files.Count, Is.EqualTo(1));
      Assert.That(files[0].Name, Is.EqualTo("shallow.bin"));
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
      if (File.Exists(zipPath)) File.Delete(zipPath);
    }
  }

  [Test]
  public void FindDuplicates_EmptyArchive_ReturnsEmpty() {
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_dedup_empty_" + Guid.NewGuid().ToString("N")[..8]);
    var zipPath = Path.Combine(Path.GetTempPath(), "cwb_dedup_empty_" + Guid.NewGuid().ToString("N")[..8] + ".zip");
    try {
      Directory.CreateDirectory(tempDir);
      // Create a ZIP with just a directory entry
      File.WriteAllBytes(Path.Combine(tempDir, "only.bin"), [42]);

      var inputs = new List<ArchiveInput> {
        new(Path.Combine(tempDir, "only.bin"), "only.bin"),
      };
      ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

      var groups = DeduplicationScanner.FindDuplicates(zipPath);
      Assert.That(groups, Is.Empty);
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
      if (File.Exists(zipPath)) File.Delete(zipPath);
    }
  }

  [Test]
  public void Execute_PreservesAllUniqueFiles() {
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_dedup_preserve_" + Guid.NewGuid().ToString("N")[..8]);
    var zipPath = Path.Combine(Path.GetTempPath(), "cwb_dedup_preserve_" + Guid.NewGuid().ToString("N")[..8] + ".zip");
    try {
      Directory.CreateDirectory(tempDir);
      var dup = new byte[64];
      new Random(7).NextBytes(dup);

      File.WriteAllBytes(Path.Combine(tempDir, "a.bin"), dup);
      File.WriteAllBytes(Path.Combine(tempDir, "b.bin"), dup);
      File.WriteAllBytes(Path.Combine(tempDir, "c.bin"), dup);
      File.WriteAllBytes(Path.Combine(tempDir, "unique1.bin"), [1, 2, 3]);
      File.WriteAllBytes(Path.Combine(tempDir, "unique2.bin"), [4, 5, 6, 7]);

      var inputs = new List<ArchiveInput> {
        new(Path.Combine(tempDir, "a.bin"), "a.bin"),
        new(Path.Combine(tempDir, "b.bin"), "b.bin"),
        new(Path.Combine(tempDir, "c.bin"), "c.bin"),
        new(Path.Combine(tempDir, "unique1.bin"), "unique1.bin"),
        new(Path.Combine(tempDir, "unique2.bin"), "unique2.bin"),
      };
      ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

      DeduplicationScanner.Execute(zipPath, DeduplicationStrategy.KeepFirst);

      var entries = ArchiveOperations.List(zipPath, password: null);
      // 1 kept from the triple + 2 unique = 3 total
      Assert.That(entries.Count, Is.EqualTo(3));
      Assert.That(entries.Any(e => e.Name == "a.bin"), Is.True, "Kept file (first) should remain");
      Assert.That(entries.Any(e => e.Name == "unique1.bin"), Is.True, "Unique file should remain");
      Assert.That(entries.Any(e => e.Name == "unique2.bin"), Is.True, "Unique file should remain");
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
      if (File.Exists(zipPath)) File.Delete(zipPath);
    }
  }
}
