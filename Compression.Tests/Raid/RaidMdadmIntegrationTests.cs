using System.Diagnostics;
using System.Security.Cryptography;
using Compression.Core.DiskImage.Raid;
using FileSystem.Ext;

namespace Compression.Tests.Raid;

/// <summary>
/// End-to-end proof of the mdraid assembler against the real Linux tools. Each case
/// (1) builds an array with <c>mdadm --create</c> over loop-mounted sparse files,
/// (2) lays an ext2 filesystem on the md device and writes a known
/// <c>RAIDPROOF.txt</c>, (3) tears mdadm/loops down, then (4) re-opens the raw member
/// files with <see cref="RaidAssembler"/> — <em>without mdadm</em> — hands the assembled
/// stream to <see cref="ExtReader"/> and asserts the file reads back byte-identical.
/// RAID5 is additionally proven with one member omitted (XOR reconstruction).
///
/// <para>Everything is probed and the fixture <see cref="Assert.Ignore(string)"/>s
/// cleanly when Linux, sudo, mdadm, losetup, mke2fs or mount are unavailable — it never
/// fails on environment. The sudo password comes from <c>CWB_SUDO_PASSWORD</c> (default
/// <c>1234</c>); set <c>CWB_SKIP_SUDO=1</c> to force-skip.</para>
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
[Category("OsIntegration")]
public sealed class RaidMdadmIntegrationTests {
  private string _tmpDir = null!;
  private readonly List<string> _loops = [];
  private readonly List<string> _mdDevices = [];
  private readonly List<string> _mounts = [];

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_raid_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    foreach (var m in this._mounts) RunSudo($"umount {Shq(m)}");
    foreach (var md in this._mdDevices) RunSudo($"mdadm --stop {Shq(md)}");
    foreach (var l in this._loops) RunSudo($"losetup -d {Shq(l)}");
    this._mounts.Clear(); this._mdDevices.Clear(); this._loops.Clear();
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── RAID0 (2 disks, metadata 1.2) ─────────────────────────────────────
  [Test]
  public void Raid0_Metadata12_AssemblesAndReadsProofFileByteExact() {
    RequireTools();
    var (members, payload) = this.CreateArrayAndWriteProof(level: "0", disks: 2, chunkKb: 64, metadata: "1.2");
    AssertProofReadsBack(members, payload);
  }

  // ── RAID5 (3 disks, metadata 1.2) — full + degraded ───────────────────
  [Test]
  public void Raid5_Metadata12_AssemblesAndReadsProofFileByteExact() {
    RequireTools();
    var (members, payload) = this.CreateArrayAndWriteProof(level: "5", disks: 3, chunkKb: 64, metadata: "1.2");
    AssertProofReadsBack(members, payload);
  }

  [Test]
  public void Raid5_Degraded_OneMemberOmitted_ReconstructsProofFileByteExact() {
    RequireTools();
    var (members, payload) = this.CreateArrayAndWriteProof(level: "5", disks: 3, chunkKb: 64, metadata: "1.2");
    // Omit the last member -> the assembler must reconstruct it by XOR.
    var degraded = members.Take(members.Count - 1).ToList();
    AssertProofReadsBack(degraded, payload);
  }

  // ── RAID1 (2 disks, metadata 0.90) ────────────────────────────────────
  [Test]
  public void Raid1_Metadata090_AssemblesAndReadsProofFileByteExact() {
    RequireTools();
    var (members, payload) = this.CreateArrayAndWriteProof(level: "1", disks: 2, chunkKb: 0, metadata: "0.90");
    AssertProofReadsBack(members, payload);
  }

