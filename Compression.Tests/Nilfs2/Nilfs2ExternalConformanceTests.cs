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
///   image via the appliance nilfs2 driver. Both the real <c>mkfs.nilfs2</c>
///   image and our writer's image mount and round-trip — our writer emits the
///   full single-checkpoint log structure (super root + DAT/cpfile/sufile/ifile
///   + segment summary with the spec checksums + a flat root directory), so the
///   kernel mounts it and reads back the files we wrote.</description></item>
/// </list>
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
[Category("Nilfs2External")]
[Category("Slow")]
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

  // ── Gate 3b: mount gate — OUR image mounts + round-trips via the driver ─

  /// <summary>
  /// Pins the achieved capability: <see cref="Nilfs2Writer"/> now emits the full
  /// single-checkpoint log structure the kernel needs — a super root carrying the
  /// DAT / cpfile / sufile inodes, a segment summary with the spec
  /// (ss_sumsum / ss_datasum) checksums, an ifile holding the root directory
  /// inode, a DAT (disk-address-translation) table, and a flat root directory
  /// with the user files. The real <c>nilfs2</c> appliance kernel mounts the
  /// image, lists the directory, and reads back a file we wrote. If this ever
  /// regresses (mount fails), the writer lost real mountability — fix the writer,
  /// do not silently weaken the assertion.
  /// </summary>
  [Test]
  public void MountGate_OurImage_MountsAndReadsBackViaAppliance() {
    if (!GuestfishNilfsCapable)
      Assert.Ignore("guestfish appliance with nilfs2 support unavailable.");

    var w = new Nilfs2Writer();
    w.AddFile("hello.txt", SmallText);
    var winImg = Path.Combine(this._tmpDir, "our.nilfs2");
    File.WriteAllBytes(winImg, w.Build(blockSize: 4096, volumeLabel: "CWBOUR"));
    var wsl = FsInteropToolbox.WinToWsl(winImg);

    var script =
      GuestfishEnv() +
      "printf 'run\\n" +
      "mount /dev/sda /\\n" +
      "cat /hello.txt\\n" +
      "umount /\\n' | " +
      $"timeout 420 guestfish -a {wsl} 2>&1";
    var r = FsInteropToolbox.RunWsl(script);

    Assert.That(r.StdOut, Does.Contain(System.Text.Encoding.UTF8.GetString(SmallText)),
      "The real nilfs2 kernel driver must mount our image and read back the file " +
      "we wrote. If this fails, the writer lost kernel-grade mountability.\n" +
      $"stdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }
}
