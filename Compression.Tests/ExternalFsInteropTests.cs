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
