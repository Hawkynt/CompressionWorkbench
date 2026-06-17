using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FileSystem.Jfs;

namespace Compression.Tests.Jfs;

/// <summary>
/// Spill accounting for the inline dtroot. The dinode's inline dtroot holds
/// exactly 8 entry slots (indices 1..8). Each child consumes one head slot plus
/// one continuation slot per name chunk beyond the 11-char head capacity, so the
/// inline-vs-external decision must sum the per-name slot cost, not count
/// children: a directory packed with multi-slot long names can exhaust the
/// 8-slot budget while still holding far fewer than 8 entries. The old
/// child-count threshold left such directories inline, producing an overflowing
/// dtroot. These tests pin the slot-based decision via the on-disk dtroot flag
/// (0x83 inline-leaf vs 0x85 router-promoted), a full <see cref="JfsReader"/>
/// round-trip, and — when available — a real <c>fsck.jfs -fnv</c> clean gate.
/// </summary>
[TestFixture]
public class JfsInlineSpillSlotAccountingTests {

  // ── slot geometry (mirrors JfsWriter.EntrySlotCost) ─────────────────────
  private const int DtHeadNameChars = 11;   // DTLHDRDATALEN
  private const int DtSlotNameChars = 15;    // DTSLOTDATALEN
  private const int InlineDirEntries = 8;    // inline dtroot entry slots (1..8)

  private static int SlotsForName(string name) =>
    name.Length <= DtHeadNameChars
      ? 1
      : 1 + (name.Length - DtHeadNameChars + DtSlotNameChars - 1) / DtSlotNameChars;

  // ── on-disk root dtroot flag location ───────────────────────────────────
  // The fileset inode table starts at block 30 (FsitBlock). ROOT_I is inode 2,
  // each dinode is 512 bytes, and the dtroot/di_data union begins at +224
  // (XtreeDataOffset). The dtree-page header flag byte sits at +16 inside that
  // union: 0x83 = DXD_INDEX|BT_ROOT|BT_LEAF (inline leaf dtroot), 0x85 =
  // DXD_INDEX|BT_ROOT|BT_INTERNAL (router-promoted to an external dtree).
  private const int RootDtRootFlagOffset =
    30 * JfsWriter.BlockSize + JfsWriter.RootIno * JfsWriter.InodeSize + JfsWriter.XtreeDataOffset + 16;
  private const byte InlineLeafFlag = 0x83;
  private const byte RouterFlag = 0x85;

  // ── image build helper ──────────────────────────────────────────────────
  private static byte[] BuildImage(IEnumerable<(string Name, byte[] Data)> files) {
    var w = new JfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static byte RootFlag(byte[] img) => img[RootDtRootFlagOffset];

  // ── WSL fsck gate (mirrors JfsPostMutationExternalTests) ────────────────
  private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

  private static (string StdOut, string StdErr, int ExitCode) RunExact(string exe, string args, int timeoutMs = 90_000) {
    var psi = new ProcessStartInfo {
      FileName = exe, Arguments = args, RedirectStandardOutput = true,
      RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(); } catch { /* best effort */ } }
    return (stdout, stderr, proc.ExitCode);
  }

  private static (string StdOut, string StdErr, int ExitCode) RunWsl(string linuxCommand) {
    var dq = linuxCommand.Replace("\"", "\\\"");
    return IsWindows ? RunExact("wsl", $"-e bash -c \"{dq}\"") : RunExact("/bin/bash", $"-c \"{dq}\"");
  }

  private static string WinToWsl(string winPath) {
    var full = Path.GetFullPath(winPath);
    if (full.Length < 2 || full[1] != ':') return full.Replace('\\', '/');
    var drive = char.ToLowerInvariant(full[0]);
    return $"'/mnt/{drive}{full[2..].Replace('\\', '/')}'";
  }

  private static bool _wslChecked, _wslAvailable, _fsckChecked, _fsckAvailable;

  private static bool WslAvailable {
    get {
      if (_wslChecked) return _wslAvailable;
      _wslChecked = true;
      if (!IsWindows) return _wslAvailable = true;
      try { _wslAvailable = RunExact("wsl", "--status", timeoutMs: 5_000).ExitCode == 0; }
      catch { _wslAvailable = false; }
      return _wslAvailable;
    }
  }

