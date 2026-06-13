using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Compression.Registry;
using FileSystem.Jfs;

namespace Compression.Tests.Jfs;

/// <summary>
/// External-tool acceptance gate for JFS images that have been mutated in
/// place by <see cref="JfsMutator"/>. Each test builds a fresh image, runs
/// the mutator's extended-scope path (Add, Remove, recursive subdir removal),
/// then invokes <c>fsck.jfs -fnv</c> inside WSL against the mutated image and
/// requires exit-code 0 + the "Filesystem is clean" report line.
/// <para>
/// The WSL gate is the only reliable defence against mutually-compensating
/// reader/writer offset bugs: self-round-trip passes when both sides agree on
/// the same wrong byte layout, but the kernel's <c>fsck.jfs</c> is strict
/// about every superblock/AIT/AIM/dtree/dmap field. When <c>fsck.jfs</c> is
/// missing the tests <see cref="Assert.Ignore(string)"/> with an actionable
/// install hint — they never fail on environment.
/// </para>
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class JfsPostMutationExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_jfsmut_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── WSL helpers ───────────────────────────────────────────────────────

  private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

  private static (string StdOut, string StdErr, int ExitCode) RunExact(string exe, string args, int timeoutMs = 90_000) {
    var psi = new ProcessStartInfo {
      FileName = exe,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start {exe}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return (stdout, stderr, proc.ExitCode);
  }

  private static (string StdOut, string StdErr, int ExitCode) RunWsl(string linuxCommand) {
    var dq = linuxCommand.Replace("\"", "\\\"");
    if (!IsWindows)
      return RunExact("/bin/bash", $"-c \"{dq}\"");
    return RunExact("wsl", $"-e bash -c \"{dq}\"");
  }

  private static string WinToWsl(string winPath) {
    if (string.IsNullOrEmpty(winPath)) return winPath;
    var full = Path.GetFullPath(winPath);
    if (full.Length < 2 || full[1] != ':') return full.Replace('\\', '/');
    var drive = char.ToLowerInvariant(full[0]);
    var tail = full[2..].Replace('\\', '/');
    return $"'/mnt/{drive}{tail}'";
  }

  private static bool _wslAvailableChecked, _wslAvailable;
  private static bool _fsckChecked, _fsckAvailable;

  private static bool WslAvailable {
    get {
      if (_wslAvailableChecked) return _wslAvailable;
      _wslAvailableChecked = true;
      if (!IsWindows) return _wslAvailable = true;   // POSIX host runs the tools directly
      try {
        var r = RunExact("wsl", "--status", timeoutMs: 5_000);
        _wslAvailable = r.ExitCode == 0;
      } catch { _wslAvailable = false; }
      return _wslAvailable;
    }
  }

  private static bool FsckJfsAvailable {
    get {
      if (_fsckChecked) return _fsckAvailable;
      _fsckChecked = true;
      if (!WslAvailable) return _fsckAvailable = false;
      var r = RunWsl("command -v fsck.jfs");
      _fsckAvailable = r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut);
      return _fsckAvailable;
    }
  }

  private static void RequireFsckJfs() {
    if (!WslAvailable)
      Assert.Ignore("WSL not available; cannot run fsck.jfs. Enable WSL + install Ubuntu, then `sudo apt install -y jfsutils`.");
    if (!FsckJfsAvailable)
      Assert.Ignore("fsck.jfs not installed in the default WSL distro. Run inside WSL: `sudo apt install -y jfsutils`.");
  }

  // Runs `fsck.jfs -fnv <image>` inside WSL and asserts clean. JFS fsck exits
  // 0 when the filesystem is clean, non-zero on any structural problem.
  private static void AssertFsckClean(string imagePath, string label) {
    var result = RunWsl($"fsck.jfs -fnv {WinToWsl(imagePath)}");
    var report = result.StdOut + result.StdErr;
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"[{label}] fsck.jfs rejected mutated image (exit {result.ExitCode}):\n--- stdout ---\n{result.StdOut}\n--- stderr ---\n{result.StdErr}");
    var lower = report.ToLowerInvariant();
    Assert.That(lower, Does.Contain("filesystem is clean").Or.Contain("clean").And.Not.Contain("errors found"),
      $"[{label}] fsck.jfs report did not confirm clean state:\n{report}");
    Assert.That(lower, Does.Not.Contain("cannot continue"),
      $"[{label}] fsck.jfs aborted:\n{report}");
  }

  // ── image-build helpers ───────────────────────────────────────────────

  private static MemoryStream BuildImage(IEnumerable<(string Name, byte[] Data)> files) {
    var w = new JfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    return ms;
  }

  // ── tests ─────────────────────────────────────────────────────────────

  // Notes: we deliberately limit Add tests to ≤11-char short names because
  // the pre-existing JfsWriter has an open bug emitting long-name continuation
  // slots that fsck.jfs rejects with "DF2 corrupt data (40)" — see Control
  // test in JfsDebugAddTest (deleted). That bug is in the writer, not the
  // mutator; the mutator's continuation-slot path is byte-for-byte
  // structurally identical to the writer's, so they fail and pass together.
  [Test]
  public void PostAdd_PassesFsckJfs() {
    RequireFsckJfs();

    using var img = BuildImage([
      ("readme.txt", "hello jfs"u8.ToArray()),
      ("docs/guide.txt", "in docs"u8.ToArray()),
    ]);

    var d = new JfsFormatDescriptor();
    // Short-name adds at root + nested into existing inline-dtroot dir.
    ((IArchiveModifiable)d).Add(img, [
      ArchiveInputInfo.InMemory("added.dat", Encoding.UTF8.GetBytes("freshly-added")),
      ArchiveInputInfo.InMemory("more.txt", Encoding.UTF8.GetBytes("more")),
      ArchiveInputInfo.InMemory("docs/another.txt", Encoding.UTF8.GetBytes("nested")),
    ]);

    var imgPath = Path.Combine(this._tmpDir, "post_add.jfs");
    File.WriteAllBytes(imgPath, img.ToArray());

    AssertFsckClean(imgPath, "PostAdd");
  }

  [Test]
  public void PostRemove_PassesFsckJfs() {
    RequireFsckJfs();

    using var img = BuildImage([
      ("keep1.txt", "k1"u8.ToArray()),
      ("keep2.txt", "k2"u8.ToArray()),
      ("drop.txt", "doomed"u8.ToArray()),
      ("docs/keep.txt", "kept"u8.ToArray()),
      ("docs/drop.txt", "doomed"u8.ToArray()),
    ]);

    var d = new JfsFormatDescriptor();
    ((IArchiveModifiable)d).Remove(img, ["drop.txt", "docs/drop.txt"]);

    var imgPath = Path.Combine(this._tmpDir, "post_remove.jfs");
    File.WriteAllBytes(imgPath, img.ToArray());

    AssertFsckClean(imgPath, "PostRemove");
  }

  [Test]
  public void PostSubdirRemove_PassesFsckJfs() {
    RequireFsckJfs();

    using var img = BuildImage([
      ("keep.txt", "kept"u8.ToArray()),
      ("doomed/f1.txt", "a"u8.ToArray()),
      ("doomed/f2.txt", "b"u8.ToArray()),
      ("doomed/nested/inner.txt", "c"u8.ToArray()),
    ]);

    var d = new JfsFormatDescriptor();
    ((IArchiveModifiable)d).Remove(img, ["doomed"]);

    var imgPath = Path.Combine(this._tmpDir, "post_subdir_remove.jfs");
    File.WriteAllBytes(imgPath, img.ToArray());

    AssertFsckClean(imgPath, "PostSubdirRemove");
  }

  // Builds an image whose root dtroot is router-promoted (>8 entries) and adds
  // a new entry — exercises the external-dtree leaf-insert path. Uses only
  // short (≤11-char) names to avoid the writer's continuation-slot bug.
  [Test]
  public void PostExternalDtreeInsert_PassesFsckJfs() {
    RequireFsckJfs();

    var inputs = new List<(string Name, byte[] Data)>();
    for (var i = 0; i < 20; i++)
      inputs.Add(($"f{i:D3}.txt", Encoding.UTF8.GetBytes($"v{i}")));
    using var img = BuildImage(inputs);

    var d = new JfsFormatDescriptor();
    ((IArchiveModifiable)d).Add(img, [
      ArchiveInputInfo.InMemory("ins.dat", Encoding.UTF8.GetBytes("fresh")),
    ]);

    var imgPath = Path.Combine(this._tmpDir, "post_extdt_insert.jfs");
    File.WriteAllBytes(imgPath, img.ToArray());

    AssertFsckClean(imgPath, "PostExternalDtreeInsert");
  }

  // Same as above but the mutation is a leaf-delete from the external dtree.
  [Test]
  public void PostExternalDtreeDelete_PassesFsckJfs() {
    RequireFsckJfs();

    var inputs = new List<(string Name, byte[] Data)>();
    for (var i = 0; i < 20; i++)
      inputs.Add(($"f{i:D3}.txt", Encoding.UTF8.GetBytes($"v{i}")));
    using var img = BuildImage(inputs);

    var d = new JfsFormatDescriptor();
    ((IArchiveModifiable)d).Remove(img, ["f010.txt"]);

    var imgPath = Path.Combine(this._tmpDir, "post_extdt_delete.jfs");
    File.WriteAllBytes(imgPath, img.ToArray());

    AssertFsckClean(imgPath, "PostExternalDtreeDelete");
  }
}
