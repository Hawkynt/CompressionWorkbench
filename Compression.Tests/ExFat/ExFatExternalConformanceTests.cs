#pragma warning disable CS1591

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Compression.Tests.ExFat;

/// <summary>
/// Spec-conformance check of the exFAT writer against the reference
/// <c>fsck.exfat</c> from exfatprogs. The writer must produce a volume that an
/// independent implementation declares clean — proving the boot-region
/// checksum, allocation bitmap, up-case table + checksum, FAT/bitmap
/// consistency, every File entry set's set-checksum, the Stream Extension
/// name-hash, and the cluster chains are all self-consistent.
/// <para>
/// The check shells out to the real tool and skips cleanly when it is absent
/// (e.g. on Windows CI or a machine without exfatprogs), mirroring the
/// HasCommand / RunTool pattern in <c>OsIntegrationTests</c>.
/// </para>
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class ExFatExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_exfat_fsck_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── A representative volume: root files, a nested tree, a ~50-file
  //    directory, and a mix of small and larger files. ──────────────────

  private static byte[] Build_RepresentativeImage(int sizeMB = 0, int clusterBytes = 0) {
    var writer = new FileSystem.ExFat.ExFatWriter();

    // Root-level files, small and larger.
    writer.AddFile("readme.txt", "exFAT conformance fixture"u8.ToArray());
    writer.AddFile("large.bin", FilledBytes(200_000, seed: 1));

    // A nested directory tree.
    writer.AddFile("docs/guide.txt", FilledBytes(3_000, seed: 2));
    writer.AddFile("docs/api/reference.bin", FilledBytes(20_000, seed: 3));
    writer.AddFile("a/b/c/d/e/deep.txt", "five levels down"u8.ToArray());

    // A directory holding ~50 files of varied sizes.
    for (var i = 0; i < 50; ++i)
      writer.AddFile($"bigdir/file{i:D2}.dat", FilledBytes(100 + i * 37, seed: 100 + i));

    return sizeMB > 0 ? writer.Build(sizeMB, clusterBytes) : writer.BuildAutoSized(clusterBytes);
  }

  private static byte[] FilledBytes(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  // ── given a built image, when fsck.exfat checks it read-only,
  //    then it reports the volume clean (exit 0, no errors) ──────────────

  [Test]
  [CancelAfter(60_000)]
  public void AutoSizedImage_IsCleanAccordingToFsckExfat() {
    RequireFsckExfat();

    var imagePath = Path.Combine(_tmpDir, "auto.exfat");
    File.WriteAllBytes(imagePath, Build_RepresentativeImage());

    AssertFsckReportsClean(imagePath);
  }

  [Test]
  [CancelAfter(60_000)]
  public void FixedSizeImage_IsCleanAccordingToFsckExfat() {
    RequireFsckExfat();

    var imagePath = Path.Combine(_tmpDir, "fixed16.exfat");
    File.WriteAllBytes(imagePath, Build_RepresentativeImage(sizeMB: 16));

    AssertFsckReportsClean(imagePath);
  }

  [Test]
  [CancelAfter(60_000)]
  public void LargeClusterImage_IsCleanAccordingToFsckExfat() {
    RequireFsckExfat();

    // A 256 MB image with 64 KB clusters exercises a different geometry
    // (larger FAT, larger heap) than the small-cluster default.
    var imagePath = Path.Combine(_tmpDir, "big64k.exfat");
    File.WriteAllBytes(imagePath, Build_RepresentativeImage(sizeMB: 256, clusterBytes: 65536));

    AssertFsckReportsClean(imagePath);
  }

  [Test]
  [CancelAfter(60_000)]
  public void WideDirectorySpanningManyClusters_IsCleanAccordingToFsckExfat() {
    RequireFsckExfat();

    // 300 files in one directory force its entry region across several
    // clusters, checking the directory cluster-chain layout.
    var writer = new FileSystem.ExFat.ExFatWriter();
    for (var i = 0; i < 300; ++i)
      writer.AddFile($"wide/f{i:D3}.txt", FilledBytes(50, seed: i));

    var imagePath = Path.Combine(_tmpDir, "wide.exfat");
    File.WriteAllBytes(imagePath, writer.Build(32));

    AssertFsckReportsClean(imagePath);
  }

  // ── In-place modify conformance: add / remove on an existing volume must
  //    leave it fsck-clean (genuine R/W, no rebuild). ──────────────────────

  [Test]
  [CancelAfter(60_000)]
  public void InPlaceAdd_SmallAndMultiCluster_StaysCleanAndReadsBack() {
    RequireFsckExfat();

    var imagePath = Path.Combine(_tmpDir, "inplace_add.exfat");
    var writer = new FileSystem.ExFat.ExFatWriter();
    writer.AddFile("keep.txt", "preserve-me-at-offset"u8.ToArray());
    File.WriteAllBytes(imagePath, writer.Build(32));
    AssertFsckReportsClean(imagePath);

    var small = "small-added-in-place"u8.ToArray();
    var large = FilledBytes(60_000, seed: 17); // ~15 clusters @ 4 KB
    using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite)) {
      FileSystem.ExFat.ExFatModifier.AddFile(fs, "small.txt", small);
      FileSystem.ExFat.ExFatModifier.AddFile(fs, "large.bin", large);
    }
    AssertFsckReportsClean(imagePath);

    using (var fs = File.OpenRead(imagePath)) {
      var reader = new FileSystem.ExFat.ExFatReader(fs);
      Assert.Multiple(() => {
        Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "small.txt")), Is.EqualTo(small));
        Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "large.bin")), Is.EqualTo(large));
        Assert.That(System.Text.Encoding.ASCII.GetString(
          reader.Extract(reader.Entries.Single(e => e.Name == "keep.txt"))), Is.EqualTo("preserve-me-at-offset"));
      });
    }
  }

  [Test]
  [CancelAfter(60_000)]
  public void InPlaceRemove_FreesClustersAndStaysClean() {
    RequireFsckExfat();

    var imagePath = Path.Combine(_tmpDir, "inplace_remove.exfat");
    var writer = new FileSystem.ExFat.ExFatWriter();
    writer.AddFile("keep.txt", "stays"u8.ToArray());
    writer.AddFile("gone.bin", FilledBytes(40_000, seed: 3));
    File.WriteAllBytes(imagePath, writer.Build(32));
    AssertFsckReportsClean(imagePath);

    using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite))
      Assert.That(FileSystem.ExFat.ExFatModifier.RemoveFile(fs, "gone.bin"), Is.True);
    AssertFsckReportsClean(imagePath);

    using (var fs = File.OpenRead(imagePath)) {
      var reader = new FileSystem.ExFat.ExFatReader(fs);
      Assert.That(reader.Entries.Any(e => e.Name == "gone.bin"), Is.False);
      Assert.That(reader.Entries.Any(e => e.Name == "keep.txt"), Is.True);
    }
  }

  [Test]
  [CancelAfter(120_000)]
  public void InPlaceAdd_GrowsRootDirectoryAcrossClusters_StaysClean() {
    RequireFsckExfat();

    // One 4 KB root cluster holds ~128 entries (≈ 42 three-entry files). Adding
    // 100 files forces the root directory to grow into additional clusters — the
    // case that must pad the prior cluster's tail so no 0x00 end-marker orphans
    // the appended clusters.
    var imagePath = Path.Combine(_tmpDir, "inplace_grow.exfat");
    File.WriteAllBytes(imagePath, new FileSystem.ExFat.ExFatWriter().Build(32));

    using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite))
      for (var i = 0; i < 100; ++i)
        FileSystem.ExFat.ExFatModifier.AddFile(fs, $"GROW{i:D3}.DAT", FilledBytes(100 + i, seed: i));
    AssertFsckReportsClean(imagePath);

    using (var fs = File.OpenRead(imagePath)) {
      var reader = new FileSystem.ExFat.ExFatReader(fs);
      Assert.That(reader.Entries.Count(e => e.Name.StartsWith("GROW")), Is.EqualTo(100));
    }
  }

  // ── fsck.exfat plumbing (HasCommand / RunTool style) ─────────────────

  private static void RequireFsckExfat() {
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      Assert.Ignore("fsck.exfat conformance check is Linux/macOS only");
    if (!HasCommand("fsck.exfat"))
      Assert.Ignore("fsck.exfat (exfatprogs) not installed");
  }

  private static void AssertFsckReportsClean(string imagePath) {
    // -n = answer "no" to every repair prompt (read-only check). exfatprogs
    // returns 0 only when the volume is already clean.
    var result = RunTool("fsck.exfat", $"-n \"{imagePath}\"");
    TestContext.Out.WriteLine(result.StdOut);
    TestContext.Out.WriteLine(result.StdErr);

    Assert.Multiple(() => {
      Assert.That(result.ExitCode, Is.EqualTo(0),
        $"fsck.exfat reported a non-clean volume.\nstdout: {result.StdOut}\nstderr: {result.StdErr}");
      Assert.That(result.StdOut, Does.Contain("clean").IgnoreCase,
        "fsck.exfat did not report the volume as clean");
      Assert.That(result.StdOut + result.StdErr, Does.Not.Contain("corrupted").IgnoreCase,
        "fsck.exfat reported corruption");
    });
  }

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static bool HasCommand(string name) {
    try {
      var result = RunShell($"which {name} 2>/dev/null");
      return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
    } catch {
      return false;
    }
  }

  private static ToolResult RunShell(string command) {
    var psi = new ProcessStartInfo {
      FileName = "/bin/sh",
      Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start shell for: {command}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(30_000)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }

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
    if (!proc.WaitForExit(timeoutMs)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }
}