  private static bool FsckJfsAvailable {
    get {
      if (_fsckChecked) return _fsckAvailable;
      _fsckChecked = true;
      if (!WslAvailable) return _fsckAvailable = false;
      var r = RunWsl("command -v fsck.jfs");
      return _fsckAvailable = r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut);
    }
  }

  private static void RequireFsckJfs() {
    if (!WslAvailable)
      Assert.Ignore("WSL not available; cannot run fsck.jfs. Enable WSL + install Ubuntu, then `sudo apt install -y jfsutils`.");
    if (!FsckJfsAvailable)
      Assert.Ignore("fsck.jfs not installed in the default WSL distro. Run inside WSL: `sudo apt install -y jfsutils`.");
  }

  private static void AssertFsckClean(string imagePath, string label) {
    var result = RunWsl($"fsck.jfs -fnv {WinToWsl(imagePath)}");
    var report = result.StdOut + result.StdErr;
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"[{label}] fsck.jfs rejected image (exit {result.ExitCode}):\n--- stdout ---\n{result.StdOut}\n--- stderr ---\n{result.StdErr}");
    var lower = report.ToLowerInvariant();
    Assert.That(lower, Does.Contain("clean").And.Not.Contain("errors found"),
      $"[{label}] fsck.jfs report did not confirm clean state:\n{report}");
    Assert.That(lower, Does.Not.Contain("cannot continue"), $"[{label}] fsck.jfs aborted:\n{report}");
  }

  // ── boundary: exactly 8 slots stays inline; one more slot spills ────────
  // Four 12-char names cost 2 slots each (head + 1 continuation) = 8 slots
  // exactly, filling the inline dtroot with only four children. The old
  // child-count rule (4 <= 8) kept this inline too — but so does the
  // slot-based rule (8 <= 8), so it must stay inline (flag 0x83).
  [Test, Category("BoundaryValue")]
  public void Inline_ExactlyEightSlots_FourLongNames_StaysInline() {
    var names = new[] { "alpha-name01", "bravo-name02", "delta-name03", "gamma-name04" };
    foreach (var n in names) Assert.That(n.Length, Is.EqualTo(12)); // 2 slots each
    Assert.That(names.Sum(SlotsForName), Is.EqualTo(8), "fixture must sum to exactly 8 inline slots");

    var img = BuildImage(names.Select(n => (n, Encoding.UTF8.GetBytes(n))));

    Assert.That(RootFlag(img), Is.EqualTo(InlineLeafFlag), "8-slot directory must stay inline");
    AssertRoundTrips(img, names);
  }

  // Three 12-char names (2 slots each = 6) plus one 27-char name (3 slots) =
  // 9 slots across only FOUR children — one slot past the inline budget. The
  // old child-count rule (4 <= 8) wrongly kept this inline and overflowed the
  // 8-slot dtroot; the slot-based rule (9 > 8) must promote it to a router.
  [Test, Category("BoundaryValue")]
  public void Inline_NineSlots_FourChildren_SpillsToExternal() {
    var longName = new string('z', 27); // 1 + ceil((27-11)/15) = 3 slots
    Assert.That(SlotsForName(longName), Is.EqualTo(3));
    var names = new[] { "alpha-name01", "bravo-name02", "delta-name03", longName };
    Assert.That(names.Sum(SlotsForName), Is.EqualTo(9), "fixture must sum to 9 slots with only 4 children");

    var img = BuildImage(names.Select(n => (n, Encoding.UTF8.GetBytes(n))));

    Assert.That(RootFlag(img), Is.EqualTo(RouterFlag),
      "9-slot directory (4 children) must spill — child count alone would keep it inline");
    AssertRoundTrips(img, names);
  }

  // ── packed directory: many multi-slot long names, total slots >> 8 ──────
  // Six 39-char names cost 1 + ceil((39-11)/15) = 3 slots each = 18 slots, more
  // than double the inline budget while holding fewer than 8 children. Must
  // spill to an external dtree, round-trip, and pass fsck.jfs.
  private static (string Name, byte[] Data)[] PackedLongNameInputs() {
    var inputs = new List<(string, byte[])>();
    for (var i = 0; i < 6; i++) {
      var name = $"packed-long-directory-entry-number-{i:D2}"; // 39 chars
      Assert.That(SlotsForName(name), Is.EqualTo(3), "each packed name must cost 3 inline slots");
      inputs.Add((name, Encoding.UTF8.GetBytes($"payload-for-{i}")));
    }
    Assert.That(inputs.Sum(t => SlotsForName(t.Item1)), Is.EqualTo(18), "fixture slots must be 18 (>> 8)");
    return inputs.ToArray();
  }

  [Test, Category("RoundTrip")]
  public void PackedLongNameDir_SpillsAndRoundTrips() {
    var inputs = PackedLongNameInputs();
    var img = BuildImage(inputs);

    Assert.That(RootFlag(img), Is.EqualTo(RouterFlag),
      "slot-packed long-name directory must spill to an external dtree");
    AssertRoundTrips(img, inputs.Select(t => t.Name).ToArray());

    // Content intact through the external dtree leaves.
    using var ms = new MemoryStream(img);
    var r = new JfsReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
    foreach (var (name, data) in inputs)
      Assert.That(files[name], Is.EqualTo(data), $"content intact for {name}");
  }

  [Test, Category("ExternalInterop")]
  public void PackedLongNameDir_PassesFsckJfs() {
    RequireFsckJfs();

    var img = BuildImage(PackedLongNameInputs());
    Assert.That(RootFlag(img), Is.EqualTo(RouterFlag), "packed dir must have spilled before fsck");

    var dir = Path.Combine(Path.GetTempPath(), $"cwb_jfs_spill_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var imgPath = Path.Combine(dir, "packed_longnames.jfs");
      File.WriteAllBytes(imgPath, img);
      AssertFsckClean(imgPath, "PackedLongNameDir");
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  // ── shared round-trip assertion ─────────────────────────────────────────
  private static void AssertRoundTrips(byte[] img, string[] names) {
    using var ms = new MemoryStream(img);
    var r = new JfsReader(ms);
    var present = r.Entries.Where(e => !e.IsDirectory)
                           .Select(e => e.Name.Replace('\\', '/'))
                           .ToHashSet();
    Assert.That(present.Count, Is.EqualTo(names.Length), "every entry round-trips");
    foreach (var n in names)
      Assert.That(present.Contains(n), Is.True, $"entry present at exact path: {n}");
  }
}
