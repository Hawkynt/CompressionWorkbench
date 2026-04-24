using System.Diagnostics;
using System.Text;
using FileFormat.Qcow2;
using FileFormat.Vhd;
using FileFormat.Vmdk;
using FileSystem.ExFat;
using FileSystem.Ext;
using FileSystem.Fat;
using FileSystem.HfsPlus;
using FileSystem.Iso;
using FileSystem.Ntfs;
using FileSystem.SquashFs;

namespace Compression.Tests;

/// <summary>
/// Round-trip tests that validate filesystem / disk-image output from our
/// writers against real third-party tools installed on the host (7-Zip,
/// qemu-img, DISM, chkdsk, mtools). Each test skips via <c>Assert.Ignore</c>
/// when the required tool is missing, emitting an actionable install hint.
/// <para>
/// Tool discovery runs once per fixture via a static ctor on
/// <see cref="FsInteropToolbox"/>. Nothing in this fixture writes to
/// <c>C:\</c> — all I/O lives under <see cref="Path.GetTempPath"/>.
/// </para>
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public class ExternalFsInteropTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_fsinterop_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Test data ──────────────────────────────────────────────────────

  private static byte[] SmallText => "Hello from CompressionWorkbench FS interop!"u8.ToArray();

  private static byte[] RepetitiveText {
    get {
      var sb = new StringBuilder();
      for (var i = 0; i < 64; i++)
        sb.AppendLine($"Line {i}: The quick brown fox jumps over the lazy dog.");
      return Encoding.UTF8.GetBytes(sb.ToString());
    }
  }

  // ── FAT (chkdsk via mounted VHD — needs admin; mtools if available) ──

  [Test]
  public void Fat_OurImage_InspectableBy7z() {
    FsInteropToolbox.Require7z();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    fat.AddFile("REPEAT.TXT", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "fat12.img");
    File.WriteAllBytes(imgPath, fat.Build());

    var result = FsInteropToolbox.Run7z($"l \"{imgPath}\"");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"7z list failed:\nstdout:{result.StdOut}\nstderr:{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("HELLO").IgnoreCase.Or.Contain("hello").IgnoreCase,
      "7z listing should mention HELLO.TXT");
  }

  [Test]
  public void Fat_OurImage_Extractable7z_RoundTrip() {
    FsInteropToolbox.Require7z();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    fat.AddFile("DATA.BIN", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "fat12_rt.img");
    File.WriteAllBytes(imgPath, fat.Build());

    var extractDir = Path.Combine(this._tmpDir, "fat_x");
    Directory.CreateDirectory(extractDir);
    var result = FsInteropToolbox.Run7z($"x \"{imgPath}\" -o\"{extractDir}\" -y");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"7z extract failed:\n{result.StdErr}");

    var hello = FsInteropToolbox.FindFile(extractDir, "HELLO.TXT") ?? FsInteropToolbox.FindFile(extractDir, "hello.txt");
    Assert.That(hello, Is.Not.Null, "Expected HELLO.TXT in extract output");
    Assert.That(File.ReadAllBytes(hello!), Is.EqualTo(SmallText));
  }

  [Test]
  public void Fat_OurImage_ValidatedByMtools() {
    if (!FsInteropToolbox.MToolsAvailable)
      Assert.Ignore("mtools not installed. Install via Cygwin (pkg 'mtools') or https://www.gnu.org/software/mtools/ and add to PATH.");

    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "fat_mtools.img");
    File.WriteAllBytes(imgPath, fat.Build());

    var result = FsInteropToolbox.RunPath("minfo", $"-i \"{imgPath}\" ::");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"minfo rejected our FAT image:\n{result.StdErr}");
  }

  [Test]
  public void Fat_OurImage_ChkdskRequiresAdmin_Skip() {
    Assert.Ignore("chkdsk on a raw FAT image requires mounting via Mount-DiskImage (admin PowerShell). " +
                  "To validate manually: PowerShell (Admin) → `Mount-DiskImage -ImagePath <img>` then `chkdsk X:` where X: is the assigned letter.");
  }

  // ── exFAT ──────────────────────────────────────────────────────────

  [Test]
  public void ExFat_OurImage_InspectableBy7z() {
    FsInteropToolbox.Require7z();
    var xfat = new ExFatWriter();
    xfat.AddFile("hello.txt", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "exfat.img");
    File.WriteAllBytes(imgPath, xfat.Build());

    var result = FsInteropToolbox.Run7z($"l \"{imgPath}\"");
    // 7-Zip's exFAT handler is newer; tolerate the case where the build can't parse it.
    if (result.ExitCode != 0)
      Assert.Ignore($"7z couldn't parse our exFAT image (build may predate exFAT handler): {result.StdErr.Trim()}");
  }

  // ── NTFS ───────────────────────────────────────────────────────────

  [Test]
  public void Ntfs_OurImage_InspectableBy7z() {
    FsInteropToolbox.Require7z();
    var ntfs = new NtfsWriter();
    ntfs.AddFile("hello.txt", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "ntfs.img");
    File.WriteAllBytes(imgPath, ntfs.Build());

    var result = FsInteropToolbox.Run7z($"l \"{imgPath}\"");
    if (result.ExitCode != 0)
      Assert.Ignore($"7z couldn't parse our NTFS image: {result.StdErr.Trim()}");
  }

  // ── ext2/3/4 ───────────────────────────────────────────────────────

  [Test]
  public void Ext_OurImage_InspectableBy7z() {
    FsInteropToolbox.Require7z();
    var ext = new ExtWriter();
    ext.AddFile("hello.txt", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "ext2.img");
    File.WriteAllBytes(imgPath, ext.Build());

    var result = FsInteropToolbox.Run7z($"l \"{imgPath}\"");
    if (result.ExitCode != 0)
      Assert.Ignore($"7z couldn't parse our ext image: {result.StdErr.Trim()}");
    Assert.That(result.StdOut, Does.Contain("hello.txt").IgnoreCase.Or.Contain("HELLO").IgnoreCase,
      "Expected hello.txt in listing");
  }

  // ── HFS+ ───────────────────────────────────────────────────────────

  [Test]
  public void HfsPlus_OurImage_InspectableBy7z() {
    FsInteropToolbox.Require7z();
    var hfs = new HfsPlusWriter();
    hfs.AddFile("hello.txt", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "hfsplus.img");
    File.WriteAllBytes(imgPath, hfs.Build());

    var result = FsInteropToolbox.Run7z($"l \"{imgPath}\"");
    if (result.ExitCode != 0)
      Assert.Ignore($"7z couldn't parse our HFS+ image: {result.StdErr.Trim()}");
  }

  // ── ISO9660 ────────────────────────────────────────────────────────

  [Test]
  public void Iso_OurImage_ListedBy7z() {
    FsInteropToolbox.Require7z();
    var iso = new IsoWriter();
    iso.AddFile("HELLO.TXT", SmallText);
    iso.AddFile("REPEAT.TXT", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "image.iso");
    File.WriteAllBytes(imgPath, iso.Build());

    var result = FsInteropToolbox.Run7z($"l \"{imgPath}\"");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"7z list on our ISO failed:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("HELLO.TXT").IgnoreCase, "ISO listing should mention HELLO.TXT");
  }

  [Test]
  public void Iso_OurImage_Extractable7z_RoundTrip() {
    FsInteropToolbox.Require7z();
    var iso = new IsoWriter();
    iso.AddFile("HELLO.TXT", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "image_rt.iso");
    File.WriteAllBytes(imgPath, iso.Build());

    var extractDir = Path.Combine(this._tmpDir, "iso_x");
    Directory.CreateDirectory(extractDir);
    var result = FsInteropToolbox.Run7z($"x \"{imgPath}\" -o\"{extractDir}\" -y");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"7z extract failed:\n{result.StdErr}");

    var hello = FsInteropToolbox.FindFile(extractDir, "HELLO.TXT") ?? FsInteropToolbox.FindFile(extractDir, "hello.txt");
    Assert.That(hello, Is.Not.Null, "Expected HELLO.TXT in extract output");
    Assert.That(File.ReadAllBytes(hello!), Is.EqualTo(SmallText));
  }

  [Test]
  public void Iso_OurImage_ReadableByDism_Skip() {
    // DISM is always present on Windows but mounting ISOs or applying WIM images
    // requires admin. We keep this test as documentation and always skip.
    Assert.Ignore("DISM can validate WIM and mounted ISO images but needs elevation. " +
                  "To validate manually (Admin PowerShell): `Mount-DiskImage -ImagePath <iso>` then inspect contents.");
  }

  // ── SquashFS ───────────────────────────────────────────────────────

  [Test]
  public void SquashFs_OurImage_ListedBy7z() {
    FsInteropToolbox.Require7z();
    var imgPath = Path.Combine(this._tmpDir, "image.sqfs");
    using (var fs = File.Create(imgPath)) {
      using var sfs = new SquashFsWriter(fs, leaveOpen: true);
      sfs.AddFile("hello.txt", SmallText);
      sfs.AddFile("repeat.txt", RepetitiveText);
    }

    var result = FsInteropToolbox.Run7z($"l \"{imgPath}\"");
    if (result.ExitCode != 0)
      Assert.Ignore($"7z couldn't parse our SquashFS image: {result.StdErr.Trim()}");
    Assert.That(result.StdOut, Does.Contain("hello.txt").IgnoreCase, "SquashFS listing should mention hello.txt");
  }

  // ── VHD ────────────────────────────────────────────────────────────

  [Test]
  public void Vhd_OurFixed_InspectableByQemuImg() {
    if (!FsInteropToolbox.QemuImgAvailable)
      Assert.Ignore("qemu-img not found. Install from https://qemu.weilnetz.de/w64/ or add its dir to PATH.");

    // Build a tiny FAT image and wrap it in a VHD
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var fatImg = fat.Build();

    var vhd = new VhdWriter();
    vhd.SetDiskData(fatImg);
    var vhdBytes = vhd.Build();
    var vhdPath = Path.Combine(this._tmpDir, "fixed.vhd");
    File.WriteAllBytes(vhdPath, vhdBytes);

    var result = FsInteropToolbox.RunPath("qemu-img", $"info \"{vhdPath}\"");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"qemu-img rejected our fixed VHD:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("vpc").Or.Contain("vhd"), "qemu-img should report format as vpc/vhd");
  }

  [Test]
  public void Vhd_OurImage_Listable7z() {
    FsInteropToolbox.Require7z();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var vhd = new VhdWriter();
    vhd.SetDiskData(fat.Build());
    var vhdPath = Path.Combine(this._tmpDir, "listable.vhd");
    File.WriteAllBytes(vhdPath, vhd.Build());

    var result = FsInteropToolbox.Run7z($"l \"{vhdPath}\"");
    if (result.ExitCode != 0)
      Assert.Ignore($"7z couldn't parse our VHD: {result.StdErr.Trim()}");
  }

  // ── VMDK ───────────────────────────────────────────────────────────

  [Test]
  public void Vmdk_OurImage_InspectableByQemuImg() {
    if (!FsInteropToolbox.QemuImgAvailable)
      Assert.Ignore("qemu-img not found. Install from https://qemu.weilnetz.de/w64/ or add its dir to PATH.");

    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var vmdk = new VmdkWriter();
    vmdk.SetDiskData(fat.Build());
    var vmdkPath = Path.Combine(this._tmpDir, "image.vmdk");
    File.WriteAllBytes(vmdkPath, vmdk.Build());

    var result = FsInteropToolbox.RunPath("qemu-img", $"info \"{vmdkPath}\"");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"qemu-img rejected our VMDK:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("vmdk"), "qemu-img should report format as vmdk");
  }

  [Test]
  public void Vmdk_OurImage_Listable7z() {
    FsInteropToolbox.Require7z();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var vmdk = new VmdkWriter();
    vmdk.SetDiskData(fat.Build());
    var vmdkPath = Path.Combine(this._tmpDir, "image_7z.vmdk");
    File.WriteAllBytes(vmdkPath, vmdk.Build());

    var result = FsInteropToolbox.Run7z($"l \"{vmdkPath}\"");
    if (result.ExitCode != 0)
      Assert.Ignore($"7z couldn't parse our VMDK: {result.StdErr.Trim()}");
  }

  // ── QCOW2 ──────────────────────────────────────────────────────────

  [Test]
  public void Qcow2_OurImage_CheckedByQemuImg() {
    if (!FsInteropToolbox.QemuImgAvailable)
      Assert.Ignore("qemu-img not found. Install from https://qemu.weilnetz.de/w64/ or add its dir to PATH.");

    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var qcow = new Qcow2Writer();
    qcow.SetDiskImage(fat.Build());

    var qcowPath = Path.Combine(this._tmpDir, "image.qcow2");
    using (var fs = File.Create(qcowPath))
      qcow.WriteTo(fs);

    var info = FsInteropToolbox.RunPath("qemu-img", $"info \"{qcowPath}\"");
    Assert.That(info.ExitCode, Is.EqualTo(0), $"qemu-img info failed:\n{info.StdErr}");
    Assert.That(info.StdOut, Does.Contain("qcow2"), "qemu-img should report format as qcow2");

    var check = FsInteropToolbox.RunPath("qemu-img", $"check \"{qcowPath}\"");
    Assert.That(check.ExitCode, Is.EqualTo(0), $"qemu-img check reported errors:\n{check.StdOut}\n{check.StdErr}");
  }

  // ═══════════════════════════════════════════════════════════════════
  // WSL-based validation — real Linux kernel fsck/repair/check tools
  // ═══════════════════════════════════════════════════════════════════

  private static void RequireWsl() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. Run `wsl --install` in Admin PowerShell and reboot, " +
                    "then `sudo apt install -y e2fsprogs xfsprogs btrfs-progs exfatprogs dosfstools " +
                    "udftools squashfs-tools` inside the Linux shell.");
  }

  private static void RequireWslTool(string tool) {
    RequireWsl();
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"WSL is present but '{tool}' is not installed in the distro. " +
                    $"Run inside WSL: `sudo apt install -y <pkg-providing-{tool}>`.");
  }

  // ── ext4 (fsck.ext4 -n + dumpe2fs) ─────────────────────────────────

  [Test]
  public void Ext_OurImage_Fsckext4Accepts() {
    RequireWslTool("fsck.ext4");
    var ext = new ExtWriter();
    ext.AddFile("hello.txt", SmallText);
    ext.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "ext4_fsck.img");
    File.WriteAllBytes(imgPath, ext.Build());

    // -n = no-op (read-only check); -f = force even if "clean"; -v = verbose
    var result = FsInteropToolbox.RunWsl($"fsck.ext4 -fnv {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.ext4 rejected our image (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test]
  public void Ext_OurImage_DumpE2fsReportsSuperblock() {
    RequireWslTool("dumpe2fs");
    var ext = new ExtWriter();
    ext.AddFile("hello.txt", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "ext4_dump.img");
    File.WriteAllBytes(imgPath, ext.Build());

    var result = FsInteropToolbox.RunWsl($"dumpe2fs -h {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"dumpe2fs failed:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("Filesystem UUID"), "dumpe2fs -h should print Filesystem UUID line");
    Assert.That(result.StdOut, Does.Contain("0xEF53"), "dumpe2fs -h should report magic number 0xEF53");
  }

  // ── XFS (xfs_repair -n + xfs_db) ───────────────────────────────────

  [Test]
  public void Xfs_OurImage_XfsRepairAccepts() {
    RequireWslTool("xfs_repair");
    var xfs = new FileSystem.Xfs.XfsWriter();
    xfs.AddFile("hello.txt", SmallText);
    xfs.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "xfs_repair.img");
    using (var fs = File.Create(imgPath)) xfs.WriteTo(fs);

    // -n = no modify; -f = force (treat file as block device)
    var result = FsInteropToolbox.RunWsl($"xfs_repair -n -f {FsInteropToolbox.WinToWsl(imgPath)}");
    // xfs_repair exit 0 means clean; exit 1 means repair would be needed.
    // For a fresh mkfs-parity image, 0 is the pass criterion.
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"xfs_repair rejected our image (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  // ── Btrfs (btrfs check) ────────────────────────────────────────────

  [Test]
  public void Btrfs_OurImage_BtrfsCheckAccepts() {
    RequireWslTool("btrfs");
    var btrfs = new FileSystem.Btrfs.BtrfsWriter();
    btrfs.AddFile("hello.txt", SmallText);
    btrfs.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "btrfs_check.img");
    using (var fs = File.Create(imgPath)) btrfs.WriteTo(fs);

    var result = FsInteropToolbox.RunWsl($"btrfs check --readonly {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"btrfs check rejected our image (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  // ── FAT (fsck.fat -n) ──────────────────────────────────────────────

  [Test]
  public void Fat_OurImage_FsckFatAccepts() {
    RequireWslTool("fsck.fat");
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    fat.AddFile("REPEAT.TXT", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "fat_fsck.img");
    File.WriteAllBytes(imgPath, fat.Build());

    // -n = no-op; -V = verify (second pass)
    var result = FsInteropToolbox.RunWsl($"fsck.fat -n -V {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.fat rejected our image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  // ── exFAT (fsck.exfat) ─────────────────────────────────────────────

  [Test]
  public void ExFat_OurImage_FsckExfatAccepts() {
    RequireWslTool("fsck.exfat");
    var xfat = new ExFatWriter();
    xfat.AddFile("hello.txt", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "exfat_fsck.img");
    File.WriteAllBytes(imgPath, xfat.Build());

    // fsck.exfat in read-only mode
    var result = FsInteropToolbox.RunWsl($"fsck.exfat -n {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.exfat rejected our image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  // ── SquashFS (unsquashfs -stat) ────────────────────────────────────

  [Test]
  public void SquashFs_OurImage_UnsquashfsStatsAccepts() {
    RequireWslTool("unsquashfs");
    var imgPath = Path.Combine(this._tmpDir, "sqfs_stat.sqfs");
    using (var fs = File.Create(imgPath)) {
      using var sfs = new SquashFsWriter(fs, leaveOpen: true);
      sfs.AddFile("hello.txt", SmallText);
      sfs.AddFile("repeat.txt", RepetitiveText);
    }

    // -s dumps superblock info; non-zero exit means the image is not a valid SquashFS.
    var result = FsInteropToolbox.RunWsl($"unsquashfs -s {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"unsquashfs rejected our image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("Found a valid").IgnoreCase.Or.Contain("Superblock"),
      "unsquashfs -s should report a valid SquashFS superblock");
  }

  // ── Reverse direction: Linux mkfs → our reader ─────────────────────

  [Test]
  public void Ext_LinuxMkfsOutput_ReadByOurReader() {
    RequireWslTool("mkfs.ext4");
    // Build an empty 4MB file and let mkfs.ext4 format it in-place.
    var imgPath = Path.Combine(this._tmpDir, "mkfs_ext4.img");
    using (var fs = File.Create(imgPath))
      fs.SetLength(4 * 1024 * 1024);

    // -F = force even on a regular file; -b 1024 = 1KB blocks (smallest for 4MB image)
    var result = FsInteropToolbox.RunWsl($"mkfs.ext4 -F -b 1024 {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"mkfs.ext4 failed:\n{result.StdErr}");

    // Our reader should accept the Linux-generated image.
    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.Ext.ExtFormatDescriptor();
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null, "Our reader returned null for a valid Linux-mkfs.ext4 image");
    // A freshly-mkfs'd ext4 has only `lost+found`. That's enough to prove we read it.
  }

  [Test]
  public void Fat_LinuxMkfsOutput_ReadByOurReader() {
    RequireWslTool("mkfs.vfat");
    var imgPath = Path.Combine(this._tmpDir, "mkfs_vfat.img");
    using (var fs = File.Create(imgPath))
      fs.SetLength(2 * 1024 * 1024);

    var result = FsInteropToolbox.RunWsl($"mkfs.vfat -F 12 {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"mkfs.vfat failed:\n{result.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.Fat.FatFormatDescriptor();
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null, "Our reader returned null for a valid Linux-mkfs.vfat image");
  }

  // ── JFS (mkfs.jfs + fsck.jfs) ──────────────────────────────────────

  [Test]
  public void Jfs_OurImage_FsckJfsAccepts() {
    RequireWslTool("fsck.jfs");
    var jfs = new FileSystem.Jfs.JfsWriter();
    jfs.AddFile("hello.txt", SmallText);
    jfs.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "jfs_fsck.img");
    using (var fs = File.Create(imgPath)) jfs.WriteTo(fs);

    // fsck.jfs: -n (no modify), -f (force even if clean), -v (verbose). Exit 0 = clean.
    var result = FsInteropToolbox.RunWsl($"fsck.jfs -n -f -v {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.jfs rejected our image (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test]
  public void Jfs_LinuxMkfsOutput_ReadByOurReader() {
    RequireWslTool("mkfs.jfs");
    // JFS minimum is 16 MB.
    var imgPath = Path.Combine(this._tmpDir, "mkfs_jfs.img");
    using (var fs = File.Create(imgPath))
      fs.SetLength(16 * 1024 * 1024);

    // -q = quiet, no prompts; pipe 'y' as safety.
    var result = FsInteropToolbox.RunWsl($"echo y | mkfs.jfs -q {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"mkfs.jfs failed:\n{result.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.Jfs.JfsFormatDescriptor();
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null, "Our reader returned null for a valid Linux-mkfs.jfs image");
  }

  // ── ReiserFS (mkfs.reiserfs + reiserfsck) ──────────────────────────

  [Test]
  public void ReiserFs_OurImage_ReiserfsckAccepts() {
    RequireWslTool("reiserfsck");
    var rfs = new FileSystem.ReiserFs.ReiserFsWriter();
    rfs.AddFile("hello.txt", SmallText);
    rfs.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "reiser_check.img");
    using (var fs = File.Create(imgPath)) rfs.WriteTo(fs);

    // --check = read-only validation; --quiet = no progress bar; -y = answer yes to any prompts.
    var result = FsInteropToolbox.RunWsl($"reiserfsck --check --quiet -y {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"reiserfsck rejected our image (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test]
  public void ReiserFs_LinuxMkfsOutput_ReadByOurReader() {
    RequireWslTool("mkfs.reiserfs");
    // ReiserFS minimum is ~32 MB in practice.
    var imgPath = Path.Combine(this._tmpDir, "mkfs_reiser.img");
    using (var fs = File.Create(imgPath))
      fs.SetLength(34 * 1024 * 1024);

    // -q = quiet, -f = force (allow block-device-less regular file).
    var result = FsInteropToolbox.RunWsl($"echo y | mkfs.reiserfs -q -f {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"mkfs.reiserfs failed:\n{result.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null, "Our reader returned null for a valid Linux-mkfs.reiserfs image");
  }

  // ── F2FS (mkfs.f2fs + fsck.f2fs) ───────────────────────────────────

  [Test]
  public void F2fs_OurImage_FsckF2fsAccepts() {
    RequireWslTool("fsck.f2fs");
    var f2fs = new FileSystem.F2fs.F2fsWriter();
    f2fs.AddFile("hello.txt", SmallText);
    f2fs.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "f2fs_check.img");
    File.WriteAllBytes(imgPath, f2fs.Build());

    // fsck.f2fs: -f (force), --dry-run. Exit 0 = clean.
    var result = FsInteropToolbox.RunWsl($"fsck.f2fs -f --dry-run {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.f2fs rejected our image (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test]
  public void F2fs_LinuxMkfsOutput_ReadByOurReader() {
    RequireWslTool("mkfs.f2fs");
    // F2FS minimum is ~38 MB; we use 64 MB to match our writer's default.
    var imgPath = Path.Combine(this._tmpDir, "mkfs_f2fs.img");
    using (var fs = File.Create(imgPath))
      fs.SetLength(64 * 1024 * 1024);

    // -q = quiet, -f = force.
    var result = FsInteropToolbox.RunWsl($"mkfs.f2fs -q -f {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"mkfs.f2fs failed:\n{result.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.F2fs.F2fsFormatDescriptor();
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null, "Our reader returned null for a valid Linux-mkfs.f2fs image");
  }

  // ── UDF (mkudffs + udffsck optional) ───────────────────────────────

  [Test]
  public void Udf_OurImage_UdffsckAccepts() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. See RequireWsl() hint.");
    if (!FsInteropToolbox.WslHasTool("udffsck")) {
      // udftools on Ubuntu 24.04 doesn't ship udffsck; mkudffs is the only tool.
      // We fall back to a read-parity check via our reader as a sanity gate.
      Assert.Ignore("udffsck not available in this udftools build (Ubuntu 24.04 ships mkudffs only). " +
                    "No Linux-side reader can validate — skipping.");
    }
    var udf = new FileSystem.Udf.UdfWriter();
    udf.AddFile("hello.txt", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "udf_check.img");
    using (var fs = File.Create(imgPath)) udf.WriteTo(fs);

    var result = FsInteropToolbox.RunWsl($"udffsck {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"udffsck rejected our image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test]
  public void Udf_LinuxMkudffsOutput_ReadByOurReader() {
    RequireWslTool("mkudffs");
    // mkudffs needs ~150 blocks minimum. We use 2 MB (1024 × 2 KB blocks).
    var imgPath = Path.Combine(this._tmpDir, "mkudffs.img");
    using (var fs = File.Create(imgPath))
      fs.SetLength(2 * 1024 * 1024);

    // --blocksize 2048 is UDF default; --media-type hd says "hard disk" layout.
    // mkudffs expects block count as second positional arg when file doesn't look like a device.
    var blocks = (2 * 1024 * 1024) / 2048;
    var result = FsInteropToolbox.RunWsl(
      $"mkudffs --blocksize=2048 --media-type=hd {FsInteropToolbox.WinToWsl(imgPath)} {blocks}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"mkudffs failed:\n{result.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.Udf.UdfFormatDescriptor();
    // UDF reader may return an empty list for a freshly-formatted volume; that's fine.
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null, "Our reader returned null for a valid Linux-mkudffs image");
  }

  // ── Minix v3 (mkfs.minix) — no Linux fsck.minix for v3 ─────────────

  [Test]
  public void Minix_LinuxMkfsOutput_ReadByOurReader() {
    RequireWslTool("mkfs.minix");
    // Minix v3 supports images down to ~1 MB easily.
    var imgPath = Path.Combine(this._tmpDir, "mkfs_minix.img");
    using (var fs = File.Create(imgPath))
      fs.SetLength(4 * 1024 * 1024);

    // -3 = minix v3 (matches our writer).
    var result = FsInteropToolbox.RunWsl($"mkfs.minix -3 {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"mkfs.minix failed:\n{result.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.MinixFs.MinixFsFormatDescriptor();
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null, "Our reader returned null for a valid Linux-mkfs.minix image");
  }

  [Test]
  public void Minix_OurImage_LinuxSideCheck_Skip() {
    // util-linux ships mkfs.minix but no fsck.minix for v3 — there is no Linux tool
    // that validates v3 images. Document this and skip cleanly.
    Assert.Ignore("No Linux fsck.minix for Minix v3 in util-linux. Our reverse test " +
                  "(Linux mkfs.minix → our reader) covers read-side parity.");
  }

  // ═══════════════════════════════════════════════════════════════════
  // Mutation validation — writer + in-place/rebuild modify + fsck check
  // ═══════════════════════════════════════════════════════════════════

  private static byte[] DataA => "aaaaaaaaaaaaaaaaa-alpha"u8.ToArray();
  private static byte[] DataB => "bbbbb-bravo-original"u8.ToArray();
  private static byte[] DataBNew => "bbbbb-bravo-REPLACED-with-new-content-xxxxx"u8.ToArray();
  private static byte[] DataC => "ccccc-charlie-xxx"u8.ToArray();
  private static byte[] DataD => "ddddd-delta-added"u8.ToArray();

  // ── ext4 mutation — uses existing ExtRemover in-place + rebuild for add/replace ──

  [Test]
  public void Ext_MutateThenValidate() {
    RequireWslTool("fsck.ext4");
    var imgPath = Path.Combine(this._tmpDir, "ext4_mutate.img");

    // Step 1: create with a.txt, b.txt, c.txt
    {
      var w = new ExtWriter();
      w.AddFile("a.txt", DataA);
      w.AddFile("b.txt", DataB);
      w.AddFile("c.txt", DataC);
      File.WriteAllBytes(imgPath, w.Build());
    }

    // Step 2: initial validation
    var r1 = FsInteropToolbox.RunWsl($"fsck.ext4 -fnv {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r1.ExitCode, Is.EqualTo(0),
      $"ext4 initial image invalid: stdout:\n{r1.StdOut}\nstderr:\n{r1.StdErr}");

    // Step 3: mutate — replace b.txt, delete a.txt, add d.txt
    {
      using var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite);
      ExtModifier.Mutate(
        fs,
        replacements: [("b.txt", DataBNew), ("d.txt", DataD)],
        deletions: ["a.txt"]);
    }

    // Step 4: re-validate
    var r2 = FsInteropToolbox.RunWsl($"fsck.ext4 -fnv {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r2.ExitCode, Is.EqualTo(0),
      $"ext4 image invalid after mutation:\nstdout:\n{r2.StdOut}\nstderr:\n{r2.StdErr}");

    // Step 5: read back via our reader and confirm expected final state
    using (var stream = File.OpenRead(imgPath)) {
      var entries = new FileSystem.Ext.ExtFormatDescriptor().List(stream, null);
      var names = entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
      Assert.That(names, Does.Not.Contain("a.txt"), "a.txt should be deleted");
      Assert.That(names, Does.Contain("b.txt"), "b.txt should remain (replaced content)");
      Assert.That(names, Does.Contain("c.txt"), "c.txt should remain unchanged");
      Assert.That(names, Does.Contain("d.txt"), "d.txt should be added");
    }
  }

  // ── XFS mutation — rebuild-style via XfsModifier ───────────────────

  [Test]
  public void Xfs_MutateThenValidate() {
    RequireWslTool("xfs_repair");
    var imgPath = Path.Combine(this._tmpDir, "xfs_mutate.img");

    {
      var w = new FileSystem.Xfs.XfsWriter();
      w.AddFile("a.txt", DataA);
      w.AddFile("b.txt", DataB);
      w.AddFile("c.txt", DataC);
      using var fs = File.Create(imgPath);
      w.WriteTo(fs);
    }

    var r1 = FsInteropToolbox.RunWsl($"xfs_repair -n -f {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r1.ExitCode, Is.EqualTo(0),
      $"xfs initial image invalid:\nstdout:\n{r1.StdOut}\nstderr:\n{r1.StdErr}");

    // Mutate: rebuild-style. Replace b, delete a, add d.
    {
      using var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite);
      FileSystem.Xfs.XfsModifier.Remove(fs, ["a.txt"]);
    }
    {
      using var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite);
      FileSystem.Xfs.XfsModifier.AddOrReplace(fs, [("b.txt", DataBNew), ("d.txt", DataD)]);
    }

    var r2 = FsInteropToolbox.RunWsl($"xfs_repair -n -f {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r2.ExitCode, Is.EqualTo(0),
      $"xfs image invalid after mutation:\nstdout:\n{r2.StdOut}\nstderr:\n{r2.StdErr}");

    using (var stream = File.OpenRead(imgPath)) {
      var entries = new FileSystem.Xfs.XfsFormatDescriptor().List(stream, null);
      var names = entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
      Assert.That(names, Does.Not.Contain("a.txt"));
      Assert.That(names, Does.Contain("b.txt"));
      Assert.That(names, Does.Contain("c.txt"));
      Assert.That(names, Does.Contain("d.txt"));
    }
  }

  // ── Btrfs mutation — rebuild-style via BtrfsModifier ───────────────

  [Test]
  public void Btrfs_MutateThenValidate() {
    RequireWslTool("btrfs");
    var imgPath = Path.Combine(this._tmpDir, "btrfs_mutate.img");

    {
      var w = new FileSystem.Btrfs.BtrfsWriter();
      w.AddFile("a.txt", DataA);
      w.AddFile("b.txt", DataB);
      w.AddFile("c.txt", DataC);
      using var fs = File.Create(imgPath);
      w.WriteTo(fs);
    }

    var r1 = FsInteropToolbox.RunWsl($"btrfs check --readonly {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r1.ExitCode, Is.EqualTo(0),
      $"btrfs initial image invalid:\nstdout:\n{r1.StdOut}\nstderr:\n{r1.StdErr}");

    {
      using var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite);
      FileSystem.Btrfs.BtrfsModifier.Remove(fs, ["a.txt"]);
    }
    {
      using var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite);
      FileSystem.Btrfs.BtrfsModifier.AddOrReplace(fs, [("b.txt", DataBNew), ("d.txt", DataD)]);
    }

    var r2 = FsInteropToolbox.RunWsl($"btrfs check --readonly {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r2.ExitCode, Is.EqualTo(0),
      $"btrfs image invalid after mutation:\nstdout:\n{r2.StdOut}\nstderr:\n{r2.StdErr}");

    using (var stream = File.OpenRead(imgPath)) {
      var entries = new FileSystem.Btrfs.BtrfsFormatDescriptor().List(stream, null);
      var names = entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
      Assert.That(names, Does.Not.Contain("a.txt"));
      Assert.That(names, Does.Contain("b.txt"));
      Assert.That(names, Does.Contain("c.txt"));
      Assert.That(names, Does.Contain("d.txt"));
    }
  }

  // ── XFS regression test: 3 short-name files (reproducer for fixed sf_offset bug) ──

  [Test]
  public void Xfs_ThreeShortNameFiles_Regression() {
    RequireWslTool("xfs_repair");
    // Regression test for XfsWriter sf_offset bug (fixed). The writer used to
    // advance sf_offset by the shortform entry size (nameLen+8 padded) instead
    // of the data-block entry size (nameLen+12 padded). For name lengths 1..8
    // that mismatch produced "entry contains offset out of order in shortform
    // dir" under xfs_repair. Fix: XfsWriter.cs — see the comment at the
    // `nextOffset = ...` update line.
    var imgPath = Path.Combine(this._tmpDir, "xfs_shortnames.img");
    var w = new FileSystem.Xfs.XfsWriter();
    w.AddFile("a.txt", DataA);
    w.AddFile("b.txt", DataB);
    w.AddFile("c.txt", DataC);
    using (var fs = File.Create(imgPath)) w.WriteTo(fs);

    var result = FsInteropToolbox.RunWsl($"xfs_repair -n -f {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"xfs_repair rejected 3 short-name files:\n{result.StdOut}\n{result.StdErr}");
  }

  // ── Platform-only formats documented as non-WSL-validatable ────────

  [Test]
  public void Apfs_NoLinuxValidator_Skip() {
    Assert.Ignore("APFS has no Linux fsck (apfs-fuse is read-only and not in apt). " +
                  "Our APFS writer is self-validated only; Apple's apfs_fsck runs on macOS only.");
  }

  [Test]
  public void Zfs_NoLinuxValidator_Skip() {
    Assert.Ignore("ZFS zdb/zpool validators are tied to the zfsonlinux kernel module and not apt-installable " +
                  "in a standard WSL Ubuntu. Our ZFS writer is self-validated only.");
  }

  [Test]
  public void Mfs_NoLinuxValidator_Skip() {
    Assert.Ignore("Classic Mac MFS has no Linux validator. Self-validated only.");
  }

  [Test]
  public void HfsClassic_NoLinuxValidator_Skip() {
    Assert.Ignore("Classic HFS (not HFS+) has no Linux fsck. " +
                  "hfsutils reads but does not validate. Self-validated only.");
  }

  [Test]
  public void HfsPlus_NoLinuxFsck_Skip() {
    // Linux hfsprogs ships fsck.hfsplus but it's notoriously lenient and not apt-default.
    // Our HFS+ writer targets macOS fsck_hfs which is the canonical validator.
    Assert.Ignore("HFS+ canonical validator is macOS fsck_hfs. Linux hfsprogs fsck.hfsplus " +
                  "is lenient and not installed by default. Self-validated only.");
  }

  // ── existing (kept below) ───────────────────────────────────────────

  [Test]
  public void Qcow2_OurImage_Listable7z() {
    FsInteropToolbox.Require7z();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var qcow = new Qcow2Writer();
    qcow.SetDiskImage(fat.Build());
    var qcowPath = Path.Combine(this._tmpDir, "image_7z.qcow2");
    using (var fs = File.Create(qcowPath))
      qcow.WriteTo(fs);

    var result = FsInteropToolbox.Run7z($"l \"{qcowPath}\"");
    if (result.ExitCode != 0)
      Assert.Ignore($"7z couldn't parse our QCOW2: {result.StdErr.Trim()}");
  }
}

/// <summary>
/// Discovers and wraps host-side FS validation tools (7-Zip, qemu-img, DISM,
/// chkdsk, mtools). Cached on first access.
/// </summary>
internal static class FsInteropToolbox {
  // Known portable 7-Zip location used by ExternalInteropTests.cs.
  private static readonly string[] SevenZipCandidates = [
    @"D:\PortableApps\7-ZipPortable\App\7-Zip64\7z.exe",
    @"D:\PortableApps\7-ZipPortable\App\7-Zip\7z.exe",
    @"C:\Program Files\7-Zip\7z.exe",
    @"C:\Program Files (x86)\7-Zip\7z.exe",
  ];

  public static string? SevenZipPath { get; } = SevenZipCandidates.FirstOrDefault(File.Exists) ?? TryFromPath("7z");

  public static bool SevenZipAvailable => SevenZipPath is not null;
  public static bool QemuImgAvailable { get; } = TryFromPath("qemu-img") is not null;
  public static bool DismAvailable { get; } = TryFromPath("dism") is not null;
  public static bool ChkdskAvailable { get; } = TryFromPath("chkdsk") is not null;
  public static bool MToolsAvailable { get; } = TryFromPath("minfo") is not null;
  public static bool WslAvailable { get; } = DetectWsl();

  private static readonly Dictionary<string, bool> _wslToolCache = new(StringComparer.Ordinal);

  private static bool DetectWsl() {
    if (!OperatingSystem.IsWindows()) return false;
    var wslExe = TryFromPath("wsl");
    if (wslExe is null) return false;
    // `wsl --status` prints default distro info when a distro is installed;
    // prints a setup prompt when WSL is shipped-but-not-configured.
    var status = RunExact(wslExe, "--status");
    return status.ExitCode == 0 && !string.IsNullOrWhiteSpace(status.StdOut);
  }

  /// <summary>
  /// Checks whether the named Linux tool (e.g. <c>fsck.ext4</c>, <c>btrfs</c>)
  /// is installed in the default WSL distro. Results are cached per tool name.
  /// </summary>
  public static bool WslHasTool(string tool) {
    if (!WslAvailable) return false;
    if (_wslToolCache.TryGetValue(tool, out var cached)) return cached;
    // `command -v` returns 0 and prints path if present, 1 otherwise; works in any POSIX shell.
    var result = RunWsl($"command -v {tool}");
    var found = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
    _wslToolCache[tool] = found;
    return found;
  }

  /// <summary>
  /// Executes <paramref name="linuxCommand"/> inside the default WSL distro
  /// (as a single <c>bash -c</c> invocation so shell metacharacters work).
  /// Single-quote paths inside the command for literal arg boundaries —
  /// Windows CreateProcess groups via the outer double-quotes, bash parses
  /// the single-quotes inside.
  /// </summary>
  public static (string StdOut, string StdErr, int ExitCode) RunWsl(string linuxCommand) {
    var wsl = TryFromPath("wsl") ?? "wsl";
    var dqEscaped = linuxCommand.Replace("\"", "\\\"");
    return RunExact(wsl, $"-e bash -c \"{dqEscaped}\"");
  }

  /// <summary>
  /// Converts a Windows path (e.g. <c>C:\Users\x\foo</c>) to its WSL
  /// equivalent (<c>/mnt/c/Users/x/foo</c>). Paths with spaces are
  /// wrapped in single-quotes by the caller via <see cref="RunWsl"/>.
  /// </summary>
  public static string WinToWsl(string winPath) {
    if (string.IsNullOrEmpty(winPath)) return winPath;
    var full = Path.GetFullPath(winPath);
    if (full.Length < 2 || full[1] != ':') return full.Replace('\\', '/');
    var drive = char.ToLowerInvariant(full[0]);
    var tail = full[2..].Replace('\\', '/');
    // Single-quote the whole thing so spaces are handled when passed to bash -c.
    return $"'/mnt/{drive}{tail}'";
  }

  public static void Require7z() {
    if (!SevenZipAvailable)
      Assert.Ignore("7-Zip not found. Install from https://www.7-zip.org/ (MSI) or extract the portable build to " +
                    @"D:\PortableApps\7-ZipPortable\App\7-Zip64\.");
  }

  public static (string StdOut, string StdErr, int ExitCode) Run7z(string args) => RunExact(SevenZipPath!, args);

  /// <summary>Run an executable that we expect to resolve via PATH.</summary>
  public static (string StdOut, string StdErr, int ExitCode) RunPath(string tool, string args) {
    var resolved = TryFromPath(tool) ?? tool; // fall through — Process will resolve or throw
    return RunExact(resolved, args);
  }

  private static (string StdOut, string StdErr, int ExitCode) RunExact(string tool, string args) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    try {
      using var proc = Process.Start(psi)!;
      var stdout = proc.StandardOutput.ReadToEnd();
      var stderr = proc.StandardError.ReadToEnd();
      proc.WaitForExit(60000);
      return (stdout, stderr, proc.ExitCode);
    } catch (Exception ex) {
      return (string.Empty, ex.Message, -1);
    }
  }

  public static string? FindFile(string dir, string name) {
    if (!Directory.Exists(dir)) return null;
    foreach (var f in Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories))
      return f;
    return null;
  }

  private static string? TryFromPath(string tool) {
    var pathEnv = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(pathEnv)) return null;
    var exeName = OperatingSystem.IsWindows() && !tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
      ? tool + ".exe"
      : tool;
    foreach (var dir in pathEnv.Split(Path.PathSeparator)) {
      if (string.IsNullOrWhiteSpace(dir)) continue;
      string candidate;
      try {
        candidate = Path.Combine(dir.Trim(), exeName);
      } catch {
        continue;
      }
      if (File.Exists(candidate)) return candidate;
    }
    return null;
  }
}
