#pragma warning disable CS1591
using Compression.Lib;

namespace Compression.Tests.Operations.Atomicity;

/// <summary>
/// Verifies that conversion operations in <see cref="ArchiveOperations"/>
/// are power-fail resistant and atomic. The contract is:
/// <list type="bullet">
///   <item>Successful conversion produces the expected output.</item>
///   <item>A failed conversion (exception mid-write) leaves the target either
///         absent (if it didn't exist before) or unchanged (if it did).</item>
///   <item>No <c>.tmp</c> sibling files are left behind after either path.</item>
/// </list>
/// </summary>
[TestFixture]
public class ConversionAtomicityTests {

  // ── AtomicFileWriter unit tests ─────────────────────────────────────

  [Test, Category("Atomicity")]
  public void WriteAtomic_Success_ProducesExpectedBytes() {
    var dir = MakeTempDir();
    try {
      var target = Path.Combine(dir, "out.bin");
      var payload = "hello world"u8.ToArray();

      AtomicFileWriter.WriteAtomic(target, fs => fs.Write(payload, 0, payload.Length));

      Assert.That(File.Exists(target), Is.True, "Target file must exist after success.");
      Assert.That(File.ReadAllBytes(target), Is.EqualTo(payload), "Target must contain exactly the written bytes.");
      Assert.That(CountTempSiblings(dir, "out.bin"), Is.Zero, "No .tmp siblings should remain after success.");
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("Atomicity")]
  public void WriteAtomic_Failure_LeavesNoTargetFile_WhenTargetDidNotExist() {
    var dir = MakeTempDir();
    try {
      var target = Path.Combine(dir, "out.bin");

      Assert.Throws<InvalidOperationException>(() =>
        AtomicFileWriter.WriteAtomic(target, fs => {
          fs.WriteByte(0xAA);
          throw new InvalidOperationException("simulated mid-write failure");
        }));

      Assert.That(File.Exists(target), Is.False,
        "Target must not exist after a failed write when it didn't exist before.");
      Assert.That(CountTempSiblings(dir, "out.bin"), Is.Zero,
        "Orphan .tmp siblings are forbidden after failure.");
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("Atomicity")]
  public void WriteAtomic_Failure_PreservesExistingTarget() {
    var dir = MakeTempDir();
    try {
      var target = Path.Combine(dir, "existing.bin");
      var original = "ORIGINAL"u8.ToArray();
      File.WriteAllBytes(target, original);

      Assert.Throws<InvalidOperationException>(() =>
        AtomicFileWriter.WriteAtomic(target, fs => {
          fs.WriteByte(0xFF);
          throw new InvalidOperationException("simulated mid-write failure");
        }));

      Assert.That(File.ReadAllBytes(target), Is.EqualTo(original),
        "Existing target must be byte-for-byte unchanged after a failed write.");
      Assert.That(CountTempSiblings(dir, "existing.bin"), Is.Zero,
        "Orphan .tmp siblings are forbidden after failure.");
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("Atomicity")]
  public void WriteAllBytesAtomic_Success_ProducesExpectedBytes() {
    var dir = MakeTempDir();
    try {
      var target = Path.Combine(dir, "bytes.bin");
      var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

      AtomicFileWriter.WriteAllBytesAtomic(target, payload);

      Assert.That(File.ReadAllBytes(target), Is.EqualTo(payload));
      Assert.That(CountTempSiblings(dir, "bytes.bin"), Is.Zero);
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("Atomicity")]
  public void WriteAtomic_OverwriteExistingTarget_OnSuccess_ReplacesContents() {
    var dir = MakeTempDir();
    try {
      var target = Path.Combine(dir, "replace.bin");
      File.WriteAllBytes(target, "OLD"u8.ToArray());

      var fresh = "NEW-CONTENT"u8.ToArray();
      AtomicFileWriter.WriteAtomic(target, fs => fs.Write(fresh, 0, fresh.Length));

      Assert.That(File.ReadAllBytes(target), Is.EqualTo(fresh));
      Assert.That(CountTempSiblings(dir, "replace.bin"), Is.Zero);
    } finally { Directory.Delete(dir, true); }
  }

  // ── ArchiveOperations.Create atomic-write tests ──────────────────────

  [Test, Category("Atomicity")]
  public void Create_Zip_Success_ProducesValidArchive_AndNoTempFiles() {
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "source.txt");
      File.WriteAllText(src, "atomic-create");

      var zipPath = Path.Combine(dir, "out.zip");
      ArchiveOperations.Create(zipPath, [new ArchiveInput(src, "source.txt")], new CompressionOptions());

      Assert.That(File.Exists(zipPath), Is.True);
      Assert.That(new FileInfo(zipPath).Length, Is.GreaterThan(0));
      Assert.That(CountTempSiblings(dir, "out.zip"), Is.Zero,
        "Create() must clean up its staging .tmp on success.");
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("Atomicity")]
  public void Create_Zip_Failure_LeavesExistingTargetUntouched() {
    var dir = MakeTempDir();
    try {
      // Pre-seed the target so we can prove it survives a failed Create.
      var zipPath = Path.Combine(dir, "existing.zip");
      var sentinel = "SENTINEL-DO-NOT-OVERWRITE"u8.ToArray();
      File.WriteAllBytes(zipPath, sentinel);

      // Force failure: passing a source path that doesn't exist makes the
      // ZIP writer throw when it tries to stream the entry's bytes.
      var ghost = Path.Combine(dir, "does-not-exist.txt");
      Assert.That(File.Exists(ghost), Is.False, "Sanity: ghost source must not exist.");

      Assert.Throws<FileNotFoundException>(() =>
        ArchiveOperations.Create(zipPath,
          [new ArchiveInput(ghost, "ghost.txt")], new CompressionOptions()));

      Assert.That(File.ReadAllBytes(zipPath), Is.EqualTo(sentinel),
        "A failed Create() must leave the prior target byte-for-byte intact.");
      Assert.That(CountTempSiblings(dir, "existing.zip"), Is.Zero,
        "A failed Create() must not leave .tmp siblings behind.");
    } finally { Directory.Delete(dir, true); }
  }

  // ── ArchiveOperations.Convert atomic-write tests ─────────────────────

  [Test, Category("Atomicity")]
  public void Convert_Tier1_Success_ProducesValidArchive_AndNoTempFiles() {
    var dir = MakeTempDir();
    try {
      // Build a small Gzip file to convert.
      var payload = System.Text.Encoding.UTF8.GetBytes(
        "Repeat repeat repeat repeat repeat repeat repeat repeat repeat repeat.");
      var gzPath = Path.Combine(dir, "in.gz");
      using (var outFs = File.Create(gzPath))
      using (var gz = new FileFormat.Gzip.GzipStream(outFs,
        Compression.Core.Streams.CompressionStreamMode.Compress,
        Compression.Core.Deflate.DeflateCompressionLevel.Default,
        leaveOpen: true)) {
        gz.Write(payload);
      }

      var zlibPath = Path.Combine(dir, "out.zlib");
      var (_, tier) = ArchiveOperations.Convert(gzPath, zlibPath, password: null);

      Assert.That(tier, Is.EqualTo(1), "gz→zlib should take Tier 1 (bitstream transfer).");
      Assert.That(File.Exists(zlibPath), Is.True);
      Assert.That(new FileInfo(zlibPath).Length, Is.GreaterThan(0));
      Assert.That(CountTempSiblings(dir, "out.zlib"), Is.Zero,
        "Convert() must clean up its staging .tmp on success.");
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("Atomicity")]
  public void Convert_Failure_PreservesExistingTarget_AndCleansTemps() {
    var dir = MakeTempDir();
    try {
      // Pre-seed the target so we can prove it survives a failed Convert.
      var dstPath = Path.Combine(dir, "out.zlib");
      var sentinel = "SENTINEL-DO-NOT-OVERWRITE"u8.ToArray();
      File.WriteAllBytes(dstPath, sentinel);

      // Pass a non-existent source path. Convert() opens the source first,
      // which throws FileNotFoundException before any output bytes land.
      var ghost = Path.Combine(dir, "does-not-exist.gz");
      Assert.That(File.Exists(ghost), Is.False);

      Assert.Throws<FileNotFoundException>(() =>
        ArchiveOperations.Convert(ghost, dstPath, password: null));

      Assert.That(File.ReadAllBytes(dstPath), Is.EqualTo(sentinel),
        "A failed Convert() must leave the prior target byte-for-byte intact.");
      Assert.That(CountTempSiblings(dir, "out.zlib"), Is.Zero,
        "A failed Convert() must not leave .tmp siblings behind.");
    } finally { Directory.Delete(dir, true); }
  }

  // ── ArchiveOperations.ConvertFs atomic-write tests ───────────────────

  [Test, Category("Atomicity")]
  public void ConvertFs_Failure_LeavesNoOrphanTemps() {
    var dir = MakeTempDir();
    try {
      // Build a D64 source with a single file.
      var srcPath = Path.Combine(dir, "src.d64");
      using (var fs = File.Create(srcPath))
        fs.Write(new FileSystem.D64.D64Writer().Build());

      var dstPath = Path.Combine(dir, "out.zip");
      var sentinel = "SENTINEL"u8.ToArray();
      File.WriteAllBytes(dstPath, sentinel);

      // ConvertFs to an unknown target format triggers a NotSupportedException
      // before any bytes hit disk.
      Assert.Throws<NotSupportedException>(() =>
        ArchiveOperations.ConvertFs(srcPath, dstPath, targetFormatId: "this-format-does-not-exist"));

      Assert.That(File.ReadAllBytes(dstPath), Is.EqualTo(sentinel),
        "A failed ConvertFs() must leave the prior target byte-for-byte intact.");
      Assert.That(CountTempSiblings(dir, "out.zip"), Is.Zero,
        "A failed ConvertFs() must not leave .tmp siblings behind.");
    } finally { Directory.Delete(dir, true); }
  }

  // ── ArchiveOperations.ConvertClusters atomic-write tests ─────────────

  [Test, Category("Atomicity")]
  public void ConvertClusters_Success_LeavesNoTempFiles() {
    var dir = MakeTempDir();
    try {
      // Build a small FAT image.
      var srcPath = Path.Combine(dir, "src.img");
      var writer = new FileSystem.Fat.FatWriter();
      writer.AddFile("HELLO.TXT", "hello-fat"u8.ToArray());
      var fatBytes = writer.Build(totalSectors: 2880); // 1.44MB-ish
      File.WriteAllBytes(srcPath, fatBytes);

      var dstPath = Path.Combine(dir, "out.img");
      ArchiveOperations.ConvertClusters(srcPath, dstPath, targetClusterSize: 1024);

      Assert.That(File.Exists(dstPath), Is.True);
      Assert.That(CountTempSiblings(dir, "out.img"), Is.Zero,
        "ConvertClusters() must clean up its staging .tmp on success.");
    } finally { Directory.Delete(dir, true); }
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  private static string MakeTempDir() {
    var p = Path.Combine(Path.GetTempPath(), "cwb_atomicity_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(p);
    return p;
  }

  /// <summary>
  /// Counts <c>.tmp.*</c> siblings of <paramref name="targetFileName"/> in
  /// <paramref name="dir"/>. Used to verify the fail-safe protocol cleaned
  /// up after itself.
  /// </summary>
  private static int CountTempSiblings(string dir, string targetFileName) {
    return Directory.GetFiles(dir, targetFileName + ".tmp.*").Length;
  }
}
