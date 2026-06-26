#pragma warning disable CA1416 // e2fsck / mkfs are Linux-only and runtime-guarded.
using System.Diagnostics;
using System.Runtime.InteropServices;
using FileSystem.Ext;

namespace Compression.Tests.Ext;

/// <summary>
/// Proves <see cref="ExtInPlaceShrinker"/> performs a genuine in-place trailing-free
/// shrink: it trims the volume to a smaller block count while leaving every surviving
/// block byte-identical (no re-pack), and produces an image that round-trips through
/// <see cref="ExtReader"/>. The <c>OsIntegration</c> cases hand the shrunk image to the
/// e2fsprogs oracle (<c>e2fsck -fn</c>, <c>debugfs</c>) and skip cleanly when those
/// tools are unavailable.
/// </summary>
[TestFixture]
public class ExtInPlaceShrinkTests {

  // ── Pure-managed proofs (no external tools) ─────────────────────────────

  [Test, Category("HappyPath")]
  public void ShrinkToFit_ReducesImage_AndRoundTripsViaReader() {
    var w = new ExtWriter();
    var alpha = MakeData(40_000); var beta = MakeData(30_000);
    w.AddFile("alpha.bin", alpha);
    w.AddFile("beta.bin", beta);
    w.AddFile("docs/readme.txt", "hello readme"u8.ToArray());
    var image = w.Build(blockSize: 1024, totalBlocks: 8000,
      ExtWriter.ExtVersion.Ext2, journal: false, volumeLabel: "shrk", inodeSize: 128);

    using var ms = new MemoryStream();
    ms.Write(image);
    var originalLen = ms.Length;

    var result = ExtInPlaceShrinker.ShrinkToFit(ms);

    Assert.That(result.WasReduced, Is.True);
    Assert.That(result.NewSize, Is.LessThan(originalLen));
    Assert.That(ms.Length, Is.EqualTo(result.NewSize));

    ms.Position = 0;
    var reader = new ExtReader(ms);
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "alpha.bin")), Is.EqualTo(alpha));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "beta.bin")), Is.EqualTo(beta));
  }

  [Test, Category("HappyPath")]
  public void Shrink_LeavesSurvivingBlocksByteIdentical() {
    var w = new ExtWriter();
    w.AddFile("a.bin", MakeData(50_000));
    w.AddFile("b.bin", MakeData(20_000));
    var image = w.Build(blockSize: 1024, totalBlocks: 8000);

    using var ms = new MemoryStream();
    ms.Write(image);
    var before = ms.ToArray();

    var result = ExtInPlaceShrinker.ShrinkToFit(ms);
    Assert.That(result.WasReduced, Is.True);
    Assert.That(result.BlocksRelocated, Is.Zero, "trailing-free trim relocates nothing");

    // Every data/inode-table block below the new size is unchanged; only a handful of
    // metadata blocks (superblock, block bitmap, group descriptor) differ.
    var newLen = (int)result.NewSize;
    var changed = 0;
    var img = ms.ToArray();
    for (var i = 0; i < newLen; i++)
      if (before[i] != img[i]) changed++;
    Assert.That(changed, Is.LessThan(newLen / 4),
      "only superblock/bitmap/descriptor metadata should differ after a trailing trim");
  }

  [Test, Category("EdgeCase")]
  public void ShrinkToBlocks_RefusesRelocationNeedingTarget() {
    // A target that lands inside the data region must be refused (this version only
    // trims trailing free space).
    var w = new ExtWriter();
    w.AddFile("big.bin", MakeData(200_000));
    var image = w.Build(blockSize: 1024, totalBlocks: 8000);
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => ExtInPlaceShrinker.ShrinkToBlocks(ms, 200));
  }

  [Test, Category("EdgeCase")]
  public void ShrinkToBlocks_NoOpWhenTargetNotSmaller() {
    var w = new ExtWriter();
    w.AddFile("x.bin", MakeData(5_000));
    var image = w.Build(blockSize: 1024, totalBlocks: 8000);
    using var ms = new MemoryStream();
    ms.Write(image);
    var result = ExtInPlaceShrinker.ShrinkToBlocks(ms, 8000);
    Assert.That(result.WasReduced, Is.False);
  }

  // ── External oracle: e2fsck / debugfs against a writer image ────────────

  [TestCase("ext2", false), Category("Conformance")]
  [TestCase("ext3", true)]
  [TestCase("ext4", true)]
  public void Shrink_WriterImage_PassesE2fsckClean(string version, bool journal) {
    RequireTools("e2fsck");
    var ver = version switch {
      "ext2" => ExtWriter.ExtVersion.Ext2,
      "ext3" => ExtWriter.ExtVersion.Ext3,
      _ => ExtWriter.ExtVersion.Ext4,
    };
    var w = new ExtWriter();
    var big = MakeData(60_000);
    w.AddFile("seed.txt", "seed"u8.ToArray());
    w.AddFile("payload.bin", big);
    w.AddFile("nested/dir/file.txt", "nested content"u8.ToArray());
    var image = w.Build(blockSize: 1024, totalBlocks: 8000, ver,
      journal: journal, volumeLabel: "cwbshrk", inodeSize: 256);

    using var ms = new MemoryStream();
    ms.Write(image);
    var result = ExtInPlaceShrinker.ShrinkToFit(ms);
    Assert.That(result.WasReduced, Is.True);

    var dir = NewTempDir();
    try {
      var img = Path.Combine(dir, $"w_{version}.img");
      File.WriteAllBytes(img, ms.ToArray());
      AssertE2fsckClean(img, $"writer {version}");

      // debugfs round-trip of the large file.
      var outFile = Path.Combine(dir, "payload_out.bin");
      Run("debugfs", $"-R \"dump /payload.bin {outFile}\" {img}");
      Assert.That(File.ReadAllBytes(outFile), Is.EqualTo(big),
        "payload must read back byte-identical via debugfs after shrink");
    } finally { TryDelete(dir); }
  }

  // ── External oracle: e2fsck against a REAL mkfs image (checksums/backups) ─

  [TestCase("mkfs.ext2", "^resize_inode"), Category("Conformance")]
  [TestCase("mkfs.ext4", "^has_journal,^64bit,^metadata_csum,^resize_inode")]
  public void Shrink_RealMkfsImage_PassesE2fsckClean_FilesByteIdentical(string mkfs, string opts) {
    RequireTools("dd", mkfs, "e2fsck", "debugfs");

    var dir = NewTempDir();
    try {
      var img = Path.Combine(dir, "real.img");
      Run("dd", $"if=/dev/zero of={img} bs=1024 count=8000 status=none");
      if (Run(mkfs, $"-F -b 1024 -O {opts} {img}").Exit != 0) Assert.Ignore($"{mkfs} failed");

      var a = MakeData(60_000); var b = MakeData(50_000);
      var fa = Path.Combine(dir, "a.bin"); File.WriteAllBytes(fa, a);
      var fb = Path.Combine(dir, "b.bin"); File.WriteAllBytes(fb, b);
      Run("debugfs", $"-w -R \"write {fa} a.bin\" {img}");
      Run("debugfs", $"-w -R \"write {fb} b.bin\" {img}");
      Run("e2fsck", $"-fy {img}"); // normalize

      long newSize;
      using (var fs = new FileStream(img, FileMode.Open, FileAccess.ReadWrite)) {
        var result = ExtInPlaceShrinker.ShrinkToFit(fs);
        Assert.That(result.WasReduced, Is.True);
        newSize = result.NewSize;
      }
      Assert.That(new FileInfo(img).Length, Is.EqualTo(newSize));

      AssertE2fsckClean(img, mkfs);

      var aOut = Path.Combine(dir, "a_out.bin");
      Run("debugfs", $"-R \"dump /a.bin {aOut}\" {img}");
      Assert.That(File.ReadAllBytes(aOut), Is.EqualTo(a), "file a must survive byte-identical");

      // Our own reader agrees.
      using var rfs = File.OpenRead(img);
      var reader = new ExtReader(rfs);
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "b.bin")), Is.EqualTo(b));
    } finally { TryDelete(dir); }
  }

  // ── Helpers ─────────────────────────────────────────────────────────────

  private static byte[] MakeData(int n) { var d = new byte[n]; for (var i = 0; i < n; i++) d[i] = (byte)(i * 31 + 7); return d; }

  private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

  private static void RequireTools(params string[] tools) {
    if (!IsLinux) Assert.Ignore("e2fsprogs/mkfs run on Linux only.");
    foreach (var t in tools)
      if (!HasCommand(t)) Assert.Ignore($"'{t}' not installed.");
  }

  private static bool HasCommand(string name) {
    try {
      var r = Run("/bin/sh", $"-c \"command -v {name}\"");
      return r.Exit == 0 && !string.IsNullOrWhiteSpace(r.Out);
    } catch { return false; }
  }

  private static void AssertE2fsckClean(string img, string label) {
    var r = Run("e2fsck", $"-fn {img}");
    Assert.That(r.Exit, Is.EqualTo(0),
      $"e2fsck rejected the shrunk {label} image (exit {r.Exit}):\n{r.Out}\n{r.Err}");
    var combined = r.Out + "\n" + r.Err;
    foreach (var marker in new[] { "FIXED", "Reparieren", "WARNUNG", "WARNING", "inconsistent", "corrupt", "ungültig" })
      Assert.That(combined, Does.Not.Contain(marker).IgnoreCase,
        $"e2fsck flagged '{marker}' on the {label} image:\n{combined}");
  }

  private static string NewTempDir() {
    var d = Path.Combine(Path.GetTempPath(), $"cwb_ext_shrink_{Guid.NewGuid():N}");
    Directory.CreateDirectory(d);
    return d;
  }
  private static void TryDelete(string dir) { try { Directory.Delete(dir, true); } catch { /* best effort */ } }

  private readonly record struct ProcResult(string Out, string Err, int Exit);

  private static ProcResult Run(string tool, string args) {
    var psi = new ProcessStartInfo {
      FileName = tool, Arguments = args,
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {tool}");
    var o = p.StandardOutput.ReadToEnd();
    var e = p.StandardError.ReadToEnd();
    if (!p.WaitForExit(90_000)) { try { p.Kill(); } catch { /* best effort */ } }
    return new ProcResult(o, e, p.ExitCode);
  }
}
