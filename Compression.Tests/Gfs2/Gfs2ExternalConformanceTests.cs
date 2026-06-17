using FileSystem.Gfs2;

namespace Compression.Tests.Gfs2;

/// <summary>
/// Reverse-conformance tests for the GFS2 reader: build a real filesystem image
/// with <c>mkfs.gfs2</c> (gfs2-utils, installed in WSL), confirm
/// <c>fsck.gfs2 -n</c> accepts it, then point <see cref="Gfs2Reader"/> at that
/// exact image and assert it decodes the on-disk layout that real tooling
/// produces. This is the gate that caught the 16-vs-24-byte
/// <c>gfs2_meta_header</c> bug — self-round-trip alone passed because the old
/// reader and the old synthetic builder were mutually wrong at the same offsets.
/// <para>
/// Each test skips via <see cref="Assert.Ignore(string)"/> when WSL or the
/// gfs2-utils binaries are absent, with an actionable install hint. Nothing
/// writes outside <see cref="Path.GetTempPath"/>.
/// </para>
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public class Gfs2ExternalConformanceTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_gfs2_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static void RequireGfs2Utils() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. Install a WSL distro, then inside it run " +
                    "`sudo apt install -y gfs2-utils` to get mkfs.gfs2 / fsck.gfs2.");
    if (!FsInteropToolbox.WslHasTool("mkfs.gfs2") || !FsInteropToolbox.WslHasTool("fsck.gfs2"))
      Assert.Ignore("gfs2-utils not found in WSL. Install with `sudo apt install -y gfs2-utils`.");
  }

  /// <summary>
  /// Creates a fresh standalone (lock_nolock, single-journal) GFS2 image at
  /// <paramref name="imgPath"/> via mkfs.gfs2. The <c>-O</c> flag skips the
  /// interactive overwrite prompt; the device is pre-sized with truncate so
  /// mkfs has a block count to work with.
  /// </summary>
  private static (string StdOut, string StdErr, int ExitCode) MakeGfs2(string imgPath, long sizeBytes = 64L * 1024 * 1024) {
    var wsl = FsInteropToolbox.WinToWsl(imgPath);
    return FsInteropToolbox.RunWsl(
      $"truncate -s {sizeBytes} {wsl} && mkfs.gfs2 -p lock_nolock -j 1 -O {wsl}");
  }

  // ── Forward gate: mkfs output must be fsck-clean (reference sanity) ──

  [Test, Category("HappyPath")]
  public void MkfsGfs2_Image_IsAcceptedByFsck() {
    RequireGfs2Utils();
    var imgPath = Path.Combine(this._tmpDir, "ref.gfs2");
    var mk = MakeGfs2(imgPath);
    Assert.That(mk.ExitCode, Is.EqualTo(0), $"mkfs.gfs2 failed:\nstdout:{mk.StdOut}\nstderr:{mk.StdErr}");

    var fsck = FsInteropToolbox.RunWsl($"fsck.gfs2 -n {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(fsck.ExitCode, Is.EqualTo(0),
      $"fsck.gfs2 -n rejected mkfs.gfs2 output:\nstdout:{fsck.StdOut}\nstderr:{fsck.StdErr}");
    Assert.That(fsck.StdOut, Does.Contain("complete").IgnoreCase,
      $"fsck.gfs2 did not report completion:\n{fsck.StdOut}");
  }

  // ── Reverse gate: our reader must decode real mkfs.gfs2 layout ──

  [Test, Category("HappyPath")]
  public void Reader_Decodes_RealMkfsGfs2_Superblock() {
    RequireGfs2Utils();
    var imgPath = Path.Combine(this._tmpDir, "read.gfs2");
    var mk = MakeGfs2(imgPath);
    Assert.That(mk.ExitCode, Is.EqualTo(0), $"mkfs.gfs2 failed:\n{mk.StdErr}");

    using var fs = File.OpenRead(imgPath);
    var r = new Gfs2Reader(fs);

    Assert.Multiple(() => {
      Assert.That(r.SuperblockValid, Is.True,
        "Gfs2Reader failed to validate a real mkfs.gfs2 superblock (meta-header size / offsets).");
      Assert.That(r.BlockSize, Is.EqualTo(4096u), "Default mkfs.gfs2 block size is 4096.");
      Assert.That(r.BlockSizeShift, Is.EqualTo(12u), "1 << 12 == 4096.");
      Assert.That(r.LockProto, Is.EqualTo("lock_nolock"),
        "We created the FS with -p lock_nolock.");
      // mkfs.gfs2 records the master + root system inodes; both addresses are
      // far from zero on a real image. A bogus offset read would yield 0.
      Assert.That(r.MasterInodeBlock, Is.GreaterThan(0UL), "Master dir inode address must be non-zero.");
      Assert.That(r.RootInodeBlock, Is.GreaterThan(0UL), "Root dir inode address must be non-zero.");
      Assert.That(r.RootInodeBlock, Is.Not.EqualTo(r.MasterInodeBlock),
        "Root and master are distinct system inodes.");
      Assert.That(r.UuidHex, Has.Length.EqualTo(32), "mkfs.gfs2 always stamps a 16-byte UUID.");
      Assert.That(r.UuidHex, Is.Not.EqualTo(new string('0', 32)),
        "UUID must not be all-zero on a real image (would mean a wrong offset).");
    });
  }

  [Test, Category("HappyPath")]
  public void Reader_Walks_RealRootDirectory_WithoutError() {
    RequireGfs2Utils();
    var imgPath = Path.Combine(this._tmpDir, "walk.gfs2");
    var mk = MakeGfs2(imgPath);
    Assert.That(mk.ExitCode, Is.EqualTo(0), $"mkfs.gfs2 failed:\n{mk.StdErr}");

    using var fs = File.OpenRead(imgPath);
    var r = new Gfs2Reader(fs);
    Assert.That(r.SuperblockValid, Is.True);

    // A freshly-made GFS2 root holds only "." and "..", which the walker skips
    // by design — so a real image yields zero user entries, and crucially the
    // root-dinode walk reads as a stuffed (di_height == 0) directory without
    // throwing. A wrong di_height offset would have mis-typed the directory.
    Assert.That(r.Entries, Is.Empty,
      "Fresh mkfs.gfs2 root has only '.'/'..' which the walker skips.");
  }

  [Test, Category("Boundary")]
  public void Reader_Decodes_4kAnd1k_BlockSizes() {
    RequireGfs2Utils();
    foreach (var bsize in new[] { 1024u, 4096u }) {
      var imgPath = Path.Combine(this._tmpDir, $"bsize_{bsize}.gfs2");
      var wsl = FsInteropToolbox.WinToWsl(imgPath);
      var mk = FsInteropToolbox.RunWsl(
        $"truncate -s {64L * 1024 * 1024} {wsl} && mkfs.gfs2 -p lock_nolock -j 1 -b {bsize} -O {wsl}");
      // Some kernels/mkfs builds reject odd block sizes; skip that size cleanly.
      if (mk.ExitCode != 0) {
        TestContext.Out.WriteLine($"mkfs.gfs2 -b {bsize} unsupported here, skipping: {mk.StdErr.Trim()}");
        continue;
      }

      using var fs = File.OpenRead(imgPath);
      var r = new Gfs2Reader(fs);
      Assert.That(r.SuperblockValid, Is.True, $"Reader rejected mkfs.gfs2 -b {bsize} image.");
      Assert.That(r.BlockSize, Is.EqualTo(bsize), $"Reader misread block size for -b {bsize}.");
    }
  }
}
