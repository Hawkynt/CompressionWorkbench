using System.Diagnostics;
using System.Runtime.InteropServices;
using Compression.Registry;
using FileSystem.F2fs;

namespace Compression.Tests.F2fs;

/// <summary>
/// External-tool gate for the post-leaf-only F2FS modifier scope. Every test
/// builds a fresh F2FS image, exercises a non-trivial mutation pattern
/// (overflow into block-based dentries, NAT journal overflow, mixed add/remove),
/// writes the mutated bytes to a temp file, and runs the real Linux
/// <c>fsck.f2fs</c> against it. Exit 0 with no <c>ERROR</c> / <c>corrupted</c>
/// in the output is the pass criterion — anything else means the writer and
/// reader are mutually wrong at the same offsets (the XFS trap) and the gate
/// rejects.
/// </summary>
/// <remarks>
/// The tests <see cref="Assert.Ignore(string)"/> cleanly when WSL or
/// <c>fsck.f2fs</c> are not available so they never fail on environment.
/// Install: <c>wsl --install</c> (Admin PowerShell), then inside the distro:
/// <c>sudo apt-get install -y f2fs-tools</c>.
/// </remarks>
[TestFixture]
[Category("ExternalInterop")]
public class F2fsPostMutationExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_f2fs_post_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image with one seed file.
  // ── When ───────────────────────────────────────────────────────────────
  // 200 files are added via F2fsFormatDescriptor.Add (overflowing both the
  // 38-entry NAT journal and the 182-slot inline-dentry region — so the
  // image now uses block-based dentry storage and the journal-falls-through
  // path).
  // ── Then ──────────────────────────────────────────────────────────────
  // real fsck.f2fs accepts the mutated image as structurally consistent.
  [Test]
  public void PostAdd_PassesFsckF2fs() {
    RequireWslF2fsTool();

    var f2fs = new F2fsWriter();
    f2fs.AddFile("seed.txt", "s"u8.ToArray());
    using var img = new MemoryStream();
    img.Write(f2fs.Build());

    var m = (IArchiveModifiable)new F2fsFormatDescriptor();
    for (var i = 0; i < 200; ++i)
      m.Add(img, [ArchiveInputInfo.InMemory($"add-{i:D4}.bin",
        new byte[] { (byte)i, (byte)(i >> 8), 0x55 })]);

    var imgPath = Path.Combine(this._tmpDir, "f2fs_postadd.img");
    File.WriteAllBytes(imgPath, img.ToArray());

    AssertFsckClean(imgPath, "PostAdd");
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image with one seed + 200 added files (block-based dentry
  // storage in use).
  // ── When ───────────────────────────────────────────────────────────────
  // 100 of those added files are removed (mixed across the block-based
  // dentry directory).
  // ── Then ──────────────────────────────────────────────────────────────
  // fsck.f2fs accepts the result — confirming Remove correctly clears
  // bitmap bits, on-disk NAT, and SIT valid_map across the regular dentry
  // block layout.
  [Test]
  public void PostRemove_PassesFsckF2fs() {
    RequireWslF2fsTool();

    var f2fs = new F2fsWriter();
    f2fs.AddFile("seed.txt", "s"u8.ToArray());
    using var img = new MemoryStream();
    img.Write(f2fs.Build());

    var m = (IArchiveModifiable)new F2fsFormatDescriptor();
    var added = new List<string>();
    for (var i = 0; i < 200; ++i) {
      var n = $"rm-{i:D4}.bin";
      m.Add(img, [ArchiveInputInfo.InMemory(n, new byte[] { (byte)i, 0xBB })]);
      added.Add(n);
    }
    // Remove every other one — 100 deletions spread across the block-dentry directory.
    var toRemove = added.Where((_, i) => i % 2 == 0).ToArray();
    m.Remove(img, toRemove);

    var imgPath = Path.Combine(this._tmpDir, "f2fs_postremove.img");
    File.WriteAllBytes(imgPath, img.ToArray());

    AssertFsckClean(imgPath, "PostRemove");
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image with a seed file.
  // ── When ───────────────────────────────────────────────────────────────
  // 200 adds, then 100 removes (the even ones), then another 50 adds —
  // hitting both inline→block conversion AND every overflow/refill path.
  // ── Then ──────────────────────────────────────────────────────────────
  // fsck.f2fs accepts the final image.
  [Test]
  public void PostMixed_PassesFsckF2fs() {
    RequireWslF2fsTool();

    var f2fs = new F2fsWriter();
    f2fs.AddFile("seed.txt", "s"u8.ToArray());
    using var img = new MemoryStream();
    img.Write(f2fs.Build());

    var m = (IArchiveModifiable)new F2fsFormatDescriptor();
    var firstWave = new List<string>();
    for (var i = 0; i < 200; ++i) {
      var n = $"mix-{i:D4}.bin";
      m.Add(img, [ArchiveInputInfo.InMemory(n, new byte[] { (byte)i, 0x77 })]);
      firstWave.Add(n);
    }
    m.Remove(img, firstWave.Where((_, i) => i % 2 == 0).ToArray());
    for (var i = 0; i < 50; ++i)
      m.Add(img, [ArchiveInputInfo.InMemory($"post-{i:D3}.bin",
        new byte[] { (byte)i, 0x99 })]);

    var imgPath = Path.Combine(this._tmpDir, "f2fs_postmixed.img");
    File.WriteAllBytes(imgPath, img.ToArray());

    AssertFsckClean(imgPath, "PostMixed");
  }

  // ────────────────────────────────────────────────────────────────────────
  // WSL plumbing
  // ────────────────────────────────────────────────────────────────────────

  private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

  private static void RequireWslF2fsTool() {
    if (!WslAvailable())
      Assert.Ignore("WSL not installed — install via `wsl --install` (Admin PowerShell).");
    if (!WslHasTool("fsck.f2fs"))
      Assert.Ignore("fsck.f2fs not installed in WSL — run `sudo apt-get install -y f2fs-tools` inside WSL.");
  }

  private static bool WslAvailable() {
    if (!IsWindows) return true;   // POSIX host: the machine itself runs the Linux tools
    try {
      var r = RunExact("wsl", "--status");
      return r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut);
    } catch {
      return false;
    }
  }

  private static bool WslHasTool(string tool) {
    var r = RunWsl($"command -v {tool}");
    return r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut);
  }

  private static (string StdOut, string StdErr, int ExitCode) RunWsl(string linuxCommand) {
    var dqEscaped = linuxCommand.Replace("\"", "\\\"");
    if (!IsWindows)
      return RunExact("/bin/bash", $"-c \"{dqEscaped}\"");
    return RunExact("wsl", $"-e bash -c \"{dqEscaped}\"");
  }

  private static string WinToWsl(string winPath) {
    if (string.IsNullOrEmpty(winPath)) return winPath;
    var full = Path.GetFullPath(winPath);
    if (full.Length < 2 || full[1] != ':') return full.Replace('\\', '/');
    var drive = char.ToLowerInvariant(full[0]);
    var tail = full[2..].Replace('\\', '/');
    return $"'/mnt/{drive}{tail}'";
  }

  private static (string StdOut, string StdErr, int ExitCode) RunExact(string fileName, string args) {
    var psi = new ProcessStartInfo {
      FileName = fileName, Arguments = args,
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    if (!p.WaitForExit(60_000)) {
      try { p.Kill(); } catch { /* best effort */ }
    }
    return (stdout, stderr, p.ExitCode);
  }

  // Runs `fsck.f2fs -f --dry-run` against the image inside WSL. Exit 0 means
  // the image is structurally consistent. The XFS trap (writer + reader
  // mutually wrong at same offsets that self-round-trips) is exactly what
  // this gate catches. We additionally check for [Fail] markers in the
  // [FSCK] summary lines — fsck prints `[Ok..]` for each pass and `[Fail]`
  // when a particular check trips, so a clean run shows no `[Fail]`.
  private static void AssertFsckClean(string winImgPath, string scenarioLabel) {
    var wslPath = WinToWsl(winImgPath);
    var result = RunWsl($"fsck.f2fs -f --dry-run {wslPath}");
    var combined = (result.StdOut ?? string.Empty) + "\n" + (result.StdErr ?? string.Empty);

    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.f2fs rejected the {scenarioLabel} image (exit {result.ExitCode}):\n"
      + $"stdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(combined, Does.Not.Contain("[Fail]"),
      $"fsck.f2fs reported a check failure for the {scenarioLabel} image:\n{combined}");
    // "Inconsistent" / "inconsistency" — fsck's stronger language for serious issues.
    Assert.That(combined, Does.Not.Contain("Inconsistent").IgnoreCase,
      $"fsck.f2fs reports inconsistency for the {scenarioLabel} image:\n{combined}");
  }
}
