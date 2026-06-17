#pragma warning disable CS1591
using System.Text;
using FileSystem.Nilfs2;

namespace Compression.Tests.Nilfs2;

/// <summary>
/// External-tool conformance gates for NILFS2, run against the real
/// <c>nilfs-utils</c> + libguestfs appliance kernel inside WSL.
///
/// <para><b>Why an appliance kernel.</b> The WSL2 host kernel ships no
/// <c>nilfs2</c> module, so a host <c>mount -t nilfs2</c> and the
/// <c>nilfs-tune</c>/<c>dumpseg</c> ioctls (which talk to the kernel driver)
/// cannot run. libguestfs boots its own appliance kernel — built from the
/// generic Ubuntu kernel package which <em>does</em> carry the nilfs2 driver —
/// so <c>guestfish</c> gives a real, kernel-grade NILFS2 mount/read gate that is
/// independent of the host kernel.</para>
///
/// <para><b>Gates.</b></para>
/// <list type="number">
///   <item><description><b>Reverse gate</b> (always runnable when mkfs.nilfs2
///   exists): create an image with the real <c>mkfs.nilfs2</c>, parse it with
///   <see cref="Nilfs2Reader"/>, and assert the superblock fields + the
///   crc32_le checksum validate. Catches a reader that only understands our own
///   writer's layout.</description></item>
///   <item><description><b>Superblock CRC gate</b> (no external tool needed):
///   our writer's image must carry a checksum that re-validates on read-back,
///   and a correctly-placed secondary copy.</description></item>
///   <item><description><b>Mount gate</b> (guestfish appliance): loop-mount the
///   image via the appliance nilfs2 driver. The real <c>mkfs.nilfs2</c> image
///   mounts and round-trips; our writer's image is rejected because the full
///   log structure (super root + DAT/cpfile/sufile/ifile B-trees) is not
///   emitted — the documented, honest limit. The test asserts both outcomes so
///   the gap can never silently regress into a false R/W claim.</description></item>
/// </list>
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
[Category("Nilfs2External")]
public class Nilfs2ExternalConformanceTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_nilfs2_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Capability probes ──────────────────────────────────────────────

  private static bool MkfsAvailable =>
    FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("mkfs.nilfs2");

  private static bool GuestfishAvailable =>
    FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("guestfish");

  /// <summary>WSL sudo accepting the known password (mkfs.nilfs2 needs root).</summary>
  private const string Sudo = "echo 1234 | sudo -S";

  /// <summary>
  /// Prepares the libguestfs appliance prerequisites idempotently: the appliance
  /// reads the host vmlinuz (root-only by default) and needs /dev/kvm. Both
  /// chmods are no-ops once applied. Returns the env prefix that selects the
  /// direct backend.
  /// </summary>
  private static string GuestfishEnv() => "export LIBGUESTFS_BACKEND=direct; " +
    $"{Sudo} chmod 0666 /dev/kvm 2>/dev/null; " +
    $"{Sudo} chmod 0644 /boot/vmlinuz-* 2>/dev/null; ";

  /// <summary>
  /// True when the guestfish appliance can actually build and recognise a real
  /// nilfs2 image. Cached so the (slow) appliance boot runs at most once.
  /// </summary>
  private static bool? _guestfishNilfsCapable;
  private static bool GuestfishNilfsCapable {
    get {
      if (_guestfishNilfsCapable is { } cached) return cached;
      if (!GuestfishAvailable || !MkfsAvailable) return (_guestfishNilfsCapable = false).Value;
      var probe =
        GuestfishEnv() +
        "cd $(mktemp -d); dd if=/dev/zero of=p.nilfs2 bs=1M count=8 status=none; " +
        $"{Sudo} mkfs.nilfs2 -b 4096 -B 16 -q p.nilfs2 >/dev/null 2>&1; " +
        "printf 'run\\nlist-filesystems\\n' | timeout 360 guestfish --ro -a p.nilfs2 2>&1";
      var r = FsInteropToolbox.RunWsl(probe);
      return (_guestfishNilfsCapable = r.StdOut.Contains("nilfs2", StringComparison.OrdinalIgnoreCase)).Value;
    }
  }

  // ── Test data ──────────────────────────────────────────────────────

  private static byte[] SmallText => "Hello from CompressionWorkbench NILFS2 conformance!"u8.ToArray();

  // ── Gate 1: reverse gate — read a real mkfs.nilfs2 image ───────────

  [Test]
  public void ReverseGate_ReadsRealMkfsSuperblock() {
    if (!MkfsAvailable)
      Assert.Ignore("mkfs.nilfs2 not available in WSL (install nilfs-tools).");

    var winImg = Path.Combine(this._tmpDir, "real.nilfs2");
    var wsl = FsInteropToolbox.WinToWsl(winImg);
    // mkfs.nilfs2 needs ~128 MB at default geometry; -B 16 shrinks segments so an
    // 8 MB image is accepted (verified).
    var mk = FsInteropToolbox.RunWsl(
      $"dd if=/dev/zero of={wsl} bs=1M count=8 status=none && " +
      $"{Sudo} mkfs.nilfs2 -b 4096 -B 16 -L CWBREAL {wsl} 2>&1");
    Assert.That(File.Exists(winImg) && new FileInfo(winImg).Length > 0, Is.True,
      $"mkfs.nilfs2 did not produce an image.\n{mk.StdOut}\n{mk.StdErr}");

    using var fs = File.OpenRead(winImg);
    var r = new Nilfs2Reader(fs);

    Assert.Multiple(() => {
      Assert.That(r.ValidSuperblock, Is.True, "real mkfs superblock should parse");
      Assert.That(r.Magic, Is.EqualTo((ushort)0x3434));
      Assert.That(r.RevLevel, Is.GreaterThanOrEqualTo(2u));
      Assert.That(r.SBytes, Is.EqualTo((ushort)280), "mkfs writes s_bytes=280");
      Assert.That(r.ChecksumValid, Is.True,
        "our crc32_le must reproduce the s_sum mkfs.nilfs2 wrote (reverse gate)");
      Assert.That(r.VolumeLabel, Is.EqualTo("CWBREAL"), "label parsed from +0xA8");
      Assert.That(r.DevSize, Is.EqualTo(8UL * 1024 * 1024));
      Assert.That(r.BlocksPerSegment, Is.EqualTo(16u));
    });
  }

  // ── Gate 2: superblock CRC self-consistency on our writer ───────────

  [Test]
  public void SuperblockGate_OurWriterEmitsCrcValidSuperblockPair() {
    var w = new Nilfs2Writer();
    w.AddFile("hello.txt", SmallText);
    w.AddFile("dir/sub.txt", "nested"u8.ToArray());
    var img = w.Build(blockSize: 4096, volumeLabel: "CWBOUR");

    // Primary superblock checksum re-validates.
    Assert.That(Nilfs2Superblock.VerifyChecksum(img.AsSpan(Nilfs2Superblock.PrimaryOffset)),
      Is.True, "primary superblock crc32_le must validate");

    // Secondary superblock present one block before EOF and also CRC-valid.
    var secOff = img.Length - Nilfs2Superblock.SecondaryBackOffset;
    Assert.That(Nilfs2Superblock.VerifyChecksum(img.AsSpan(secOff)),
      Is.True, "secondary superblock crc32_le must validate");

    // Reader agrees and surfaces the label from the spec-correct offset.
    using var ms = new MemoryStream(img);
    var r = new Nilfs2Reader(ms);
    Assert.Multiple(() => {
      Assert.That(r.ChecksumValid, Is.True);
      Assert.That(r.VolumeLabel, Is.EqualTo("CWBOUR"));
      Assert.That(r.SBytes, Is.EqualTo((ushort)280));
    });
  }

  // ── Gate 3a: mount gate — real mkfs image mounts + round-trips ─────

  [Test]
  public void MountGate_RealMkfsImage_MountsAndRoundTripsViaAppliance() {
    if (!GuestfishNilfsCapable)
      Assert.Ignore("guestfish appliance with nilfs2 support unavailable " +
        "(needs libguestfs-nilfs + a generic kernel package providing /boot/vmlinuz; " +
        "the WSL host kernel itself has no nilfs2 module).");

    var winImg = Path.Combine(this._tmpDir, "rt.nilfs2");
    var wsl = FsInteropToolbox.WinToWsl(winImg);
    var script =
      GuestfishEnv() +
      $"dd if=/dev/zero of={wsl} bs=1M count=8 status=none && " +
      $"{Sudo} mkfs.nilfs2 -b 4096 -B 16 -q {wsl} >/dev/null 2>&1 && " +
      "printf 'run\\n" +
      "mount /dev/sda /\\n" +
      "write /cwb.txt \"roundtrip-payload\"\\n" +
      "cat /cwb.txt\\n" +
      "umount /\\n' | " +
      $"timeout 420 guestfish -a {wsl} 2>&1";
    var r = FsInteropToolbox.RunWsl(script);

    Assert.That(r.StdOut, Does.Contain("roundtrip-payload"),
      $"appliance nilfs2 mount + write + read-back should round-trip.\n" +
      $"stdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }

  // ── Gate 3b: mount gate — our image is honestly NOT kernel-mountable ─

  /// <summary>
  /// Documents and pins the honest limit: our writer emits a byte-accurate,
  /// CRC-valid superblock pair, but not the full log structure (super root +
  /// DAT/cpfile/sufile/ifile B-trees + segment summaries) the kernel needs to
  /// mount. The real nilfs2 driver therefore rejects the image. Asserting the
  /// rejection means a future "we made it mountable" change must update this
  /// test deliberately — it can never silently regress into a false R/W claim.
  /// </summary>
  [Test]
  public void MountGate_OurImage_IsNotKernelMountable_DocumentedLimit() {
    if (!GuestfishNilfsCapable)
      Assert.Ignore("guestfish appliance with nilfs2 support unavailable.");

    var w = new Nilfs2Writer();
    w.AddFile("hello.txt", SmallText);
    var winImg = Path.Combine(this._tmpDir, "our.nilfs2");
    File.WriteAllBytes(winImg, w.Build(blockSize: 4096, volumeLabel: "CWBOUR"));
    var wsl = FsInteropToolbox.WinToWsl(winImg);

    var script =
      GuestfishEnv() +
      "printf 'run\\nmount /dev/sda /\\n' | " +
      $"timeout 420 guestfish -a {wsl} 2>&1; true";
    var r = FsInteropToolbox.RunWsl(script);

    // The kernel rejects it: either no nilfs2 detected, or the mount fails with a
    // bad-superblock / wrong-fs-type error. Both are the documented limit.
    var rejected =
      r.StdOut.Contains("wrong fs type", StringComparison.OrdinalIgnoreCase) ||
      r.StdOut.Contains("bad superblock", StringComparison.OrdinalIgnoreCase) ||
      r.StdOut.Contains("mount exited", StringComparison.OrdinalIgnoreCase) ||
      r.StdOut.Contains("libguestfs: error", StringComparison.OrdinalIgnoreCase);
    Assert.That(rejected, Is.True,
      "Our image is documented as NOT kernel-mountable (no super root / metadata " +
      "B-trees). If this now mounts, the writer gained real R/W — update the " +
      "descriptor capabilities and this test deliberately.\n" +
      $"stdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }
}
