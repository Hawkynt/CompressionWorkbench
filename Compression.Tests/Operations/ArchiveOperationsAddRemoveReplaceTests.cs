#pragma warning disable CS1591
using Compression.Lib;

namespace Compression.Tests.Operations;

[TestFixture]
public class ArchiveOperationsAddRemoveReplaceTests {

  // ── Modifier path: D64 (IArchiveModifiable) ────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_ModifierPath_D64_AddsEntry() {
    var dir = MakeTempDir();
    try {
      var diskPath = Path.Combine(dir, "disk.d64");
      using (var fs = File.Create(diskPath))
        fs.Write(new FileSystem.D64.D64Writer().Build());

      var src = Path.Combine(dir, "hello.prg");
      File.WriteAllText(src, "hello-d64");

      ArchiveOperations.Add(diskPath, [new ArchiveInput(src, "hello.prg")]);

      var entries = ArchiveOperations.List(diskPath, password: null);
      Assert.That(entries.Any(e => e.Name.Contains("hello", StringComparison.OrdinalIgnoreCase)), Is.True);
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("RoundTrip")]
  public void Remove_ModifierPath_D64_RemovesEntry() {
    var dir = MakeTempDir();
    try {
      var diskPath = Path.Combine(dir, "disk.d64");
      using (var fs = File.Open(diskPath, FileMode.Create, FileAccess.ReadWrite)) {
        fs.Write(new FileSystem.D64.D64Writer().Build());
        FileSystem.D64.D64Modifier.AddFile(fs, "TARGET", "delete-me"u8.ToArray());
      }

      ArchiveOperations.Remove(diskPath, ["TARGET"]);

      var entries = ArchiveOperations.List(diskPath, password: null);
      Assert.That(entries.Any(e => e.Name.Equals("TARGET", StringComparison.OrdinalIgnoreCase)), Is.False);
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("RoundTrip")]
  public void Replace_ModifierPath_D64_SwapsContent() {
    var dir = MakeTempDir();
    try {
      var diskPath = Path.Combine(dir, "disk.d64");
      using (var fs = File.Open(diskPath, FileMode.Create, FileAccess.ReadWrite)) {
        fs.Write(new FileSystem.D64.D64Writer().Build());
        FileSystem.D64.D64Modifier.AddFile(fs, "DOC", "version-one"u8.ToArray());
      }

      var newSrc = Path.Combine(dir, "new.bin");
      File.WriteAllText(newSrc, "version-two-now");

      ArchiveOperations.Replace(diskPath, "DOC", newSrc);

      using var rfs = File.OpenRead(diskPath);
      var reader = new FileSystem.D64.D64Reader(rfs);
      var entry = reader.Entries.Single(e => e.Name.Equals("DOC", StringComparison.OrdinalIgnoreCase));
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("version-two-now"));
    } finally { Directory.Delete(dir, true); }
  }

  // ── Rebuild path: ZIP (no IArchiveModifiable) ──────────────────────

  [Test, Category("RoundTrip")]
  public void Add_RebuildPath_Zip_AddsEntry() {
    var dir = MakeTempDir();
    try {
      var srcA = Path.Combine(dir, "alpha.txt");
      var srcB = Path.Combine(dir, "bravo.txt");
      File.WriteAllText(srcA, "alpha-content");
      File.WriteAllText(srcB, "bravo-content");

      var zipPath = Path.Combine(dir, "test.zip");
      ArchiveOperations.Create(zipPath, [new ArchiveInput(srcA, "alpha.txt")], new CompressionOptions());

      ArchiveOperations.Add(zipPath, [new ArchiveInput(srcB, "bravo.txt")], new CompressionOptions());

      var names = ArchiveOperations.List(zipPath, password: null).Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("alpha.txt"));
      Assert.That(names, Does.Contain("bravo.txt"));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("RoundTrip")]
  public void Remove_RebuildPath_Zip_RemovesEntry() {
    var dir = MakeTempDir();
    try {
      var srcA = Path.Combine(dir, "alpha.txt");
      var srcB = Path.Combine(dir, "bravo.txt");
      File.WriteAllText(srcA, "alpha-content");
      File.WriteAllText(srcB, "bravo-content");

      var zipPath = Path.Combine(dir, "test.zip");
      ArchiveOperations.Create(zipPath,
        [new ArchiveInput(srcA, "alpha.txt"), new ArchiveInput(srcB, "bravo.txt")],
        new CompressionOptions());

      ArchiveOperations.Remove(zipPath, ["alpha.txt"], new CompressionOptions());

      var names = ArchiveOperations.List(zipPath, password: null).Select(e => e.Name).ToList();
      Assert.That(names, Does.Not.Contain("alpha.txt"));
      Assert.That(names, Does.Contain("bravo.txt"));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("RoundTrip")]
  public void Replace_RebuildPath_Zip_SwapsContent() {
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "doc.txt");
      File.WriteAllText(src, "version-one");

      var zipPath = Path.Combine(dir, "test.zip");
      ArchiveOperations.Create(zipPath, [new ArchiveInput(src, "doc.txt")], new CompressionOptions());

      var newSrc = Path.Combine(dir, "new-doc.txt");
      File.WriteAllText(newSrc, "version-two");

      ArchiveOperations.Replace(zipPath, "doc.txt", newSrc, new CompressionOptions());

      var extractDir = Path.Combine(dir, "out");
      Directory.CreateDirectory(extractDir);
      ArchiveOperations.Extract(zipPath, extractDir, password: null, files: null);
      Assert.That(File.ReadAllText(Path.Combine(extractDir, "doc.txt")), Is.EqualTo("version-two"));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("RoundTrip")]
  public void Replace_NonExistentEntry_AddsAsNew() {
    // Replace = Remove + Add. Removing a non-existent entry is a no-op for the
    // modifier path (returns false) and a delete-of-nonexistent for rebuild.
    // Either way the Add half must still create the entry.
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "alpha.txt");
      File.WriteAllText(src, "alpha");

      var zipPath = Path.Combine(dir, "test.zip");
      ArchiveOperations.Create(zipPath, [new ArchiveInput(src, "alpha.txt")], new CompressionOptions());

      var newSrc = Path.Combine(dir, "novel.txt");
      File.WriteAllText(newSrc, "fresh-content");
      ArchiveOperations.Replace(zipPath, "novel.txt", newSrc, new CompressionOptions());

      var names = ArchiveOperations.List(zipPath, password: null).Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("novel.txt"));
    } finally { Directory.Delete(dir, true); }
  }

  private static string MakeTempDir() {
    var p = Path.Combine(Path.GetTempPath(), "cwb_test_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(p);
    return p;
  }
}