  // ── array construction via the real tools ──────────────────────────────
  private (List<string> Members, byte[] Payload) CreateArrayAndWriteProof(
      string level, int disks, int chunkKb, string metadata) {
    var members = new List<string>();
    var loops = new List<string>();
    for (var i = 0; i < disks; i++) {
      var path = Path.Combine(this._tmpDir, $"member{i}.img");
      var t = Run("/bin/bash", $"-c \"truncate -s 96M {Shq(path)}\"");
      Assert.That(t.ExitCode, Is.Zero, $"truncate failed: {t.StdErr}");
      members.Add(path);

      var lo = RunSudo($"losetup --find --show {Shq(path)}");
      Assert.That(lo.ExitCode, Is.Zero, $"losetup failed: {lo.StdErr}");
      var loop = lo.StdOut.Trim();
      Assert.That(loop, Does.StartWith("/dev/loop"));
      loops.Add(loop);
      this._loops.Add(loop);
    }

    var md = $"/dev/md/cwb_{Guid.NewGuid():N}"[..24];
    var chunkArg = chunkKb > 0 ? $"--chunk={chunkKb} " : "";
    var create = RunSudo(
      $"mdadm --create {Shq(md)} --level={level} --metadata={metadata} " +
      $"--raid-devices={disks} {chunkArg}--run {string.Join(' ', loops.Select(Shq))}",
      extraYes: true);
    Assert.That(create.ExitCode, Is.Zero,
      $"mdadm --create failed (exit {create.ExitCode}): {create.StdErr}");
    this._mdDevices.Add(md);

    // Wait for initial resync so parity is fully initialised (degraded proof needs it).
    RunSudo($"mdadm --wait {Shq(md)}");

    var mkfs = RunSudo($"mke2fs -F -q -t ext2 -b 1024 {Shq(md)}");
    Assert.That(mkfs.ExitCode, Is.Zero, $"mke2fs failed: {mkfs.StdErr}");

    // Deterministic proof payload spanning several chunks/stripes.
    var payload = new byte[300_000];
    RandomNumberGenerator.Fill(payload); // random but captured; we compare against these exact bytes
    var payloadPath = Path.Combine(this._tmpDir, "payload.bin");
    File.WriteAllBytes(payloadPath, payload);

    var mnt = Path.Combine(this._tmpDir, "mnt");
    Directory.CreateDirectory(mnt);
    var mount = RunSudo($"mount -o rw {Shq(md)} {Shq(mnt)}");
    Assert.That(mount.ExitCode, Is.Zero, $"mount failed: {mount.StdErr}");
    this._mounts.Add(mnt);

    var cp = RunSudo($"cp {Shq(payloadPath)} {Shq(Path.Combine(mnt, "RAIDPROOF.txt"))}");
    Assert.That(cp.ExitCode, Is.Zero, $"writing proof file failed: {cp.StdErr}");
    RunSudo("sync");

    var umount = RunSudo($"umount {Shq(mnt)}");
    Assert.That(umount.ExitCode, Is.Zero, $"umount failed: {umount.StdErr}");
    this._mounts.Remove(mnt);

    var stop = RunSudo($"mdadm --stop {Shq(md)}");
    Assert.That(stop.ExitCode, Is.Zero, $"mdadm --stop failed: {stop.StdErr}");
    this._mdDevices.Remove(md);

    foreach (var loop in loops) RunSudo($"losetup -d {Shq(loop)}");
    this._loops.RemoveAll(loops.Contains);

    return (members, payload);
  }

  /// <summary>
  /// Assembles the raw member files with our code (no mdadm), reads
  /// <c>RAIDPROOF.txt</c> through <see cref="ExtReader"/> and asserts byte equality.
  /// </summary>
  private static void AssertProofReadsBack(IReadOnlyList<string> memberPaths, byte[] expected) {
    using var assembled = RaidAssembler.TryAssemble(memberPaths);
    Assert.That(assembled, Is.Not.Null, "RaidAssembler failed to assemble the member files.");

    assembled!.Position = 0;
    var reader = new ExtReader(assembled);
    var entry = reader.Entries.FirstOrDefault(e => e.Name == "RAIDPROOF.txt");
    Assert.That(entry, Is.Not.Null, "RAIDPROOF.txt not found by the ext reader over the assembled stream.");

    var got = reader.Extract(entry!);
    Assert.That(got, Is.EqualTo(expected), "proof file bytes differ after RAID assembly.");
  }

  // ── environment probing ─────────────────────────────────────────────────
  private static void RequireTools() {
    if (!OperatingSystem.IsLinux())
      Assert.Ignore("mdraid integration requires a Linux host.");
    if (Environment.GetEnvironmentVariable("CWB_SKIP_SUDO") == "1")
      Assert.Ignore("CWB_SKIP_SUDO=1 set.");
    foreach (var tool in new[] { "mdadm", "losetup", "mke2fs", "mount" })
      if (!HasTool(tool))
        Assert.Ignore($"required tool '{tool}' is not installed.");
    if (RunSudo("true").ExitCode != 0)
      Assert.Ignore("sudo unavailable (set CWB_SUDO_PASSWORD, or CWB_SKIP_SUDO=1 to silence).");
  }

  // ── process helpers ─────────────────────────────────────────────────────
  private static string SudoPassword =>
    Environment.GetEnvironmentVariable("CWB_SUDO_PASSWORD") ?? "1234";

  private static bool HasTool(string tool) =>
    Run("/bin/bash", $"-c \"command -v {tool}\"").ExitCode == 0;

  private static string Shq(string s) => "'" + s.Replace("'", "'\\''") + "'";

  /// <summary>
  /// Runs <paramref name="command"/> via <c>sudo -S</c>. When <paramref name="extraYes"/>
  /// is set, several <c>y</c> answers follow the password on stdin so any interactive
  /// mdadm confirmation is auto-accepted.
  /// </summary>
  private static (string StdOut, string StdErr, int ExitCode) RunSudo(string command, bool extraYes = false) {
    var stdin = SudoPassword + "\n" + (extraYes ? "y\ny\ny\ny\n" : "");
    return RunWithStdin("/bin/bash", $"-c \"sudo -S {command}\"", stdin);
  }

  private static (string StdOut, string StdErr, int ExitCode) Run(string exe, string args) =>
    RunWithStdin(exe, args, null);

  private static (string StdOut, string StdErr, int ExitCode) RunWithStdin(string exe, string args, string? stdin) {
    var psi = new ProcessStartInfo {
      FileName = exe,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = stdin != null,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    try {
      using var p = Process.Start(psi)!;
      if (stdin != null) {
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
      }
      var so = p.StandardOutput.ReadToEnd();
      var se = p.StandardError.ReadToEnd();
      if (!p.WaitForExit(180_000)) {
        try { p.Kill(true); } catch { /* best effort */ }
      }
      return (so, se, p.ExitCode);
    } catch (Exception ex) {
      return (string.Empty, ex.Message, -1);
    }
  }
}
