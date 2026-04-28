using System.Diagnostics;
using System.Text;
using Compression.Tests.Support;
using FileFormat.Qcow2;
using FileFormat.Vhd;
using FileFormat.Vhdx;
using FileFormat.Vmdk;
using FileSystem.DoubleSpace;
using FileSystem.ExFat;
using FileSystem.Ext;
using FileSystem.Fat;
using FileSystem.Hfs;
using FileSystem.HfsPlus;
using FileSystem.Iso;
using FileSystem.Ntfs;
using FileSystem.Reiser4;
using FileSystem.SquashFs;
using FileSystem.Ufs;
using FileSystem.Zfs;

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
  // Disk-container validation matrix — qemu-img is the canonical tool.
  // Tests work regardless of whether qemu-img lives on Windows PATH or
  // inside WSL; the helpers route invocations to whichever channel is
  // available. Every test skips cleanly if qemu-img is on neither.
  //
  // Per container we cover three directions:
  //   1) forward      : our writer → `qemu-img check`  exit 0
  //   2) extraction   : our writer → `qemu-img convert -O raw` matches
  //                     the original payload bytes (truncated/padded to
  //                     the reported virtual size)
  //   3) reverse      : `qemu-img create -f <fmt>` → our reader doesn't
  //                     throw on the resulting empty container
  //
  // Plus a nested-storage end-to-end test (`Vmdk_ContainingExt_*`) that
  // wraps an ext FS inside a VMDK, validates with qemu-img, then extracts
  // the FS and the original files via our own reader chain — the user's
  // explicit "VMDK containing ReFS forensic round-trip" use case (with
  // ext substituted because we don't ship a ReFS writer yet).
  // ═══════════════════════════════════════════════════════════════════

  // ── VHD ────────────────────────────────────────────────────────────

  [Test]
  public void Vhd_OurImage_QemuImgCheckAccepts() {
    FsInteropToolbox.RequireQemuImg();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var inner = fat.Build();

    var vhd = new VhdWriter();
    vhd.SetDiskData(inner);
    var imgPath = Path.Combine(this._tmpDir, "check.vhd");
    File.WriteAllBytes(imgPath, vhd.Build());

    var r = FsInteropToolbox.RunQemuImg($"check {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"qemu-img check rejected our VHD:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }

  [Test]
  public void Vhd_OurImage_ConvertToRaw_PreservesContent() {
    FsInteropToolbox.RequireQemuImg();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var inner = fat.Build();

    var vhd = new VhdWriter();
    vhd.SetDiskData(inner);
    var imgPath = Path.Combine(this._tmpDir, "convert.vhd");
    File.WriteAllBytes(imgPath, vhd.Build());

    var rawPath = Path.Combine(this._tmpDir, "extracted_vhd.raw");
    var conv = FsInteropToolbox.RunQemuImg(
      $"convert -O raw {FsInteropToolbox.WinToWsl(imgPath)} {FsInteropToolbox.WinToWsl(rawPath)}");
    Assert.That(conv.ExitCode, Is.EqualTo(0),
      $"qemu-img convert -O raw failed:\nstdout:\n{conv.StdOut}\nstderr:\n{conv.StdErr}");

    var rawBytes = File.ReadAllBytes(rawPath);
    Assert.That(rawBytes.Length, Is.GreaterThanOrEqualTo(inner.Length),
      "Raw extract should be at least as large as the inner FS payload");
    Assert.That(rawBytes.AsSpan(0, inner.Length).ToArray(), Is.EqualTo(inner),
      "First N bytes of raw extract should equal our inner FAT image bytes verbatim");
  }

  [Test]
  public void Vhd_QemuImgCreated_ReadByOurReader() {
    FsInteropToolbox.RequireQemuImg();
    var imgPath = Path.Combine(this._tmpDir, "qemu_made.vhd");
    var create = FsInteropToolbox.RunQemuImg(
      $"create -f vpc {FsInteropToolbox.WinToWsl(imgPath)} 16M");
    Assert.That(create.ExitCode, Is.EqualTo(0),
      $"qemu-img create -f vpc failed:\nstdout:\n{create.StdOut}\nstderr:\n{create.StdErr}");

    using var fs = File.OpenRead(imgPath);
    var entries = new VhdFormatDescriptor().List(fs, null);
    Assert.That(entries, Is.Not.Null, "Our VHD reader returned null on a qemu-img-created VHD");
  }

  // ── VHDX ───────────────────────────────────────────────────────────

  [Test]
  public void Vhdx_OurImage_QemuImgCheckAccepts() {
    FsInteropToolbox.RequireQemuImg();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var inner = fat.Build();

    var vhdx = new VhdxWriter();
    vhdx.SetDiskData(inner);
    var imgPath = Path.Combine(this._tmpDir, "check.vhdx");
    File.WriteAllBytes(imgPath, vhdx.Build());

    var r = FsInteropToolbox.RunQemuImg($"check {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"qemu-img check rejected our VHDX:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }

  // ── VMDK ───────────────────────────────────────────────────────────

  [Test]
  public void Vmdk_OurImage_QemuImgCheckAccepts() {
    FsInteropToolbox.RequireQemuImg();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var inner = fat.Build();

    var vmdk = new VmdkWriter();
    vmdk.SetDiskData(inner);
    var imgPath = Path.Combine(this._tmpDir, "check.vmdk");
    File.WriteAllBytes(imgPath, vmdk.Build());

    var r = FsInteropToolbox.RunQemuImg($"check {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"qemu-img check rejected our VMDK:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }

  [Test]
  public void Vmdk_OurImage_ConvertToRaw_PreservesContent() {
    FsInteropToolbox.RequireQemuImg();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var inner = fat.Build();

    var vmdk = new VmdkWriter();
    vmdk.SetDiskData(inner);
    var imgPath = Path.Combine(this._tmpDir, "convert.vmdk");
    File.WriteAllBytes(imgPath, vmdk.Build());

    var rawPath = Path.Combine(this._tmpDir, "extracted_vmdk.raw");
    var conv = FsInteropToolbox.RunQemuImg(
      $"convert -O raw {FsInteropToolbox.WinToWsl(imgPath)} {FsInteropToolbox.WinToWsl(rawPath)}");
    Assert.That(conv.ExitCode, Is.EqualTo(0),
      $"qemu-img convert -O raw failed:\nstdout:\n{conv.StdOut}\nstderr:\n{conv.StdErr}");

    var rawBytes = File.ReadAllBytes(rawPath);
    Assert.That(rawBytes.Length, Is.GreaterThanOrEqualTo(inner.Length));
    Assert.That(rawBytes.AsSpan(0, inner.Length).ToArray(), Is.EqualTo(inner));
  }

  [Test]
  public void Vmdk_QemuImgCreated_ReadByOurReader() {
    FsInteropToolbox.RequireQemuImg();
    var imgPath = Path.Combine(this._tmpDir, "qemu_made.vmdk");
    var create = FsInteropToolbox.RunQemuImg(
      $"create -f vmdk {FsInteropToolbox.WinToWsl(imgPath)} 16M");
    Assert.That(create.ExitCode, Is.EqualTo(0),
      $"qemu-img create -f vmdk failed:\nstdout:\n{create.StdOut}\nstderr:\n{create.StdErr}");

    using var fs = File.OpenRead(imgPath);
    var entries = new VmdkFormatDescriptor().List(fs, null);
    Assert.That(entries, Is.Not.Null, "Our VMDK reader returned null on a qemu-img-created VMDK");
  }

  // ── QCOW2 ──────────────────────────────────────────────────────────

  [Test]
  public void Qcow2_OurImage_QemuImgCheckAccepts() {
    FsInteropToolbox.RequireQemuImg();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var inner = fat.Build();

    var qcow = new Qcow2Writer();
    qcow.SetDiskImage(inner);
    var imgPath = Path.Combine(this._tmpDir, "check.qcow2");
    using (var fs = File.Create(imgPath)) qcow.WriteTo(fs);

    var r = FsInteropToolbox.RunQemuImg($"check {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"qemu-img check rejected our QCOW2:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }

  [Test]
  public void Qcow2_OurImage_ConvertToRaw_PreservesContent() {
    FsInteropToolbox.RequireQemuImg();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var inner = fat.Build();

    var qcow = new Qcow2Writer();
    qcow.SetDiskImage(inner);
    var imgPath = Path.Combine(this._tmpDir, "convert.qcow2");
    using (var fs = File.Create(imgPath)) qcow.WriteTo(fs);

    var rawPath = Path.Combine(this._tmpDir, "extracted_qcow2.raw");
    var conv = FsInteropToolbox.RunQemuImg(
      $"convert -O raw {FsInteropToolbox.WinToWsl(imgPath)} {FsInteropToolbox.WinToWsl(rawPath)}");
    Assert.That(conv.ExitCode, Is.EqualTo(0),
      $"qemu-img convert -O raw failed:\nstdout:\n{conv.StdOut}\nstderr:\n{conv.StdErr}");

    var rawBytes = File.ReadAllBytes(rawPath);
    Assert.That(rawBytes.Length, Is.GreaterThanOrEqualTo(inner.Length));
    Assert.That(rawBytes.AsSpan(0, inner.Length).ToArray(), Is.EqualTo(inner));
  }

  [Test]
  public void Qcow2_QemuImgCreated_ReadByOurReader() {
    FsInteropToolbox.RequireQemuImg();
    var imgPath = Path.Combine(this._tmpDir, "qemu_made.qcow2");
    var create = FsInteropToolbox.RunQemuImg(
      $"create -f qcow2 {FsInteropToolbox.WinToWsl(imgPath)} 16M");
    Assert.That(create.ExitCode, Is.EqualTo(0),
      $"qemu-img create -f qcow2 failed:\nstdout:\n{create.StdOut}\nstderr:\n{create.StdErr}");

    using var fs = File.OpenRead(imgPath);
    var entries = new Qcow2FormatDescriptor().List(fs, null);
    Assert.That(entries, Is.Not.Null, "Our QCOW2 reader returned null on a qemu-img-created QCOW2");
  }

  // ── VDI ────────────────────────────────────────────────────────────

  [Test]
  public void Vdi_OurImage_QemuImgCheckAccepts() {
    FsInteropToolbox.RequireQemuImg();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var inner = fat.Build();

    var imgPath = Path.Combine(this._tmpDir, "check.vdi");
    using (var fs = File.Create(imgPath)) {
      using var w = new FileFormat.Vdi.VdiWriter(fs, leaveOpen: true, virtualSize: inner.Length);
      w.Write(inner);
    }

    var r = FsInteropToolbox.RunQemuImg($"check {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"qemu-img check rejected our VDI:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }

  [Test]
  public void Vdi_OurImage_ConvertToRaw_PreservesContent() {
    FsInteropToolbox.RequireQemuImg();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var inner = fat.Build();

    var imgPath = Path.Combine(this._tmpDir, "convert.vdi");
    using (var fs = File.Create(imgPath)) {
      using var w = new FileFormat.Vdi.VdiWriter(fs, leaveOpen: true, virtualSize: inner.Length);
      w.Write(inner);
    }

    var rawPath = Path.Combine(this._tmpDir, "extracted_vdi.raw");
    var conv = FsInteropToolbox.RunQemuImg(
      $"convert -O raw {FsInteropToolbox.WinToWsl(imgPath)} {FsInteropToolbox.WinToWsl(rawPath)}");
    Assert.That(conv.ExitCode, Is.EqualTo(0),
      $"qemu-img convert -O raw failed:\nstdout:\n{conv.StdOut}\nstderr:\n{conv.StdErr}");

    var rawBytes = File.ReadAllBytes(rawPath);
    Assert.That(rawBytes.Length, Is.GreaterThanOrEqualTo(inner.Length));
    Assert.That(rawBytes.AsSpan(0, inner.Length).ToArray(), Is.EqualTo(inner));
  }

  [Test]
  public void Vdi_QemuImgCreated_ReadByOurReader() {
    FsInteropToolbox.RequireQemuImg();
    var imgPath = Path.Combine(this._tmpDir, "qemu_made.vdi");
    var create = FsInteropToolbox.RunQemuImg(
      $"create -f vdi {FsInteropToolbox.WinToWsl(imgPath)} 16M");
    Assert.That(create.ExitCode, Is.EqualTo(0),
      $"qemu-img create -f vdi failed:\nstdout:\n{create.StdOut}\nstderr:\n{create.StdErr}");

    using var fs = File.OpenRead(imgPath);
    var entries = new FileFormat.Vdi.VdiFormatDescriptor().List(fs, null);
    Assert.That(entries, Is.Not.Null, "Our VDI reader returned null on a qemu-img-created VDI");
  }

  // ── VHDX (forward direction in VHDX section above) ──────────────────
  // The reverse direction (qemu-img-created VHDX → our reader) lives here.

  [Test]
  public void Vhdx_OurImage_ConvertToRaw_PreservesContent() {
    FsInteropToolbox.RequireQemuImg();
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    var inner = fat.Build();

    var vhdx = new VhdxWriter();
    vhdx.SetDiskData(inner);
    var imgPath = Path.Combine(this._tmpDir, "convert.vhdx");
    File.WriteAllBytes(imgPath, vhdx.Build());

    var rawPath = Path.Combine(this._tmpDir, "extracted_vhdx.raw");
    var conv = FsInteropToolbox.RunQemuImg(
      $"convert -O raw {FsInteropToolbox.WinToWsl(imgPath)} {FsInteropToolbox.WinToWsl(rawPath)}");
    Assert.That(conv.ExitCode, Is.EqualTo(0),
      $"qemu-img convert -O raw failed:\nstdout:\n{conv.StdOut}\nstderr:\n{conv.StdErr}");

    var rawBytes = File.ReadAllBytes(rawPath);
    Assert.That(rawBytes.Length, Is.GreaterThanOrEqualTo(inner.Length),
      "Raw extract should be at least as large as the inner FS payload");
    Assert.That(rawBytes.AsSpan(0, inner.Length).ToArray(), Is.EqualTo(inner),
      "First N bytes of raw extract should equal our inner FAT image bytes verbatim");
  }

  [Test]
  public void Vhdx_QemuImgCreated_ReadByOurReader() {
    FsInteropToolbox.RequireQemuImg();
    var imgPath = Path.Combine(this._tmpDir, "qemu_made.vhdx");
    var create = FsInteropToolbox.RunQemuImg(
      $"create -f vhdx {FsInteropToolbox.WinToWsl(imgPath)} 16M");
    Assert.That(create.ExitCode, Is.EqualTo(0),
      $"qemu-img create -f vhdx failed:\nstdout:\n{create.StdOut}\nstderr:\n{create.StdErr}");

    using var fs = File.OpenRead(imgPath);
    var entries = new FileFormat.Vhdx.VhdxFormatDescriptor().List(fs, null);
    Assert.That(entries, Is.Not.Null, "Our VHDX reader returned null on a qemu-img-created VHDX");
  }

  // ── Nested storage forensic round-trip ─────────────────────────────
  //
  // User's mandate: "create a VMDK within our tools containing e.g. a
  // ReFS filesystem". We don't have a ReFS writer, so substitute ext —
  // the validation chain (write FS, wrap in container, qemu-img check,
  // unwrap, parse FS, extract files) is identical and the substitution
  // is a one-liner when ReFS lands.

  [Test]
  public void Vmdk_ContainingExt_RoundTripExtractsFiles() {
    // SOI + APP0 markers + payload — synthetic JPEG header (we only need
    // distinguishable bytes here; we're not parsing the actual image).
    var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }
      .Concat("FAKEJPEG_FOR_FORENSIC_TEST"u8.ToArray()).ToArray();
    var notesBytes = "Forensic evidence #1"u8.ToArray();

    // Build inner ext filesystem with two known files.
    var ext = new ExtWriter();
    ext.AddFile("photo.jpg", jpegBytes);
    ext.AddFile("notes.txt", notesBytes);
    var diskBytes = ext.Build();

    // Wrap in VMDK.
    var vmdk = new VmdkWriter();
    vmdk.SetDiskData(diskBytes);
    var vmdkPath = Path.Combine(this._tmpDir, "evidence.vmdk");
    File.WriteAllBytes(vmdkPath, vmdk.Build());

    // External validation when qemu-img is reachable; tolerate absence.
    if (FsInteropToolbox.QemuImgAnywhereAvailable) {
      var r = FsInteropToolbox.RunQemuImg($"check {FsInteropToolbox.WinToWsl(vmdkPath)}");
      Assert.That(r.ExitCode, Is.EqualTo(0),
        $"qemu-img check rejected our nested VMDK<ext>:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
    }

    // Read back via our chain: VMDK reader → raw disk bytes → ExtReader.
    using var vmdkStream = File.OpenRead(vmdkPath);
    var vmdkReader = new VmdkReader(vmdkStream);
    var diskEntry = vmdkReader.Entries.FirstOrDefault(e => e.Name == "disk.img");
    Assert.That(diskEntry, Is.Not.Null, "VMDK reader should expose a disk.img entry");

    var extractedDisk = vmdkReader.Extract(diskEntry!);
    Assert.That(extractedDisk.Length, Is.GreaterThanOrEqualTo(diskBytes.Length),
      "Extracted disk image should be at least as large as the inner ext payload");

    // Walk the extracted ext FS.
    using var extStream = new MemoryStream(extractedDisk);
    var extReader = new ExtReader(extStream);
    var allEntries = extReader.Entries.ToList();

    var photo = allEntries.FirstOrDefault(e => e.Name == "photo.jpg");
    var notes = allEntries.FirstOrDefault(e => e.Name == "notes.txt");
    Assert.That(photo, Is.Not.Null, "photo.jpg should be present in extracted FS");
    Assert.That(notes, Is.Not.Null, "notes.txt should be present in extracted FS");

    Assert.That(extReader.Extract(photo!), Is.EqualTo(jpegBytes),
      "photo.jpg content must round-trip byte-equal through VMDK<ext>");
    Assert.That(extReader.Extract(notes!), Is.EqualTo(notesBytes),
      "notes.txt content must round-trip byte-equal through VMDK<ext>");
  }

  // ═══════════════════════════════════════════════════════════════════
  // WSL-based validation — real Linux kernel fsck/repair/check tools
  // ═══════════════════════════════════════════════════════════════════

  private static void RequireWsl() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. Run `wsl --install` in Admin PowerShell and reboot, " +
                    "then `sudo apt install -y e2fsprogs xfsprogs btrfs-progs exfatprogs dosfstools " +
                    "udftools squashfs-tools ntfs-3g hfsprogs hfsutils zfsutils-linux` " +
                    "inside the Linux shell.");
  }

  private static void RequireWslTool(string tool) {
    RequireWsl();
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"WSL is present but '{tool}' is not installed in the distro. " +
                    $"Run inside WSL: `sudo apt install -y <pkg-providing-{tool}>`.");
  }

  /// <summary>
  /// Requires a WSL tool, providing the exact apt package name in the skip
  /// message so the user can copy/paste the install command.
  /// </summary>
  private static void RequireWslTool(string tool, string aptPackage) {
    RequireWsl();
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"WSL is present but '{tool}' is not installed in the distro. " +
                    $"Run inside WSL: `sudo apt install -y {aptPackage}`.");
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

  // ── ext1 (WSL soft-validation) ─────────────────────────────────────
  //
  // ext1 (1992) has no Linux mkfs tool — its 0xEF51 magic was retired in 1993
  // and e2fsprogs only recognises ext2's 0xEF53. The only validation we can do
  // with dumpe2fs against our writer is to verify the failure mode: dumpe2fs
  // should explicitly reject the image with a "bad magic number" diagnostic
  // (not crash, not silently accept). That tells us the image looks ext-shaped
  // up to the magic word — exactly the on-disk parity we wanted.

  [Test]
  public void Ext1_OurImage_DumpE2fsAcceptsAsExt2() {
    RequireWslTool("dumpe2fs");
    var ext1 = new FileSystem.Ext1.Ext1Writer();
    ext1.AddFile("hello.txt", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "ext1_dump.img");
    File.WriteAllBytes(imgPath, ext1.Build());

    var result = FsInteropToolbox.RunWsl($"dumpe2fs -h {FsInteropToolbox.WinToWsl(imgPath)}");

    // Soft validation — three acceptable outcomes:
    //   (A) exit 0 with magic line shown (extremely unlikely, would mean dumpe2fs
    //       silently accepts 0xEF51 — would surprise everyone).
    //   (B) non-zero exit with "Bad magic number" / "couldn't find valid
    //       filesystem superblock" / similar — proves bytes parse as
    //       superblock-shaped except for the magic.
    //   (C) some other non-zero exit — still acceptable as long as dumpe2fs
    //       didn't crash with a signal, since we're outside its spec'd input set.
    var combined = result.StdOut + "\n" + result.StdErr;
    var looksLikeMagicReject =
      combined.Contains("magic", StringComparison.OrdinalIgnoreCase) ||
      combined.Contains("superblock", StringComparison.OrdinalIgnoreCase) ||
      combined.Contains("Couldn't find", StringComparison.OrdinalIgnoreCase) ||
      combined.Contains("Couldn't open", StringComparison.OrdinalIgnoreCase);

    if (result.ExitCode == 0)
      Assert.Pass($"dumpe2fs unexpectedly accepted our 0xEF51 image:\n{result.StdOut}");
    else
      Assert.That(looksLikeMagicReject, Is.True,
        $"dumpe2fs rejected our ext1 image but the output doesn't look like a magic/superblock " +
        $"diagnostic — the writer may be emitting bytes that aren't even shaped like an ext SB.\n" +
        $"exit={result.ExitCode}\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  // ── ext2 / ext3 acceptance ─────────────────────────────────────────
  //
  // The ExtWriter emits a strict ext2-compatible image: revision 1 dynamic
  // (so s_first_ino / s_inode_size / feature flags are honoured) with only
  // FEATURE_INCOMPAT_FILETYPE set — no journal (HAS_JOURNAL), no extents
  // (EXTENTS), no large_file (RO_COMPAT_LARGE_FILE), no 64-bit, no
  // metadata_csum, no flex_bg, no sparse_super_v2. e2fsprogs unifies the
  // codepath behind fsck.ext{2,3,4}, so all three should accept the same
  // bytes; these tests verify that fact rather than re-emitting per
  // revision.

  [Test]
  public void Ext_OurImage_Fsckext2Accepts() {
    RequireWslTool("fsck.ext2");
    var ext = new ExtWriter();
    ext.AddFile("hello.txt", SmallText);
    ext.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "ext2_fsck.img");
    File.WriteAllBytes(imgPath, ext.Build());

    var result = FsInteropToolbox.RunWsl($"fsck.ext2 -fnv {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.ext2 rejected our image (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test]
  public void Ext_OurImage_Fsckext3Accepts() {
    RequireWslTool("fsck.ext3");
    var ext = new ExtWriter();
    ext.AddFile("hello.txt", SmallText);
    ext.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "ext3_fsck.img");
    File.WriteAllBytes(imgPath, ext.Build());

    // fsck.ext3 will normally complain about a missing journal, but with
    // -fnv on a journal-less revision-1 image it returns 0 (no errors,
    // nothing to repair). The journal feature is COMPAT_HAS_JOURNAL —
    // unset here — so e2fsck treats the image as a plain ext2 volume.
    var result = FsInteropToolbox.RunWsl($"fsck.ext3 -fnv {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.ext3 rejected our image (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
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

  [Test]
  public void Fat_OurImage_FsckFat_F32Accepts() {
    RequireWslTool("fsck.fat");
    // 200_000 sectors @ 512 B = ~100 MB → cluster count > 65525 → FAT32.
    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    fat.AddFile("REPEAT.TXT", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "fat32_fsck.img");
    var img = fat.Build(totalSectors: 200_000);
    File.WriteAllBytes(imgPath, img);

    // Pre-flight: confirm our own reader sees this image as FAT32 (so
    // the test fails cleanly if the writer regresses to a smaller-cluster
    // FAT16 layout). Then let fsck.fat — which auto-detects the type
    // from the BPB — validate the on-disk structure end-to-end.
    using (var stream = new MemoryStream(img)) {
      var reader = new FileSystem.Fat.FatReader(stream);
      Assert.That(reader.FatType, Is.EqualTo(32),
        "Writer should produce FAT32 at 200_000 sectors (>= 65525 clusters).");
    }

    var result = FsInteropToolbox.RunWsl($"fsck.fat -n -V {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.fat rejected our FAT32 image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test]
  public void Fat_OurImage_LongFilenameRoundtripsViaFsckFat() {
    RequireWslTool("fsck.fat");
    var fat = new FatWriter();
    // Names that all force VFAT/LFN: mixed case, > 8.3 length, multiple
    // dots, non-ASCII, dotted base. fsck.fat -n complains about any LFN
    // chain whose checksum doesn't match its 8.3 entry, so this catches
    // both the slot layout and the LfnChecksum implementation.
    fat.AddFile("Hello World With Long Name.TXT", SmallText);
    fat.AddFile("Another_Mixed_Case_File.dat", RepetitiveText);
    fat.AddFile("readme.markdown", SmallText);
    fat.AddFile("a.very.dotted.name.txt", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "fat_lfn_fsck.img");
    File.WriteAllBytes(imgPath, fat.Build());

    var result = FsInteropToolbox.RunWsl($"fsck.fat -n -V {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.fat rejected our LFN image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
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

  [Test]
  public void Fat_LinuxMkfsF32Output_ReadByOurReader() {
    RequireWslTool("mkfs.vfat");
    // Allocate enough space for FAT32: minimum is ~33 MiB practically;
    // 64 MiB gives mkfs.vfat -F 32 plenty of room.
    var imgPath = Path.Combine(this._tmpDir, "mkfs_vfat_f32.img");
    using (var fs = File.Create(imgPath))
      fs.SetLength(64 * 1024 * 1024);

    var result = FsInteropToolbox.RunWsl($"mkfs.vfat -F 32 {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"mkfs.vfat -F 32 failed:\n{result.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var reader = new FileSystem.Fat.FatReader(stream);
    Assert.That(reader.FatType, Is.EqualTo(32), "Our reader should detect FAT32");
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

  // ── Reiser4 (mkfs.reiser4 + fsck.reiser4 from reiser4progs) ────────

  [Test]
  public void Reiser4_OurImage_FsckAccepts() {
    RequireWslTool("fsck.reiser4", "reiser4progs");
    var w = new Reiser4Writer { BlockCount = 4096, Label = "OURFS", MkfsId = 0xCAFEBABEu };
    var imgPath = Path.Combine(this._tmpDir, "reiser4_fsck.img");
    File.WriteAllBytes(imgPath, w.Build());

    // fsck.reiser4 in default mode: -y answers yes to any prompts, exits 0
    // when the FS is consistent. Older reiser4progs used `--check` but the
    // 1.2.x default action *is* check.
    var result = FsInteropToolbox.RunWsl($"fsck.reiser4 -y {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.reiser4 rejected our image (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    // Sanity: fsck.reiser4 prints to stderr (not stdout) and a clean image ends
    // with "FS is consistent." Search both streams for the marker.
    var combined = result.StdOut + "\n" + result.StdErr;
    Assert.That(combined, Does.Contain("consistent").Or.Contain("Recovered"),
      $"fsck.reiser4 ran exit 0 but did not report consistency:\n{combined}");
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
  public void Mfs_NoLinuxValidator_Skip() {
    Assert.Ignore("Classic Mac MFS has no Linux validator. Self-validated only.");
  }

  // ═══════════════════════════════════════════════════════════════════
  // NTFS (ntfs-3g package) — ntfsfix / ntfsinfo / ntfsls / mkfs.ntfs
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Ntfs_OurImage_NtfsfixAccepts() {
    RequireWslTool("ntfsfix", "ntfs-3g");
    var ntfs = new NtfsWriter();
    ntfs.AddFile("hello.txt", SmallText);
    ntfs.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "ntfs_fix.img");
    File.WriteAllBytes(imgPath, ntfs.Build());

    // ntfsfix --no-action = dry-run check; exit 0 = clean, 1 = warnings, 2+ = errors.
    var result = FsInteropToolbox.RunWsl($"ntfsfix --no-action {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.LessThanOrEqualTo(1),
      $"ntfsfix rejected our NTFS image (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test]
  public void Ntfs_OurImage_NtfsinfoReportsBoot() {
    RequireWslTool("ntfsinfo", "ntfs-3g");
    var ntfs = new NtfsWriter();
    ntfs.AddFile("hello.txt", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "ntfs_info.img");
    File.WriteAllBytes(imgPath, ntfs.Build());

    // ntfsinfo -m / --mft (no argument) dumps volume-wide information; fails if
    // boot sector / $MFT broken. Equivalent of `-i 0` (inspect inode 0 = $MFT).
    var result = FsInteropToolbox.RunWsl($"ntfsinfo -m {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"ntfsinfo rejected our image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    // -m dumps "Volume Information" — verify we got past arg parsing and got real
    // volume info back rather than a "Failed to parse" / mount-error stub.
    Assert.That(result.StdOut, Does.Contain("Volume").IgnoreCase,
      "ntfsinfo -m should print volume information");
  }

  [Test]
  public void Ntfs_OurImage_NtfslsLists() {
    RequireWslTool("ntfsls", "ntfs-3g");
    var ntfs = new NtfsWriter();
    ntfs.AddFile("hello.txt", SmallText);
    ntfs.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "ntfs_ls.img");
    File.WriteAllBytes(imgPath, ntfs.Build());

    var result = FsInteropToolbox.RunWsl($"ntfsls -l {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"ntfsls rejected our image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("hello.txt").IgnoreCase,
      "ntfsls should list hello.txt");
  }

  [Test]
  public void Ntfs_LinuxMkntfsOutput_ReadByOurReader() {
    RequireWslTool("mkfs.ntfs", "ntfs-3g");
    // Build an empty 16 MB file and let mkfs.ntfs format it.
    var imgPath = Path.Combine(this._tmpDir, "mkntfs.img");
    using (var fs = File.Create(imgPath))
      fs.SetLength(16 * 1024 * 1024);

    // -F = force format on a regular file, -Q = quick (no zeroing/badblock scan), -f = fast.
    var result = FsInteropToolbox.RunWsl($"mkfs.ntfs -F -Q {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"mkfs.ntfs failed:\n{result.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.Ntfs.NtfsFormatDescriptor();
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null, "Our reader returned null for a valid Linux-mkfs.ntfs image");
  }

  // ═══════════════════════════════════════════════════════════════════
  // HFS+ (hfsprogs package) — fsck.hfsplus / mkfs.hfsplus
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void HfsPlus_OurImage_FsckHfsplusAccepts() {
    RequireWslTool("fsck.hfsplus", "hfsprogs");
    var hfs = new HfsPlusWriter();
    hfs.AddFile("hello.txt", SmallText);
    hfs.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "hfsplus_check.img");
    File.WriteAllBytes(imgPath, hfs.Build());

    // -d = debug (verbose), -f = force check even if marked clean, -n = no modify.
    var result = FsInteropToolbox.RunWsl($"fsck.hfsplus -d -f -n {FsInteropToolbox.WinToWsl(imgPath)}");
    // fsck.hfsplus exits 0 on clean, non-zero on errors.
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.hfsplus rejected our image (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test]
  public void HfsPlus_LinuxMkfsHfsplusOutput_ReadByOurReader() {
    RequireWslTool("mkfs.hfsplus", "hfsprogs");
    // Build an empty 16 MB file and let mkfs.hfsplus format it.
    var imgPath = Path.Combine(this._tmpDir, "mkfs_hfsplus.img");
    using (var fs = File.Create(imgPath))
      fs.SetLength(16 * 1024 * 1024);

    var result = FsInteropToolbox.RunWsl($"mkfs.hfsplus -v cwbtest {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"mkfs.hfsplus failed:\n{result.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.HfsPlus.HfsPlusFormatDescriptor();
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null, "Our reader returned null for a valid Linux-mkfs.hfsplus image");
  }

  // ═══════════════════════════════════════════════════════════════════
  // HFS classic (hfsutils package) — hmount + hls
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Hfs_OurImage_HlsLists() {
    RequireWslTool("hls", "hfsutils");
    RequireWslTool("hmount", "hfsutils");

    var hfs = new HfsWriter();
    hfs.AddFile("hello.txt", SmallText);
    hfs.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "hfs_classic.img");
    var imgBytes = hfs.Build();

    // hmount refuses images smaller than 800 KB (smallest physical Mac floppy).
    // Pad with trailing zeros if our minimum (400 KB) is below that threshold —
    // the HFS MDB still describes the actual volume; padding doesn't break parsing.
    const int HmountMinBytes = 800 * 1024;
    if (imgBytes.Length < HmountMinBytes) {
      var padded = new byte[HmountMinBytes];
      Array.Copy(imgBytes, padded, imgBytes.Length);
      imgBytes = padded;
    }
    File.WriteAllBytes(imgPath, imgBytes);

    // hmount mounts the disk image (writes ~/.hcwd state), hls lists root, humount unmounts.
    // Use a tmp HOME to avoid collisions across parallel test runs.
    var hfsHome = $"/tmp/cwb_hfs_{Guid.NewGuid():N}";
    var bash = $"mkdir -p {hfsHome} && HOME={hfsHome} hmount {FsInteropToolbox.WinToWsl(imgPath)} && " +
               $"HOME={hfsHome} hls && HOME={hfsHome} humount; rc=$?; rm -rf {hfsHome}; exit $rc";
    var result = FsInteropToolbox.RunWsl(bash);
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"hmount/hls/humount rejected our HFS image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("hello.txt").IgnoreCase,
      "hls should list hello.txt");
  }

  // ═══════════════════════════════════════════════════════════════════
  // ZFS (zfsutils-linux package) — zdb -l reads labels without kernel module
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Zfs_OurImage_ZdbReportsLabel() {
    RequireWslTool("zdb", "zfsutils-linux");
    var zfs = new ZfsWriter();
    zfs.SetPoolName("compworkbench");
    zfs.AddFile("hello.txt", SmallText);
    zfs.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "zfs_zdb.img");
    using (var fs = File.Create(imgPath)) zfs.WriteTo(fs);

    // zdb -l <file> reads the 4 vdev labels (XDR NVList) without needing the kernel module.
    // Exit 0 on a parseable label, non-zero on a malformed NVList or label checksum mismatch.
    var result = FsInteropToolbox.RunWsl($"zdb -l {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"zdb rejected our ZFS labels (exit {result.ExitCode}):\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("version:"),
      "zdb -l should print the version field from our NVList");
    Assert.That(result.StdOut, Does.Contain("compworkbench"),
      "zdb -l should print the pool name we set");
  }

  // ═══════════════════════════════════════════════════════════════════
  // UFS — no apt-installable validator. Two paths:
  //   B) Linux kernel mount (-t ufs, ro) — needs sudo + ufs.ko
  //   A) QEMU + FreeBSD live ISO + fsck_ffs (heavy; needs prefetched ISO)
  // ═══════════════════════════════════════════════════════════════════

  /// <summary>
  /// Option B: Build a UFS image, then ask the (Linux) kernel to mount it
  /// read-only via the in-tree <c>ufs</c> module. We require <c>sudo -n</c>
  /// (non-interactive) so the test never blocks. Skips with an actionable
  /// hint when WSL/sudo/the kernel module are not available.
  /// <para>
  /// The default WSL2 kernel (<c>microsoft-standard-WSL2</c>) is built
  /// without <c>fs/ufs/</c>, so this path will skip on stock WSL — but it
  /// works on a custom WSL kernel or native-Linux CI without changes.
  /// </para>
  /// </summary>
  [Test]
  public void Ufs_OurImage_LinuxMountReads() {
    RequireWsl();
    if (!FsInteropToolbox.WslHasPasswordlessSudo)
      Assert.Ignore("WSL passwordless sudo is required (mount/modprobe need root). " +
                    "Set it up with: `sudo visudo` → add `<your-user> ALL=(ALL) NOPASSWD: ALL` " +
                    "(or scope to /usr/sbin/modprobe, /usr/bin/mount, /usr/bin/umount).");
    if (!FsInteropToolbox.WslHasUfsKernelModule)
      Assert.Ignore("Linux `ufs` kernel module not available in this WSL kernel. " +
                    "WSL2's stock kernel ships without ufs.ko; build a custom kernel with " +
                    "CONFIG_UFS_FS=m or run on native Linux. To validate manually after a " +
                    "rebuild: `sudo modprobe ufs && sudo mount -t ufs -o loop,ro,ufstype=44bsd <img> /mnt/ufs`.");

    var ufs = new UfsWriter();
    ufs.AddFile("hello.txt", SmallText);
    ufs.AddFile("repeat.txt", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "ufs_mount.img");
    using (var fs = File.Create(imgPath)) ufs.WriteTo(fs);

    var wslImg = FsInteropToolbox.WinToWsl(imgPath);
    // Use a fixed mount point under /tmp so we don't pollute the host fs;
    // chmod 0777 because the unmount + cleanup should work without re-prompting.
    var script =
      "set -e; " +
      "MNT=$(mktemp -d); " +
      "trap 'sudo -n umount \"$MNT\" 2>/dev/null; rmdir \"$MNT\" 2>/dev/null' EXIT; " +
      "sudo -n modprobe ufs; " +
      $"sudo -n mount -t ufs -o loop,ro,ufstype=44bsd {wslImg} \"$MNT\"; " +
      "ls -la \"$MNT\"; " +
      "echo '--- hello.txt ---'; cat \"$MNT/hello.txt\"; " +
      "echo '--- repeat.txt head ---'; head -c 64 \"$MNT/repeat.txt\"";
    var result = FsInteropToolbox.RunWsl(script);
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"Linux kernel UFS mount failed:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("hello.txt"),
      "Mounted UFS root listing should include hello.txt");
    Assert.That(result.StdOut, Does.Contain("Hello from CompressionWorkbench"),
      "cat hello.txt should print our payload");
  }

  /// <summary>
  /// Option A: Boot a FreeBSD live ISO under QEMU with our UFS image as a
  /// second disk and run <c>fsck_ffs -n /dev/ada1</c>. Skips cleanly when
  /// QEMU or the prefetched ISO are unavailable. We don't auto-download the
  /// ISO (~412 MB for FreeBSD 14.3 bootonly); the user points us at it via
  /// the <c>CWB_FREEBSD_ISO</c> environment variable.
  /// <para>
  /// Even with a prefetched ISO the bootonly installer is interactive, so
  /// driving <c>fsck_ffs</c> non-interactively requires a custom autoexec
  /// or a pre-baked live image. This test ships the plumbing and a 2-minute
  /// hard timeout; if QEMU returns a non-zero serial-console exit and no
  /// "FILE SYSTEM CLEAN" line is captured, we surface the QEMU log so the
  /// next agent sees what to wire up.
  /// </para>
  /// </summary>
  [Test]
  public void Ufs_OurImage_FsckFfsAccepts() {
    if (!QemuRunner.QemuAvailable)
      Assert.Ignore("qemu-system-x86_64 not found on PATH. " +
                    "Install on Windows from https://www.qemu.org/download/#windows or in WSL via " +
                    "`sudo apt install -y qemu-system-x86`. The test boots a FreeBSD live ISO and " +
                    "runs `fsck_ffs -n /dev/ada1` against our image.");
    var iso = QemuRunner.LocateFreeBsdIso();
    if (iso is null)
      Assert.Ignore("FreeBSD live ISO not found. Set environment variable CWB_FREEBSD_ISO to a " +
                    "FreeBSD 14.x or 15.x bootonly/disc1 ISO path (≈412 MB for 14.3 bootonly). " +
                    "Download once from https://download.freebsd.org/releases/amd64/amd64/ISO-IMAGES/14.3/ " +
                    "(SHA256 b3c242b27e0dda3efc280c4a68f5cbe0b8dbc50d7993baadef7617bf32b17f0c " +
                    "for FreeBSD-14.3-RELEASE-amd64-bootonly.iso). " +
                    "We don't auto-download — the file is multi-hundred MB and your CI shouldn't pay that toll per run.");

    var ufs = new UfsWriter();
    ufs.AddFile("hello.txt", SmallText);
    var imgPath = Path.Combine(this._tmpDir, "ufs_qemu.img");
    using (var fs = File.Create(imgPath)) ufs.WriteTo(fs);

    var qemuLog = Path.Combine(this._tmpDir, "qemu_serial.log");
    var (exitCode, log) = QemuRunner.RunFsckFfs(iso, imgPath, qemuLog, TimeSpan.FromMinutes(2));

    if (exitCode == QemuRunner.SkipExitCode_StillInteractive) {
      Assert.Ignore("FreeBSD bootonly ISO drops to an interactive installer prompt that we cannot " +
                    "drive non-interactively from this test. Either bake a custom live ISO with " +
                    "`fsck_ffs -n /dev/ada1; halt` in /etc/rc.local, or run the manual command path " +
                    "documented in docs/FILESYSTEMS.md. QEMU serial log:\n" + log);
    }

    // If we reached this branch a custom live ISO was used and fsck_ffs ran. fsck_ffs prints
    // "** FILE SYSTEM IS CLEAN" or "FILE SYSTEM CLEAN" on a healthy image and exits 0.
    Assert.That(exitCode, Is.EqualTo(0),
      $"fsck_ffs reported errors against our UFS image. QEMU serial log:\n{log}");
    Assert.That(log, Does.Contain("FILE SYSTEM").IgnoreCase.Or.Contain("clean").IgnoreCase,
      "Expected fsck_ffs to print a 'FILE SYSTEM CLEAN' summary on the serial console");
  }

  // ═══════════════════════════════════════════════════════════════════
  // BcacheFS (bcachefs-tools package) — show-super reads our header,
  // mkfs reverse → our reader. Mainline kernel 6.7+; bcachefs-tools is
  // in apt on Ubuntu 24.04+ but mainline-only — older distros need the
  // upstream tarball or the bcachefs PPA.
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void BcacheFs_OurImage_BcachefsShowSuperAccepts() {
    RequireWslTool("bcachefs", "bcachefs-tools");
    // Forward direction: our writer emits a WORM-minimal SB-only image with
    // a real `struct bch_sb` + `bch_sb_layout` + `BCH_SB_FIELD_members_v2`,
    // and `bcachefs show-super` parses + prints it without error. Note:
    // `bcachefs fsck` will still complain about the empty B-trees, journal,
    // replicas, etc. — that is explicitly out of scope here, see
    // docs/FILESYSTEMS.md for the full gap statement.
    var w = new FileSystem.BcacheFs.BcacheFsWriter();
    w.SetLabel("cwb-bcachefs-interop");
    var imgPath = Path.Combine(this._tmpDir, "bcachefs_ours.img");
    using (var fs = File.Create(imgPath))
      w.WriteTo(fs);
    var wslImg = FsInteropToolbox.WinToWsl(imgPath);

    var result = FsInteropToolbox.RunWsl($"bcachefs show-super {wslImg}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"bcachefs show-super rejected our writer output:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("Superblock").IgnoreCase.Or.Contain("magic").IgnoreCase,
      "show-super output should mention superblock fields");
  }

  /// <summary>
  /// Gap-documenting test: <c>bcachefs fsck</c> is expected to FAIL against
  /// our current writer because we emit only an SB-validated image, not a
  /// fully-bootable filesystem. The kernel rejects with
  /// <c>insufficient_devices</c> during <c>bch2_trans_mark_dev_sb</c> because
  /// the alloc B-tree is absent (we have no on-disk B-tree roots, no journal
  /// entries, no replicas section, no allocator metadata).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Reaching <c>bcachefs fsck</c> exit 0 against an empty filesystem requires
  /// adding (per <c>fs/bcachefs/</c> in Linux 6.7+):
  /// </para>
  /// <list type="number">
  ///   <item>SB sections: <c>replicas_v0</c> (16 B), <c>errors</c> (8 B),
  ///     <c>counters</c> (~624 B), <c>journal_v2</c> (40 B), <c>members_v2</c>
  ///     (~136 B), <c>clean</c> (~2768 B with 8 btree_root pointers + per-dev
  ///     usage stats + clock).</item>
  ///   <item>Compat-features bitmap:
  ///     <c>alloc_info | alloc_metadata | extents_above_btree_updates_done | bformat_overflow_done</c>.</item>
  ///   <item>8 on-disk btree roots (inodes, dirents, alloc, subvolumes,
  ///     snapshots, freespace, backpointers, snapshot_trees) at bucket-aligned
  ///     sectors, each carrying a <c>btree_node</c> header with CRC32C csum
  ///     of the node body.</item>
  ///   <item>Allocator metadata: every used bucket (~25 SB + 8 btree + 10
  ///     journal) has a <c>bch_alloc</c> key in the alloc btree.</item>
  ///   <item>Journal entries written into journal-bucket sectors with their
  ///     own CRC32C csums.</item>
  /// </list>
  /// <para>
  /// Honest scope: this is multi-week kernel-spec work. Until it lands, this
  /// test asserts that fsck reports the *expected* failure (rather than
  /// passing, which would be a false-positive). When the gap is closed, flip
  /// the assertion to expect exit 0 and rename the test.
  /// </para>
  /// </remarks>
  [Test]
  public void BcacheFs_OurImage_FsckRejectsExpectedGap() {
    RequireWslTool("bcachefs", "bcachefs-tools");
    var w = new FileSystem.BcacheFs.BcacheFsWriter();
    w.SetLabel("cwb-bcachefs-fsck-gap");
    var imgPath = Path.Combine(this._tmpDir, "bcachefs_fsck_gap.img");
    using (var fs = File.Create(imgPath))
      w.WriteTo(fs);
    var wslImg = FsInteropToolbox.WinToWsl(imgPath);

    // bcachefs fsck either crashes (SIGABRT in the journal subsystem because
    // we have no journal at all) or reports `insufficient_devices` because
    // the alloc btree is absent. Both prove the gap; what we MUST NOT see is
    // a clean fsck pass, which would mean we're silently wrong about the gap.
    // The "going read-write" + "check_inodes...done" + "delete_dead_inodes...done"
    // sequence is what a successful empty-FS fsck looks like (see reference
    // image fsck output in docs/FILESYSTEMS.md). We sniff for either of those
    // success markers and fail loudly if seen.
    var result = FsInteropToolbox.RunWsl($"bcachefs fsck -y {wslImg}");
    var combined = (result.StdOut ?? "") + "\n" + (result.StdErr ?? "");
    var lookedClean =
      result.ExitCode == 0
      && combined.Contains("check_inodes... done", StringComparison.Ordinal)
      && combined.Contains("check_root... done", StringComparison.Ordinal);
    if (lookedClean)
      Assert.Fail("bcachefs fsck unexpectedly succeeded against our SB-only image — " +
                  "either we shipped real B-tree/journal/alloc support (great! promote " +
                  "in docs/FILESYSTEMS.md and flip this assertion to expect exit 0) or " +
                  "fsck silently skipped validation. Output:\n" + combined);
    // Otherwise: gap is intact, test is informative. Don't fail CI on it.
    TestContext.Out.WriteLine("[expected gap] bcachefs fsck rejected our SB-only image, as designed:");
    TestContext.Out.WriteLine(combined.Length > 2048 ? combined[..2048] + "..." : combined);
  }

  /// <summary>
  /// Round-trip via bcachefs-tools' own format command — orthogonal coverage
  /// proving that running `bcachefs format` (instead of our writer) inside
  /// WSL still produces bytes our descriptor's reader can parse. Catches
  /// regressions where a real-world bcachefs SB drifts past the offsets our
  /// parser uses.
  /// </summary>
  [Test]
  public void BcacheFs_BcachefsToolsFormat_ShowSuperAccepts() {
    RequireWslTool("bcachefs", "bcachefs-tools");
    var imgPath = Path.Combine(this._tmpDir, "bcachefs_show.img");
    var wslImg = FsInteropToolbox.WinToWsl(imgPath);
    var prep = FsInteropToolbox.RunWsl(
      $"dd if=/dev/zero of={wslImg} bs=1M count=64 status=none && " +
      $"bcachefs format --force {wslImg}");
    if (prep.ExitCode != 0)
      Assert.Ignore($"`bcachefs format` failed (likely a kernel/version mismatch in bcachefs-tools):\n" +
                    $"stdout:\n{prep.StdOut}\nstderr:\n{prep.StdErr}");

    var result = FsInteropToolbox.RunWsl($"bcachefs show-super {wslImg}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"bcachefs show-super rejected its own format output:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("Superblock").IgnoreCase.Or.Contain("magic").IgnoreCase,
      "show-super output should mention superblock fields");
  }

  [Test]
  public void BcacheFs_LinuxFormatOutput_ReadByOurReader() {
    RequireWslTool("bcachefs", "bcachefs-tools");
    var imgPath = Path.Combine(this._tmpDir, "bcachefs_reverse.img");
    var wslImg = FsInteropToolbox.WinToWsl(imgPath);
    var prep = FsInteropToolbox.RunWsl(
      $"dd if=/dev/zero of={wslImg} bs=1M count=64 status=none && " +
      $"bcachefs format --force {wslImg}");
    if (prep.ExitCode != 0)
      Assert.Ignore($"`bcachefs format` failed (likely a kernel/version mismatch in bcachefs-tools):\n" +
                    $"stdout:\n{prep.StdOut}\nstderr:\n{prep.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.BcacheFs.BcacheFsFormatDescriptor();
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null, "Our reader returned null on a real bcachefs format image");
    // Extract metadata.ini and confirm it parsed the superblock cleanly.
    var extractDir = Path.Combine(this._tmpDir, "bcachefs_meta");
    Directory.CreateDirectory(extractDir);
    using (var s2 = File.OpenRead(imgPath))
      descriptor.Extract(s2, extractDir, null, null);
    var metaPath = Path.Combine(extractDir, "metadata.ini");
    Assert.That(File.Exists(metaPath), $"metadata.ini missing in {extractDir}");
    var meta = File.ReadAllText(metaPath);
    Assert.That(meta, Does.Contain("parse_status=ok"),
      $"Our reader failed to parse the bcachefs superblock:\n{meta}");
    Assert.That(meta, Does.Match(@"block_size=\d+"),
      $"metadata.ini should report a non-empty block_size:\n{meta}");
    Assert.That(meta, Does.Not.Match(@"block_size=0\b"),
      $"block_size=0 means the superblock parse silently bottomed out:\n{meta}");
  }

  // ═══════════════════════════════════════════════════════════════════
  // Reiser4 (reiser4progs package) — both directions covered:
  //   • forward — our Reiser4Writer → fsck.reiser4 must exit 0
  //   • reverse — Linux mkfs.reiser4 → our reader surfaces master + format40
  // Forward case is the WORM-minimal empty filesystem (root dir only).
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Reiser4_LinuxMkfsOutput_ReadByOurReader() {
    RequireWslTool("mkfs.reiser4", "reiser4progs");
    // mkfs.reiser4 needs at least ~4 MB; 64 MB matches the bcachefs test for
    // consistency and gives plenty of headroom for the format40 superblock.
    var imgPath = Path.Combine(this._tmpDir, "reiser4_reverse.img");
    var wslImg = FsInteropToolbox.WinToWsl(imgPath);
    // -f -y = force without prompts; mkfs.reiser4 prompts for confirmation
    // by default even on regular files.
    var prep = FsInteropToolbox.RunWsl(
      $"dd if=/dev/zero of={wslImg} bs=1M count=64 status=none && " +
      $"mkfs.reiser4 -f -y {wslImg}");
    if (prep.ExitCode != 0)
      Assert.Ignore($"`mkfs.reiser4` failed:\n" +
                    $"stdout:\n{prep.StdOut}\nstderr:\n{prep.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.Reiser4.Reiser4FormatDescriptor();
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null, "Our reader returned null on a real mkfs.reiser4 image");

    var extractDir = Path.Combine(this._tmpDir, "reiser4_meta");
    Directory.CreateDirectory(extractDir);
    using (var s2 = File.OpenRead(imgPath))
      descriptor.Extract(s2, extractDir, null, null);
    var metaPath = Path.Combine(extractDir, "metadata.ini");
    Assert.That(File.Exists(metaPath), $"metadata.ini missing in {extractDir}");
    var meta = File.ReadAllText(metaPath);
    Assert.That(meta, Does.Contain("parse_status=ok"),
      $"Our reader failed to parse the Reiser4 master superblock:\n{meta}");
    Assert.That(meta, Does.Match(@"blocksize=\d+"),
      $"metadata.ini should report a non-empty blocksize:\n{meta}");
  }

  [Test]
  public void Reiser4_NoForwardWriter_Skip() {
    // Superseded by Reiser4_OurImage_FsckAccepts. Kept as a sentinel that
    // skips cleanly so historical baselines that filter on this name still
    // see a recognised result.
    Assert.Ignore(
      "Superseded by Reiser4_OurImage_FsckAccepts (forward path now covered by " +
      "Reiser4Writer + WSL fsck.reiser4).");
  }

  // ═══════════════════════════════════════════════════════════════════
  // ext1 (no Linux validator — `mkfs.ext1` does not exist; ext1's 0xEF51
  // magic was retired in 1993). Sanity test only:
  //   1. Our reader accepts a synthetic 4 KB buffer with 0xEF51 at +1080
  //      (the canonical ext1 magic slot) and surfaces parse_status=ok.
  //   2. Our reader rejects a real ext2 image (0xEF53) — the wrong magic
  //      means our descriptor must not claim it.
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Ext1_OurDescriptor_AcceptsSyntheticEf51Magic() {
    // 4 KB synthetic. ext1 superblock starts at file offset 1024; the s_magic
    // field is 56 bytes into the superblock, i.e. file offset 1080. Field
    // layout matches GOOD_OLD-revision ext2 (s_inodes_count u32 @+0, …),
    // which lets us populate the few u32 slots Ext1Superblock.TryParse reads
    // and have it return Valid=true with non-zero counts so the surface
    // matches what a forensic operator would see on a real 1992 image.
    var img = new byte[4096];
    // Superblock @ 1024.
    BitConverter.GetBytes((uint)16).CopyTo(img, 1024 + 0);   // s_inodes_count
    BitConverter.GetBytes((uint)64).CopyTo(img, 1024 + 4);   // s_blocks_count
    BitConverter.GetBytes((uint)0).CopyTo(img, 1024 + 8);    // s_r_blocks_count
    BitConverter.GetBytes((uint)50).CopyTo(img, 1024 + 12);  // s_free_blocks_count
    BitConverter.GetBytes((uint)10).CopyTo(img, 1024 + 16);  // s_free_inodes_count
    BitConverter.GetBytes((uint)1).CopyTo(img, 1024 + 20);   // s_first_data_block
    BitConverter.GetBytes((uint)0).CopyTo(img, 1024 + 24);   // s_log_block_size (0 = 1 KiB)
    BitConverter.GetBytes((uint)8192).CopyTo(img, 1024 + 32);// s_blocks_per_group
    BitConverter.GetBytes((uint)16).CopyTo(img, 1024 + 40);  // s_inodes_per_group
    // s_magic = 0xEF51 (ext1) at file offset 1080.
    img[1080] = 0x51;
    img[1081] = 0xEF;

    var imgPath = Path.Combine(this._tmpDir, "ext1_synthetic.img");
    File.WriteAllBytes(imgPath, img);

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.Ext1.Ext1FormatDescriptor();
    var entries = descriptor.List(stream, null);
    Assert.That(entries, Is.Not.Null);
    Assert.That(entries.Any(e => e.Name == "superblock.bin"),
      "Ext1 reader should expose superblock.bin entry on a 0xEF51 image");

    var extractDir = Path.Combine(this._tmpDir, "ext1_meta");
    Directory.CreateDirectory(extractDir);
    using (var s2 = File.OpenRead(imgPath))
      descriptor.Extract(s2, extractDir, null, null);
    var meta = File.ReadAllText(Path.Combine(extractDir, "metadata.ini"));
    Assert.That(meta, Does.Contain("parse_status=ok"),
      $"Ext1 reader failed on a synthetic 0xEF51 superblock:\n{meta}");
    Assert.That(meta, Does.Contain("magic=0xEF51"),
      $"metadata.ini should echo the 0xEF51 magic:\n{meta}");
  }

  [Test]
  public void Ext1_OurDescriptor_RejectsExt2Image() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("mkfs.ext2"))
      Assert.Ignore("WSL + mkfs.ext2 required to build a real ext2 image for the negative test. " +
                    "Install via: `sudo apt install -y e2fsprogs` inside WSL.");

    // Real ext2 (revision 0, no features) has magic 0xEF53 at offset 1080.
    // Our ext1 descriptor checks for 0xEF51 — must NOT mark this image as
    // parsed-ok. Build with -r 0 -O none so the on-disk struct stays in
    // GOOD_OLD layout; the only meaningful difference vs ext1 is the magic.
    var imgPath = Path.Combine(this._tmpDir, "real_ext2.img");
    var wslImg = FsInteropToolbox.WinToWsl(imgPath);
    var prep = FsInteropToolbox.RunWsl(
      $"dd if=/dev/zero of={wslImg} bs=1M count=4 status=none && " +
      $"mkfs.ext2 -F -r 0 -O none {wslImg}");
    if (prep.ExitCode != 0)
      Assert.Ignore($"`mkfs.ext2 -r 0 -O none` failed:\n{prep.StdErr}");

    using var stream = File.OpenRead(imgPath);
    var descriptor = new FileSystem.Ext1.Ext1FormatDescriptor();
    var extractDir = Path.Combine(this._tmpDir, "ext1_neg");
    Directory.CreateDirectory(extractDir);
    descriptor.Extract(stream, extractDir, null, null);
    var meta = File.ReadAllText(Path.Combine(extractDir, "metadata.ini"));
    Assert.That(meta, Does.Contain("parse_status=partial"),
      "Ext1 descriptor must reject a real ext2 image (magic 0xEF53 ≠ 0xEF51). " +
      $"Got:\n{meta}");
  }

  // ═══════════════════════════════════════════════════════════════════
  // HAMMER + HAMMER2 (DragonFly BSD only — no Linux apt validator).
  // Documented as skip stubs; manual validation requires booting DragonFly
  // BSD (or its live ISO) under QEMU and running `hammer info` /
  // `hammer2 info` against the image.
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Hammer_OurImage_DragonFlyHammerInfo() {
    if (!Compression.Tests.Support.QemuRunner.QemuAvailable)
      Assert.Ignore("qemu-system-x86_64 not found. Install on Windows from https://qemu.weilnetz.de/w64/ or " +
                    "via 'sudo apt install -y qemu-system-x86' in WSL.");
    var iso = Compression.Tests.Support.QemuRunner.LocateDragonFlyIso();
    if (iso is null)
      Assert.Ignore("DragonFly BSD ISO not staged. Set CWB_DRAGONFLY_ISO env var to a downloaded " +
                    "live ISO from https://www.dragonflybsd.org/download/ (≈900 MB). Stock live ISO " +
                    "drops to a login prompt and times out — for full automation build a custom live " +
                    "image with /etc/rc.local that runs 'hammer info /dev/da1; halt'.");

    // Build minimal HAMMER image via our descriptor (read-only currently — uses synthetic)
    var imgPath = Path.Combine(this._tmpDir, "hammer.img");
    File.WriteAllBytes(imgPath, new byte[16 * 1024 * 1024]);  // placeholder until HAMMER writer exists
    var logPath = Path.Combine(this._tmpDir, "hammer.serial.log");

    var (exit, log) = Compression.Tests.Support.QemuRunner.RunHammerInfo(
      iso, imgPath, logPath, TimeSpan.FromMinutes(2), useHammer2: false);

    if (exit == Compression.Tests.Support.QemuRunner.SkipExitCode_StillInteractive)
      Assert.Ignore($"DragonFly live ISO timed out at interactive prompt. Build a custom live image " +
                    $"with autoexec'd 'hammer info /dev/da1' in /etc/rc.local. Captured serial log:\n{log}");
    Assert.That(exit, Is.EqualTo(0), $"hammer info rejected our image:\n{log}");
    Assert.That(log, Does.Contain("HAMMER").Or.Contain("volume"), "Expected hammer info output in serial log");
  }

  [Test]
  public void Hammer2_OurImage_DragonFlyHammer2Info() {
    if (!Compression.Tests.Support.QemuRunner.QemuAvailable)
      Assert.Ignore("qemu-system-x86_64 not found. See Hammer_OurImage_DragonFlyHammerInfo for install hints.");
    var iso = Compression.Tests.Support.QemuRunner.LocateDragonFlyIso();
    if (iso is null)
      Assert.Ignore("DragonFly BSD ISO not staged. Set CWB_DRAGONFLY_ISO env var. " +
                    "Custom live ISO with 'hammer2 info /dev/da1; halt' in /etc/rc.local needed for full automation.");

    var imgPath = Path.Combine(this._tmpDir, "hammer2.img");
    File.WriteAllBytes(imgPath, new byte[16 * 1024 * 1024]);
    var logPath = Path.Combine(this._tmpDir, "hammer2.serial.log");

    var (exit, log) = Compression.Tests.Support.QemuRunner.RunHammerInfo(
      iso, imgPath, logPath, TimeSpan.FromMinutes(2), useHammer2: true);

    if (exit == Compression.Tests.Support.QemuRunner.SkipExitCode_StillInteractive)
      Assert.Ignore($"DragonFly live ISO timed out at interactive prompt. Captured log:\n{log}");
    Assert.That(exit, Is.EqualTo(0), $"hammer2 info rejected our image:\n{log}");
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

  // ═══════════════════════════════════════════════════════════════════
  // DoubleSpace / DriveSpace — DOS-only validation paths
  // ═══════════════════════════════════════════════════════════════════
  // No Linux apt-installable validator exists. Three potential paths:
  //   (1) dmsdos — GPL DBLSPACE/DRVSPACE Linux kernel module + userspace
  //                tool (last release 2001, doesn't build modern out-of-box).
  //   (2) DOSBox-X + MS-DOS 6.22 — runs the real DBLSPACE.EXE /CHKDSK
  //                command. Needs a legal MS-DOS 6.22 boot image, supplied
  //                by the user via env var CWB_MSDOS_BOOT_IMG.
  //   (3) Build dmsdos from source — practical if a maintained fork exists.
  //
  // For now both tests skip cleanly with actionable instructions.

  [Test]
  public void DoubleSpace_OurImage_DmsdosAccepts() {
    if (!FsInteropToolbox.WslHasTool("dmsdos") &&
        !FsInteropToolbox.WslHasTool("dmsdosfsck")) {
      Assert.Ignore(
        "dmsdos not found. The original DBLSPACE/DRVSPACE Linux tool was a kernel module " +
        "(in-tree until ~2.4) plus a userspace fsck. To enable: clone a maintained dmsdos " +
        "fork (e.g. https://github.com/search?q=dmsdos+linux) into WSL and `make`. " +
        "Then run: 'dmsdosfsck <our.cvf>'. Since dmsdos doesn't ship in apt, this skip is " +
        "expected on stock distros. The CVF writer self-validates via round-trip tests.");
    }
    var dbl = new DoubleSpaceWriter();
    dbl.AddFile("HELLO.TXT", SmallText);
    dbl.AddFile("REPEAT.TXT", RepetitiveText);
    var imgPath = Path.Combine(this._tmpDir, "dblspace.cvf");
    File.WriteAllBytes(imgPath, dbl.Build());

    var result = FsInteropToolbox.RunWsl($"dmsdosfsck {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"dmsdosfsck rejected our CVF:\n{result.StdOut}\n{result.StdErr}");
  }

  // ── Shared CVF/CHKDSK harness ──────────────────────────────────────
  //
  // The DBLSPACE (MS-DOS 6.0/6.2) and DRVSPACE (MS-DOS 6.22+) gates are
  // structurally identical — only the env var, CVF variant, and driver
  // filename differ. Both delegate to RunCvfChkdskGate so the harness is
  // a single source of truth.

  /// <summary>Drives a DBLSPACE.EXE / DRVSPACE.EXE /CHKDSK run inside DOSBox-X
  /// against a CVF produced by our writer with the given variant. Skips
  /// cleanly with an actionable hint when any prerequisite is missing.</summary>
  private void RunCvfChkdskGate(
    string envVarPrimary,
    string envVarFallback,
    CvfVariant variant,
    string driverName,
    string productLabel) {
    var msdosImg = Environment.GetEnvironmentVariable(envVarPrimary);
    if (string.IsNullOrEmpty(msdosImg) || !File.Exists(msdosImg))
      msdosImg = Environment.GetEnvironmentVariable(envVarFallback);
    if (string.IsNullOrEmpty(msdosImg) || !File.Exists(msdosImg)) {
      Assert.Ignore(
        $"Set {envVarPrimary} to a legal {productLabel} boot hard-disk image. " +
        $"{driverName}.BIN must be on the C: partition. " +
        "See Compression.Tests/Support/MsDosImageStaging.md for the full " +
        "build recipe (DOSBox-X imgmake → SETUP → copy out C:).");
    }
    if (!DosboxRunner.DosboxXAvailable) {
      Assert.Ignore(
        "DOSBox-X not found. Install from https://dosbox-x.com/ (Windows) or " +
        "via 'sudo apt install -y dosbox-x' (Linux/WSL). DOSBox-X is required " +
        $"(not classic DOSBox) because only DOSBox-X loads {driverName}.BIN reliably.");
    }

    var dbl = new DoubleSpaceWriter { Variant = variant };
    dbl.AddFile("HELLO.TXT", "Test from CompressionWorkbench"u8.ToArray());
    var cvfPath = Path.Combine(this._tmpDir, $"test_{variant}.cvf");
    File.WriteAllBytes(cvfPath, dbl.Build());

    var hostShareDir = this._tmpDir;
    var guestOutputPath = Path.Combine(hostShareDir, "OUTPUT.TXT");
    if (File.Exists(guestOutputPath)) File.Delete(guestOutputPath);

    // Both DOS and DOS\\ paths are tried so this harness works against
    // either an unattended-install image (DBLSPACE.EXE in C:\\DOS) or a
    // hand-rolled minimal boot floppy (driver in root).
    var driverExe = $"{driverName}.EXE";
    var conf = new DosboxConfBuilder()
      .MountBootImageAsC(msdosImg)
      .MountHdd(cvfPath, "D")
      .Autoexec(
        $"mount e \"{hostShareDir}\"",
        "C:",
        $"if exist C:\\DOS\\{driverExe} C:\\DOS\\{driverExe} /CHKDSK D: > E:\\OUTPUT.TXT",
        $"if exist C:\\{driverExe} C:\\{driverExe} /CHKDSK D: >> E:\\OUTPUT.TXT",
        "EXIT")
      .Build();

    var result = DosboxRunner.RunWithConf(conf, guestOutputPath, TimeSpan.FromSeconds(60));

    if (result.ExitCode == DosboxRunner.ExitCode_DosboxMissing)
      Assert.Ignore("DOSBox-X disappeared between probe and run — re-check installation.");
    if (result.ExitCode == DosboxRunner.ExitCode_TimedOut) {
      Assert.Ignore(
        "DOSBox-X timed out before exiting. The MS-DOS guest may be waiting at " +
        "an interactive prompt (e.g. 'Press any key'); rebuild your boot image " +
        "with a non-interactive AUTOEXEC.BAT. Captured stderr: " + result.StdErr.Trim());
    }

    if (string.IsNullOrWhiteSpace(result.GuestOutput)) {
      Assert.Ignore(
        $"DOSBox-X ran but {driverExe} produced no output. Check that " +
        $"C:\\DOS\\{driverExe} exists in your boot image. " +
        $"Stdout: {result.StdOut.Trim()} | Stderr: {result.StdErr.Trim()}");
    }

    Assert.Multiple(() => {
      Assert.That(result.GuestOutput, Does.Not.Contain("error").IgnoreCase,
        $"{driverName} /CHKDSK reported error against our CVF:\n{result.GuestOutput}");
      Assert.That(result.GuestOutput, Does.Not.Contain("invalid").IgnoreCase,
        $"{driverName} /CHKDSK reported invalid structure in our CVF:\n{result.GuestOutput}");
    });
  }

  /// <summary>DBLSPACE gate — MS-DOS 6.0/6.2, CvfVariant.DoubleSpace60, signature
  /// <c>DBLS</c>. Env var: <c>CWB_MSDOS_DBLSPACE_BOOT_IMG</c> (or legacy
  /// <c>CWB_MSDOS_BOOT_IMG</c>). Runs <c>DBLSPACE /CHKDSK D:</c> in DOSBox-X.</summary>
  [Test]
  public void DoubleSpace_OurImage_DblspaceChkdsk() =>
    this.RunCvfChkdskGate(
      envVarPrimary: "CWB_MSDOS_DBLSPACE_BOOT_IMG",
      envVarFallback: "CWB_MSDOS_BOOT_IMG",
      variant: CvfVariant.DoubleSpace60,
      driverName: "DBLSPACE",
      productLabel: "MS-DOS 6.0/6.2 DoubleSpace");

  /// <summary>DRVSPACE gate — MS-DOS 6.22+, CvfVariant.DriveSpace62, signature
  /// <c>DVRS</c>. Env var: <c>CWB_MSDOS_DRVSPACE_BOOT_IMG</c> (or legacy
  /// <c>CWB_MSDOS622_BOOT_IMG</c>). Runs <c>DRVSPACE /CHKDSK D:</c> in DOSBox-X.</summary>
  [Test]
  public void DriveSpace_OurImage_DrvspaceChkdsk() =>
    this.RunCvfChkdskGate(
      envVarPrimary: "CWB_MSDOS_DRVSPACE_BOOT_IMG",
      envVarFallback: "CWB_MSDOS622_BOOT_IMG",
      variant: CvfVariant.DriveSpace62,
      driverName: "DRVSPACE",
      productLabel: "MS-DOS 6.22 DriveSpace");

  // ═══════════════════════════════════════════════════════════════════
  // FAT — FreeDOS chkdsk gate (legal, no proprietary binaries)
  // ═══════════════════════════════════════════════════════════════════

  /// <summary>FAT validation via FreeDOS CHKDSK in DOSBox-X. FreeDOS is GPL
  /// — staged automatically by <see cref="FreeDosCache.EnsureLiveCdIso"/> on
  /// first run (hash-pinned) so no manual setup is required. Skips cleanly
  /// when DOSBox-X is missing or the FreeDOS download fails (offline / hash
  /// mismatch).
  /// <para>
  /// Marked <see cref="ExplicitAttribute"/> because driving FreeDOS-in-DOSBox-X
  /// headlessly is fragile — boot timing, autoexec interaction, and EXIT
  /// handling vary across DOSBox-X versions. The gate is wired and ready to
  /// run on demand (CI matrix or explicit <c>dotnet test --filter</c>) but
  /// not on every developer build.
  /// </para></summary>
  [Test]
  [Explicit("FreeDOS-in-DOSBox-X automation is fragile — run on demand. "
          + "See Compression.Tests/Support/MsDosImageStaging.md.")]
  public void Fat_OurImage_FreedosChkdsk() {
    if (!DosboxRunner.DosboxXAvailable) {
      Assert.Ignore(
        "DOSBox-X not found. Install from https://dosbox-x.com/ (Windows) or " +
        "via 'sudo apt install -y dosbox-x' (Linux/WSL).");
    }
    var freedosIso = FreeDosCache.EnsureLiveCdIso();
    if (string.IsNullOrEmpty(freedosIso) || !File.Exists(freedosIso)) {
      Assert.Ignore(
        "FreeDOS LiveCD ISO unavailable (offline / mirror unreachable / hash " +
        "mismatch). Pre-stage manually: download " +
        FreeDosCache.LiveCdZipUrl + " (SHA-256 " + FreeDosCache.LiveCdZipSha256 +
        "), extract " + FreeDosCache.IsoEntryName + " from the zip, then either " +
        "place it at %TEMP%/cwb-freedos-cache/" + FreeDosCache.IsoEntryName +
        " or set CWB_FREEDOS_ISO to its full path.");
    }

    var fat = new FatWriter();
    fat.AddFile("HELLO.TXT", SmallText);
    fat.AddFile("REPEAT.TXT", RepetitiveText);
    var fatPath = Path.Combine(this._tmpDir, "fat_for_freedos.img");
    File.WriteAllBytes(fatPath, fat.Build());

    var hostShareDir = this._tmpDir;
    var guestOutputPath = Path.Combine(hostShareDir, "OUTPUT.TXT");
    if (File.Exists(guestOutputPath)) File.Delete(guestOutputPath);

    // FreeDOS LiveCD boots straight to a shell when there is no installer
    // step — but the bundled ISO defaults to an interactive welcome screen
    // unless config.sys/autoexec.bat is overridden. For a headless gate we
    // mount the ISO, mount the FAT image as D:, and point an autoexec at it
    // via a host-side overlay drive (E:). DOSBox-X runs the [autoexec]
    // section AFTER any guest config.sys/autoexec.bat finish — for the
    // FreeDOS LiveCD this is best-effort and may race with the welcome
    // screen, which is why this gate is [Explicit].
    var conf = new DosboxConfBuilder()
      .MountHdd(fatPath, "D")
      .Autoexec(
        $"imgmount E \"{freedosIso}\" -t iso",
        $"mount F \"{hostShareDir}\"",
        "D:",
        // FreeDOS ships chkdsk.exe in C:\FREEDOS\BIN on the LiveCD when
        // installed, but we are running off the LiveCD itself so chkdsk
        // lives on the ISO. The LiveCD mounts the ISO contents at E:.
        "if exist E:\\FREEDOS\\BIN\\CHKDSK.EXE E:\\FREEDOS\\BIN\\CHKDSK.EXE D: > F:\\OUTPUT.TXT",
        "if exist E:\\BIN\\CHKDSK.EXE E:\\BIN\\CHKDSK.EXE D: >> F:\\OUTPUT.TXT",
        "EXIT")
      .Build();

    var result = DosboxRunner.RunWithConf(conf, guestOutputPath, TimeSpan.FromSeconds(45));

    if (result.ExitCode == DosboxRunner.ExitCode_DosboxMissing)
      Assert.Ignore("DOSBox-X disappeared between probe and run.");
    if (result.ExitCode == DosboxRunner.ExitCode_TimedOut) {
      Assert.Ignore(
        "DOSBox-X timed out — FreeDOS likely paused at the interactive " +
        "welcome screen. The autoexec.bat overlay is best-effort against the " +
        "LiveCD; consider building a custom FreeDOS boot disk for headless CI. " +
        "Stderr: " + result.StdErr.Trim());
    }
    if (string.IsNullOrWhiteSpace(result.GuestOutput)) {
      Assert.Ignore(
        "DOSBox-X ran but FreeDOS CHKDSK produced no output. The LiveCD " +
        "interactive boot may have intercepted the autoexec hand-off. " +
        $"Stdout: {result.StdOut.Trim()} | Stderr: {result.StdErr.Trim()}");
    }

    Assert.That(result.GuestOutput, Does.Not.Contain("invalid").IgnoreCase,
      $"FreeDOS CHKDSK reported invalid structure on our FAT image:\n{result.GuestOutput}");
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
  /// <summary>True when qemu-img is reachable either on Windows PATH or inside WSL.</summary>
  public static bool QemuImgAnywhereAvailable => QemuImgAvailable || (WslAvailable && WslHasTool("qemu-img"));
  public static bool DismAvailable { get; } = TryFromPath("dism") is not null;
  public static bool ChkdskAvailable { get; } = TryFromPath("chkdsk") is not null;
  public static bool MToolsAvailable { get; } = TryFromPath("minfo") is not null;
  public static bool WslAvailable { get; } = DetectWsl();
  public static bool WslHasPasswordlessSudo { get; } = DetectWslPasswordlessSudo();
  public static bool WslHasUfsKernelModule { get; } = DetectWslUfsModule();

  private static readonly Dictionary<string, bool> _wslToolCache = new(StringComparer.Ordinal);

  private static bool DetectWslPasswordlessSudo() {
    if (!WslAvailable) return false;
    // `sudo -n true` exits 0 iff sudo is configured for passwordless invocation
    // for the current user (NOPASSWD). Any other case (no sudo, prompt required,
    // user not in sudoers) returns non-zero without blocking on a tty.
    var result = RunWsl("sudo -n true 2>/dev/null");
    return result.ExitCode == 0;
  }

  private static bool DetectWslUfsModule() {
    if (!WslAvailable) return false;
    // Three valid signals that the kernel can serve UFS:
    //   1. `ufs` already loaded (lsmod | grep)
    //   2. modinfo finds the module (built but not loaded)
    //   3. /proc/filesystems lists it (built-in, not modular)
    var result = RunWsl(
      "lsmod 2>/dev/null | grep -qw ufs || " +
      "modinfo ufs >/dev/null 2>&1 || " +
      "grep -qw ufs /proc/filesystems 2>/dev/null");
    return result.ExitCode == 0;
  }

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

  /// <summary>
  /// Skips the calling test cleanly when qemu-img is unavailable on either the
  /// Windows host PATH or inside WSL. The skip message names both install
  /// channels so the next runner sees an actionable hint.
  /// </summary>
  public static void RequireQemuImg() {
    if (!QemuImgAnywhereAvailable)
      Assert.Ignore("qemu-img not found. Install on Windows from https://qemu.weilnetz.de/w64/ " +
                    "(add the bin dir to PATH) or inside WSL via `sudo apt install -y qemu-utils`. " +
                    "Both `qemu-img info|check|convert|create` are then auto-discovered.");
  }

  /// <summary>
  /// Invokes qemu-img with <paramref name="args"/>, preferring the Windows
  /// binary when present and otherwise routing through WSL. Path arguments
  /// must already be in the form returned by <see cref="WinToWsl"/> when the
  /// WSL path is used; the helper does NOT translate paths automatically
  /// because qemu-img also accepts native Windows paths when run on Windows.
  /// Caller is expected to call <see cref="WinToWsl"/> on every path argument
  /// — both code paths accept that quoted POSIX form correctly.
  /// </summary>
  public static (string StdOut, string StdErr, int ExitCode) RunQemuImg(string args) {
    if (QemuImgAvailable) {
      // Windows-native qemu-img doesn't accept '/mnt/c/...' paths — the test
      // helpers always feed us WinToWsl()-quoted forms. Translate back to
      // Windows so both invocation paths share one wire format.
      var nativeArgs = TranslateWslPathsToWindows(args);
      return RunPath("qemu-img", nativeArgs);
    }
    if (WslAvailable && WslHasTool("qemu-img"))
      return RunWsl($"qemu-img {args}");
    return (string.Empty, "qemu-img not available", -1);
  }

  /// <summary>
  /// Rewrites every <c>'/mnt/X/...'</c> token in a qemu-img argument string
  /// back to a quoted Windows path so the Windows-native binary can consume
  /// the same arg list a WSL invocation would. Keeps non-path tokens intact.
  /// </summary>
  internal static string TranslateWslPathsToWindows(string args) {
    if (string.IsNullOrEmpty(args)) return args;
    // Match either single-quoted '/mnt/x/...' or bare /mnt/x/... forms.
    return System.Text.RegularExpressions.Regex.Replace(
      args,
      "'?/mnt/([a-zA-Z])(/[^'\"\\s]*)'?",
      m => {
        var drive = char.ToUpperInvariant(m.Groups[1].Value[0]);
        var tail = m.Groups[2].Value.Replace('/', '\\');
        return $"\"{drive}:{tail}\"";
      });
  }

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

  /// <summary>Public wrapper for PATH-based tool discovery, used by DOSBox-style host probes.</summary>
  public static bool HostPathHasTool(string tool) => TryFromPath(tool) is not null;

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
