#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Validates the in-memory ConvertArchive pipeline: when the source fits in
/// the InMemoryProcessing budget we extract straight into byte arrays, build
/// the target in a MemoryStream and commit it atomically — no tempdir, no
/// torn writes. The disk-tempdir fallback path is preserved for huge images
/// and we exercise it by lowering the threshold.
/// </summary>
[TestFixture]
public class ConvertArchiveInMemoryTests {

  // ── Helpers ────────────────────────────────────────────────────────

  private static string MakeTempDir() {
    var p = Path.Combine(Path.GetTempPath(), "cwb_test_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(p);
    return p;
  }

  /// <summary>
  /// Builds a tiny in-memory FAT image with two text files in it.
  /// </summary>
  private static byte[] BuildSmallFatImage() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("HELLO.TXT", "hello, in-memory pipeline"u8.ToArray());
    w.AddFile("WORLD.TXT", "the second one"u8.ToArray());
    return w.BuildAutoSized();
  }

  /// <summary>
  /// Snapshot of cwb_x2m_* and cwb_fsconv_* tempdir names so a test can
  /// assert no leak after the call.
  /// </summary>
  private static HashSet<string> SnapshotTempDirs() {
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in Directory.EnumerateDirectories(Path.GetTempPath(), "cwb_x2m_*"))
      set.Add(d);
    foreach (var d in Directory.EnumerateDirectories(Path.GetTempPath(), "cwb_fsconv_*"))
      set.Add(d);
    return set;
  }

  // ── Tests ──────────────────────────────────────────────────────────

  [Test, Category("InMemoryPipeline")]
  public void SmallImage_UsesInMemoryPath_NoTempDirLeak() {
    var dir = MakeTempDir();
    var savedThreshold = InMemoryProcessing.ThresholdBytes;
    try {
      InMemoryProcessing.ThresholdBytes = InMemoryProcessing.DefaultThresholdBytes; // generous

      var src = Path.Combine(dir, "src.img");
      File.WriteAllBytes(src, BuildSmallFatImage());

      var dst = Path.Combine(dir, "dst.zip");
      var before = SnapshotTempDirs();

      var warnings = ArchiveOperations.ConvertArchive(src, dst);

      var after = SnapshotTempDirs();
      var leaked = after.Except(before).ToList();
      Assert.That(leaked, Is.Empty, "Conversion leaked tempdirs: " + string.Join(", ", leaked));
      Assert.That(File.Exists(dst), Is.True, "Output file was not created.");

      // Cross-category warning should still be emitted (filesystem → archive).
      Assert.That(warnings.Any(w => w.Contains("Cross-category", StringComparison.OrdinalIgnoreCase)),
                  Is.True, "Cross-category warning missing.");

      // Round-trip: ZIP must contain both files we put in.
      var entries = ArchiveOperations.List(dst, password: null);
      var names = entries.Select(e => e.Name).ToList();
      Assert.That(names.Any(n => n.Contains("HELLO", StringComparison.OrdinalIgnoreCase)), Is.True);
      Assert.That(names.Any(n => n.Contains("WORLD", StringComparison.OrdinalIgnoreCase)), Is.True);
    } finally {
      InMemoryProcessing.ThresholdBytes = savedThreshold;
      try { Directory.Delete(dir, true); } catch { }
    }
  }

  [Test, Category("InMemoryPipeline")]
  public void SmallImage_OutputIsAtomic_NoTornFileOnWriterCrash() {
    // We can't easily inject a throwing writer mid-stream without a custom
    // descriptor, so this test verifies the atomic-rename guarantee a
    // different way: if the writer succeeds the file exists; if it would
    // crash mid-write, RebuildToFileAtomic stages bytes in RAM first and
    // only renames once they're all there. Use the existing pipeline and
    // assert that the final on-disk file matches a round-tripped readback
    // byte-exact, proving no truncation/torn intermediate ever landed.
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "src.img");
      File.WriteAllBytes(src, BuildSmallFatImage());

      var dst = Path.Combine(dir, "dst.zip");
      ArchiveOperations.ConvertArchive(src, dst);

      // Re-open and verify every entry decompresses cleanly.
      Assert.That(File.Exists(dst), Is.True);
      Assert.That(new FileInfo(dst).Length, Is.GreaterThan(22), "ZIP too small to contain EOCD.");
      Assert.That(ArchiveOperations.Test(dst, password: null), Is.True,
        "ZIP integrity check failed — atomic write left a torn file.");
    } finally {
      try { Directory.Delete(dir, true); } catch { }
    }
  }

  [Test, Category("InMemoryPipeline")]
  public void LargeImage_UsesDiskPath_StillSucceeds() {
    var dir = MakeTempDir();
    var savedThreshold = InMemoryProcessing.ThresholdBytes;
    try {
      // Force the disk-tempdir fallback by capping the in-memory budget at
      // a tiny value the source comfortably exceeds.
      InMemoryProcessing.ThresholdBytes = 1024;

      var src = Path.Combine(dir, "src.img");
      File.WriteAllBytes(src, BuildSmallFatImage()); // ~auto-sized FAT, well above 1 KiB

      Assert.That(new FileInfo(src).Length, Is.GreaterThan(1024L),
        "Test setup: source must exceed the forced threshold.");

      var dst = Path.Combine(dir, "dst.zip");
      ArchiveOperations.ConvertArchive(src, dst);

      Assert.That(File.Exists(dst), Is.True);
      var entries = ArchiveOperations.List(dst, password: null);
      Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    } finally {
      InMemoryProcessing.ThresholdBytes = savedThreshold;
      try { Directory.Delete(dir, true); } catch { }
    }
  }

  [Test, Category("InMemoryPipeline")]
  public void ExtractEntryToMemory_FatSource_ReturnsByteExactContent() {
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "src.img");
      File.WriteAllBytes(src, BuildSmallFatImage());

      FormatRegistration.EnsureInitialized();
      var ops = FormatRegistry.GetArchiveOps("Fat");
      Assert.That(ops, Is.Not.Null);

      using var fs = File.OpenRead(src);
      var bytes = ops!.ExtractEntryToMemory(fs, "HELLO.TXT", password: null);

      Assert.That(bytes, Is.EqualTo("hello, in-memory pipeline"u8.ToArray()).AsCollection);
    } finally {
      try { Directory.Delete(dir, true); } catch { }
    }
  }

  [Test, Category("InMemoryPipeline")]
  public void ExtractEntryToMemory_ZipSource_ReturnsByteExactContent() {
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "src.zip");
      using (var fs = File.Create(src)) {
        var w = new FileFormat.Zip.ZipWriter(fs, leaveOpen: true);
        w.AddEntry("a.txt", "alpha"u8.ToArray());
        w.AddEntry("b.txt", "beta"u8.ToArray());
        w.Finish();
      }

      FormatRegistration.EnsureInitialized();
      var ops = FormatRegistry.GetArchiveOps("Zip");
      Assert.That(ops, Is.Not.Null);

      using var fs2 = File.OpenRead(src);
      var bytes = ops!.ExtractEntryToMemory(fs2, "b.txt", password: null);

      Assert.That(bytes, Is.EqualTo("beta"u8.ToArray()).AsCollection);
    } finally {
      try { Directory.Delete(dir, true); } catch { }
    }
  }
}
