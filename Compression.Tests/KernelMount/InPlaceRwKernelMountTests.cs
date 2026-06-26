using System.Diagnostics;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.KernelMount;

/// <summary>
/// Real Linux-kernel mount gate for filesystems brought to genuine in-place
/// read/write. Each test (1) builds an image with this repo's writer, (2)
/// adds a file through the descriptor's in-place <c>Add</c> path, then (3)
/// loop-mounts the image read-only with the <em>host kernel driver</em> and
/// reads the added file back, asserting the bytes match exactly.
///
/// <para>This is the strongest available proof: it is the actual on-disk
/// driver — not our own reader — that has to accept the superblock, walk the
/// directory and return the file. A reader/writer pair that share a wrong
/// layout would pass our round-trip tests but fail here.</para>
///
/// <para><b>Environment requirements.</b> A Linux host whose kernel carries
/// the relevant filesystem module (<c>minix</c>, <c>nilfs2</c>), plus
/// <c>losetup</c> + <c>mount</c> reachable through <c>sudo</c>. The sudo
/// password defaults to the conventional CI value and can be overridden with
/// the <c>CWB_SUDO_PASSWORD</c> environment variable; set
/// <c>CWB_SKIP_SUDO=1</c> to force-skip on hosts where unattended sudo is
/// undesirable. Every prerequisite is probed and the test
/// <see cref="Assert.Ignore(string)"/>s cleanly when anything is missing — it
/// never fails on environment.</para>
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
[Category("KernelMount")]
// Also tagged OsIntegration so the core test filter (Category!=OsIntegration)
// excludes it: this fixture shells to `sudo losetup`/`mount`, which must only
// run in the dedicated, continue-on-error OS-integration lane — never during an
// ordinary `dotnet test` or the core CI run.
[Category("OsIntegration")]
public sealed class InPlaceRwKernelMountTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_kmount_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Tests ───────────────────────────────────────────────────────────

  [Test]
  public void MinixV1_OurInPlaceImage_KernelMinixMountReadsAddedFileByteExact() {
    RequireLinuxModule("minix");

    var img = BuildMinixV1();
    var path = Path.Combine(this._tmpDir, "minixv1.img");
    File.WriteAllBytes(path, img);

    var got = MountReadFile(path, fsType: "minix", file: "added.dat");
    Assert.That(got, Is.EqualTo(MinixV1Payload),
      "host minix driver must mount our writer's image and read the in-place-added file byte-exact");
  }

  [Test]
  public void MinixV2_OurInPlaceImage_KernelMinixMountReadsAddedFileByteExact() {
    RequireLinuxModule("minix");

    var img = BuildMinixV2();
    var path = Path.Combine(this._tmpDir, "minixv2.img");
    File.WriteAllBytes(path, img);

    var got = MountReadFile(path, fsType: "minix", file: "added.dat");
    Assert.That(got, Is.EqualTo(MinixV2Payload),
      "host minix driver must mount our writer's image and read the in-place-added file byte-exact");
  }

  [Test]
  public void Nilfs2_OurImage_KernelNilfs2MountReadsFileByteExact() {
    RequireLinuxModule("nilfs2");

    var img = BuildNilfs2();
    var path = Path.Combine(this._tmpDir, "nilfs2.img");
    File.WriteAllBytes(path, img);

    var got = MountReadFile(path, fsType: "nilfs2", file: "added.dat");
    Assert.That(got, Is.EqualTo(Nilfs2Payload),
      "host nilfs2 driver must mount our writer's image and read our file byte-exact");
  }

  // ── Image builders (writer + in-place Add) ──────────────────────────

  private static readonly string MinixV1Payload = "PROOF-PAYLOAD-MINIXV1";
  private static readonly string MinixV2Payload = "PROOF-PAYLOAD-MINIXV2";
  private static readonly string Nilfs2Payload = "PROOF-PAYLOAD-NILFS2";

  private static byte[] BuildMinixV1() {
    using var ms = new MemoryStream();
    using (var w = new FileSystem.MinixV1.MinixV1Writer(ms, leaveOpen: true)) {
      w.AddFile("readme.txt", "Minix v1 volume readme."u8.ToArray());
      w.AddFile("docs/guide.txt", "guide one dir deep"u8.ToArray());
      w.Finish();
    }
    using var work = new MemoryStream();
    work.Write(ms.ToArray());
    work.Position = 0;
    ((IArchiveModifiable)new FileSystem.MinixV1.MinixV1FormatDescriptor())
      .Add(work, [ArchiveInputInfo.InMemory("added.dat", Encoding.ASCII.GetBytes(MinixV1Payload))]);
    return work.ToArray();
  }

  private static byte[] BuildMinixV2() {
    using var ms = new MemoryStream();
    using (var w = new FileSystem.MinixV2.MinixV2Writer(ms, leaveOpen: true)) {
      w.AddFile("readme.txt", "Minix v2 volume readme."u8.ToArray());
      w.AddFile("docs/guide.txt", "guide one dir deep"u8.ToArray());
      w.Finish();
    }
    using var work = new MemoryStream();
    work.Write(ms.ToArray());
    work.Position = 0;
    ((IArchiveModifiable)new FileSystem.MinixV2.MinixV2FormatDescriptor())
      .Add(work, [ArchiveInputInfo.InMemory("added.dat", Encoding.ASCII.GetBytes(MinixV2Payload))]);
    return work.ToArray();
  }

  private static byte[] BuildNilfs2() {
    // The NILFS2 writer emits the full single-checkpoint log in one pass, so
    // the "in-place add" is expressed by adding both files before Build().
    var w = new FileSystem.Nilfs2.Nilfs2Writer();
    w.AddFile("readme.txt", "Nilfs2 volume readme."u8.ToArray());
    w.AddFile("added.dat", Encoding.ASCII.GetBytes(Nilfs2Payload));
    return w.Build(blockSize: 4096, volumeLabel: "CWBOUR");
  }

  // ── Loop-mount harness ──────────────────────────────────────────────

  private static string SudoPassword =>
    Environment.GetEnvironmentVariable("CWB_SUDO_PASSWORD") ?? "1234";

  private static bool LinuxSudoMountAvailable {
    get {
      if (!OperatingSystem.IsLinux()) return false;
      if (Environment.GetEnvironmentVariable("CWB_SKIP_SUDO") == "1") return false;
      if (!HasTool("losetup") || !HasTool("mount")) return false;
      // `sudo -S -v` validates the supplied password without running anything.
      var r = RunSudo("true");
      return r.ExitCode == 0;
    }
  }

  private static void RequireLinuxModule(string module) {
    if (!OperatingSystem.IsLinux())
      Assert.Ignore("kernel-mount gate requires a Linux host.");
    if (!LinuxSudoMountAvailable)
      Assert.Ignore("losetup/mount via sudo unavailable (set CWB_SUDO_PASSWORD, or CWB_SKIP_SUDO=1 to silence).");
    // The module is usable when it is loaded, modprobe-loadable, or built-in.
    var probe = Run("/bin/bash",
      $"-c \"grep -qw {module} /proc/filesystems || lsmod | grep -qw {module} || " +
      $"echo {Shq(SudoPassword)} | sudo -S modprobe {module} 2>/dev/null\"");
    var present = Run("/bin/bash", $"-c \"grep -qw {module} /proc/filesystems\"");
    if (present.ExitCode != 0 && probe.ExitCode != 0)
      Assert.Ignore($"kernel filesystem module '{module}' not available on this host.");
  }

  /// <summary>
  /// Loop-mounts <paramref name="imagePath"/> read-only as
  /// <paramref name="fsType"/>, reads <paramref name="file"/> from the mount
  /// root and returns its content as a string. Always tears the loop device
  /// and mount down, even on failure.
  /// </summary>
  private string MountReadFile(string imagePath, string fsType, string file) {
    var mnt = Path.Combine(this._tmpDir, "mnt");
    Directory.CreateDirectory(mnt);

    var setup = RunSudo($"losetup --find --show {Shq(imagePath)}");
    Assert.That(setup.ExitCode, Is.Zero, $"losetup failed: {setup.StdErr}");
    var loop = setup.StdOut.Trim();
    Assert.That(loop, Does.StartWith("/dev/loop"), $"unexpected loop device '{loop}'");

    try {
      var mr = RunSudo($"mount -t {fsType} -o ro {Shq(loop)} {Shq(mnt)}");
      Assert.That(mr.ExitCode, Is.Zero,
        $"kernel '{fsType}' mount rejected our image (exit {mr.ExitCode}): {mr.StdErr}");
      try {
        var cat = RunSudo($"cat {Shq(Path.Combine(mnt, file))}");
        Assert.That(cat.ExitCode, Is.Zero, $"reading {file} failed: {cat.StdErr}");
        return cat.StdOut;
      } finally {
        RunSudo($"umount {Shq(mnt)}");
      }
    } finally {
      RunSudo($"losetup -d {Shq(loop)}");
    }
  }

  // ── process helpers ─────────────────────────────────────────────────

  private static bool HasTool(string tool) =>
    Run("/bin/bash", $"-c \"command -v {tool}\"").ExitCode == 0;

  /// <summary>Single-quote a token for safe embedding in a bash command.</summary>
  private static string Shq(string s) => "'" + s.Replace("'", "'\\''") + "'";

  private static (string StdOut, string StdErr, int ExitCode) RunSudo(string command) =>
    Run("/bin/bash", $"-c \"echo {Shq(SudoPassword)} | sudo -S {command}\"");

  private static (string StdOut, string StdErr, int ExitCode) Run(string exe, string args) {
    var psi = new ProcessStartInfo {
      FileName = exe,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    try {
      using var p = Process.Start(psi)!;
      var so = p.StandardOutput.ReadToEnd();
      var se = p.StandardError.ReadToEnd();
      if (!p.WaitForExit(90_000)) {
        try { p.Kill(true); } catch { /* best effort */ }
      }
      return (so, se, p.ExitCode);
    } catch (Exception ex) {
      return (string.Empty, ex.Message, -1);
    }
  }
}
