#pragma warning disable CS1591

using System.Diagnostics;
using System.Text;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// Verifies that images produced by <see cref="FatWriter"/> are accepted as
/// structurally clean by <c>fsck.fat</c> (dosfstools) — an independent,
/// widely deployed reference implementation of the FAT specification.
///
/// <para>The check is read-only (<c>fsck.fat -n</c>). It cross-validates every
/// conformance point the writer is responsible for: the BPB geometry, both FAT
/// copies being identical, the FAT[0]/FAT[1] media + end-of-chain markers, the
/// FAT32 FSInfo free-cluster summary and backup boot sector, the cluster-chain
/// termination, and the <c>.</c>/<c>..</c> entries inside every subdirectory.</para>
///
/// <para>The suite skips cleanly when <c>fsck.fat</c> is not installed, so it is
/// a no-op on machines without dosfstools.</para>
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class FatExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_fatfsck_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── Tool detection / execution (mirrors OsIntegrationTests) ─────────

  private static bool HasCommand(string name) {
    try {
      var result = RunTool("/bin/sh", $"-c \"which {name} 2>/dev/null\"");
      return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
    } catch {
      return false;
    }
  }

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static ToolResult RunTool(string tool, string args, int timeoutMs = 60_000) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start {tool}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs))
      try { proc.Kill(); } catch { /* best effort */ }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }

  // ── fsck.fat result interpretation ──────────────────────────────────
  //
  // fsck.fat -n is "no-op": it reports problems but never repairs. Crucially
  // it returns exit 0 even for FAT-copy mismatches and a wrong FSInfo free
  // count ("Auto-correcting." — which it then doesn't do, because -n). So the
  // exit code alone is NOT a reliable pass signal; we must also assert the
  // output is free of fsck.fat's problem phrases. These phrases appear only
  // when fsck flags a defect — the benign header line "Checking free cluster
  // summary." is deliberately excluded.
  private static readonly string[] ProblemPhrases = [
    "differ",            // "FATs differ ..."
    "wrong",             // "Free cluster summary wrong ..."
    "Invalid",           // "Invalid '.' entry ..."
    "Fixing",            // structural repair
    "Auto-correcting",   // FSInfo repair
    "Corrupt",
    "corrupted",
    "orphan",
    "truncated",
    "mismatch",
    "Dropping",
    "Reclaiming",
    "Removing",
    "Unaligned",
    "bad cluster",
    "Cluster start",     // "Cluster start does/should ..."
    "contains a bad",
  ];

  private static (bool Clean, string Report) RunFsck(string imagePath) {
    var r = RunTool("fsck.fat", $"-n -v \"{imagePath}\"");
    var combined = r.StdOut + "\n" + r.StdErr;
    // fsck.fat echoes the image path on its trailing summary line
    // ("<path>: N files, ...") and again on any error line that names the
    // file. We scan for problem phrases, so the path must not be allowed to
    // contribute matches (e.g. a temp dir literally containing "corrupt").
    // Strip the image path from the text before phrase-matching.
    var scan = combined.Replace(imagePath, "<image>", StringComparison.Ordinal);
    var flagged = ProblemPhrases
      .Where(p => scan.Contains(p, StringComparison.OrdinalIgnoreCase))
      .ToList();
    var clean = r.ExitCode == 0 && flagged.Count == 0;
    var report = $"exit={r.ExitCode}; flagged=[{string.Join(", ", flagged)}]\n{combined}";
    return (clean, report);
  }

  // ── Deterministic test payloads ─────────────────────────────────────

  private static byte[] Bytes(int n, int seed) {
    var r = new Random(seed);
    var b = new byte[n];
    r.NextBytes(b);
    return b;
  }

  /// <summary>FAT12 1.44 MB floppy: a root file, a two-level nested tree, and
  /// a single subdirectory holding ~50 small files.</summary>
  private static byte[] BuildFat12Floppy() {
    var w = new FatWriter();
    w.AddFile("README.TXT", Encoding.ASCII.GetBytes("conformance probe: FAT12 root file"));
    w.AddFile("docs/readme.md", Bytes(1234, 1));
    w.AddFile("docs/sub/deep.bin", Bytes(2000, 2));
    for (var i = 0; i < 50; i++)
      w.AddFile($"many/file{i:D3}.dat", Bytes(100 + i, 100 + i));
    return w.Build(totalSectors: 2880, forcedFatType: 12);
  }

  /// <summary>FAT16 image with a nested tree and a moderate subdirectory.</summary>
  private static byte[] BuildFat16() {
    var w = new FatWriter();
    w.AddFile("ROOT.TXT", Encoding.ASCII.GetBytes("conformance probe: FAT16 root file"));
    w.AddFile("dir1/data.bin", Bytes(5000, 10));
    w.AddFile("dir1/dir2/nested.bin", Bytes(9000, 11));
    for (var i = 0; i < 30; i++)
      w.AddFile($"stuff/item{i:D2}.txt", Bytes(500 + i * 7, 200 + i));
    return w.Build(totalSectors: 40_000, forcedFatType: 16);
  }

  /// <summary>FAT32 image (>= 200 000 sectors) with a three-level nested tree,
  /// a large file, and a subdirectory holding ~1000 files.</summary>
  private static byte[] BuildFat32() {
    var w = new FatWriter();
    w.AddFile("README.TXT", Encoding.ASCII.GetBytes("conformance probe: FAT32 root file"));
    w.AddFile("a/b/c/deep.bin", Bytes(70_000, 20));
    w.AddFile("big.bin", Bytes(300_000, 21));
    for (var i = 0; i < 1000; i++)
      w.AddFile($"bigdir/f{i:D4}.dat", Bytes(50 + i % 200, 1000 + i));
    return w.Build(totalSectors: 300_000, forcedFatType: 32);
  }

  private string WriteImage(string name, byte[] image) {
    var path = Path.Combine(_tmpDir, name);
    File.WriteAllBytes(path, image);
    return path;
  }

  // ═══════════════════════════════════════════════════════════════════
  // Given a FatWriter image, when fsck.fat -n verifies it, then it is clean.
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Fat12FloppyImage_IsCleanUnderFsckFat() {
    if (!HasCommand("fsck.fat")) Assert.Ignore("fsck.fat (dosfstools) not installed");

    var path = WriteImage("fat12.img", BuildFat12Floppy());
    var (clean, report) = RunFsck(path);

    Assert.That(clean, Is.True, $"fsck.fat flagged the FAT12 image:\n{report}");
  }

  [Test]
  public void Fat16Image_IsCleanUnderFsckFat() {
    if (!HasCommand("fsck.fat")) Assert.Ignore("fsck.fat (dosfstools) not installed");

    var path = WriteImage("fat16.img", BuildFat16());
    var (clean, report) = RunFsck(path);

    Assert.That(clean, Is.True, $"fsck.fat flagged the FAT16 image:\n{report}");
  }

  [Test]
  [CancelAfter(120_000)]
  public void Fat32Image_IsCleanUnderFsckFat() {
    if (!HasCommand("fsck.fat")) Assert.Ignore("fsck.fat (dosfstools) not installed");

    var path = WriteImage("fat32.img", BuildFat32());
    var (clean, report) = RunFsck(path);

    Assert.That(clean, Is.True, $"fsck.fat flagged the FAT32 image:\n{report}");
  }

  /// <summary>The streaming <see cref="FatWriter.BuildTo"/> path must produce
  /// an image fsck.fat also considers clean (and, per the parity tests,
  /// byte-identical to <see cref="FatWriter.Build"/>).</summary>
  [Test]
  [CancelAfter(120_000)]
  public void Fat32StreamedImage_IsCleanUnderFsckFat() {
    if (!HasCommand("fsck.fat")) Assert.Ignore("fsck.fat (dosfstools) not installed");

    var w = new FatWriter();
    w.AddFile("README.TXT", Encoding.ASCII.GetBytes("conformance probe: streamed FAT32"));
    w.AddFile("a/b/c/deep.bin", Bytes(70_000, 20));
    w.AddFile("big.bin", Bytes(300_000, 21));
    for (var i = 0; i < 1000; i++)
      w.AddFile($"bigdir/f{i:D4}.dat", Bytes(50 + i % 200, 1000 + i));

    var path = Path.Combine(_tmpDir, "fat32_streamed.img");
    using (var fs = File.Create(path))
      w.BuildTo(fs, totalSectors: 300_000, forcedFatType: 32);

    var (clean, report) = RunFsck(path);
    Assert.That(clean, Is.True, $"fsck.fat flagged the streamed FAT32 image:\n{report}");
  }

  // ═══════════════════════════════════════════════════════════════════
  // Guard: prove the conformance assertion actually bites.
  //
  // fsck.fat -n returns exit 0 even for a FAT-copy mismatch, so a naive
  // "exit code only" assertion would silently pass on a corrupt image. This
  // test corrupts the second FAT copy of an otherwise-clean image and shows
  // RunFsck reports it as NOT clean — i.e. the phrase-based check is what
  // makes the conformance tests above meaningful.
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  [CancelAfter(120_000)]
  public void DeliberatelyCorruptedSecondFat_IsRejected_ProvingTheCheckBites() {
    if (!HasCommand("fsck.fat")) Assert.Ignore("fsck.fat (dosfstools) not installed");

    var image = BuildFat32();
    var path = WriteImage("fat32_corrupt.img", image);

    // Sanity: the pristine image is clean.
    var (cleanBefore, reportBefore) = RunFsck(path);
    Assert.That(cleanBefore, Is.True, $"baseline image was not clean:\n{reportBefore}");

    // Corrupt the second FAT copy so it diverges from the first. fsck.fat
    // detects this as "FATs differ" but still exits 0 under -n.
    var bytesPerSector = BitConverter.ToUInt16(image, 11);
    var reservedSectors = BitConverter.ToUInt16(image, 14);
    var fatSize = BitConverter.ToUInt32(image, 36); // BPB_FATSz32
    var secondFatByteOffset = (reservedSectors + fatSize) * bytesPerSector;
    // Flip an in-use entry (cluster 3) in the second FAT only.
    var entryOffset = (int)secondFatByteOffset + 3 * 4;
    image[entryOffset] ^= 0xFF;
    image[entryOffset + 1] ^= 0xFF;
    File.WriteAllBytes(path, image);

    var (cleanAfter, reportAfter) = RunFsck(path);
    Assert.That(cleanAfter, Is.False,
      $"corruption went undetected — the conformance check does not bite:\n{reportAfter}");
  }
}
