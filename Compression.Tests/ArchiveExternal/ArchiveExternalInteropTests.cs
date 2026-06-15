using System.Text;
using FileFormat.Ar;
using FileFormat.Cab;
using FileFormat.Cpio;
using FileFormat.Lzh;
using FileFormat.Tar;
using FileFormat.Zip;
using FileSystem.Iso;

namespace Compression.Tests;

/// <summary>
/// Cross-validation of our ARCHIVE writers against real reference tools that
/// live inside WSL (libarchive's <c>bsdtar</c>, <c>cabextract</c>, lhasa's
/// <c>lha</c>, The Unarchiver's <c>lsar</c>). The discipline is "our output →
/// reference tool reads it": we build a tiny archive in-process with a couple
/// of named files carrying known bytes, hand it to the external tool, and
/// assert the tool lists the entries (and, where the tool extracts cleanly,
/// that the bytes round-trip).
/// <para>
/// Every test is gated on <see cref="FsInteropToolbox.WslHasTool"/> and skips
/// via <see cref="Assert.Ignore(string)"/> with an actionable apt hint when the
/// tool is missing. All I/O lives under <see cref="Path.GetTempPath"/>.
/// </para>
/// </summary>
[TestFixture]
[Category("ArchiveExternalInterop")]
public class ArchiveExternalInteropTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_archext_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Test data ──────────────────────────────────────────────────────

  private static readonly byte[] HelloBytes = "Hello from CompressionWorkbench archive interop!"u8.ToArray();

  private static byte[] RepeatBytes {
    get {
      var sb = new StringBuilder();
      for (var i = 0; i < 40; i++)
        sb.AppendLine($"Line {i}: The quick brown fox jumps over the lazy dog.");
      return Encoding.UTF8.GetBytes(sb.ToString());
    }
  }

  private static void RequireWslTool(string tool, string aptPackage) {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. Run `wsl --install` in Admin PowerShell, reboot, then install the listed package inside the distro.");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"WSL is present but '{tool}' is not installed in the distro. Run inside WSL: `sudo apt install -y {aptPackage}`.");
  }

  // ═══════════════════════════════════════════════════════════════════
  // 1. TAR → bsdtar -tvf (libarchive)
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Tar_OurOutput_ListedByBsdtar() {
    RequireWslTool("bsdtar", "libarchive-tools");
    var imgPath = Path.Combine(this._tmpDir, "ours.tar");
    using (var fs = File.Create(imgPath))
    using (var tar = new TarWriter(fs)) {
      tar.AddEntry(new TarEntry { Name = "hello.txt" }, HelloBytes);
      tar.AddEntry(new TarEntry { Name = "docs/repeat.txt" }, RepeatBytes);
    }

    var r = FsInteropToolbox.RunWsl($"bsdtar -tvf {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"bsdtar -tvf rejected our TAR:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("hello.txt"), "TAR listing should mention hello.txt");
    Assert.That(r.StdOut, Does.Contain("docs/repeat.txt"), "TAR listing should mention docs/repeat.txt");
    // -tvf prints sizes; our hello.txt size must appear in the verbose line.
    Assert.That(r.StdOut, Does.Contain(HelloBytes.Length.ToString()),
      "verbose listing should report the correct hello.txt byte size");
  }

  [Test]
  public void Tar_OurOutput_ExtractedByBsdtar_RoundTrip() {
    RequireWslTool("bsdtar", "libarchive-tools");
    var imgPath = Path.Combine(this._tmpDir, "rt.tar");
    using (var fs = File.Create(imgPath))
    using (var tar = new TarWriter(fs)) {
      tar.AddEntry(new TarEntry { Name = "hello.txt" }, HelloBytes);
    }

    var outDir = Path.Combine(this._tmpDir, "tar_x");
    Directory.CreateDirectory(outDir);
    var r = FsInteropToolbox.RunWsl(
      $"bsdtar -xf {FsInteropToolbox.WinToWsl(imgPath)} -C {FsInteropToolbox.WinToWsl(outDir)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"bsdtar -xf failed:\n{r.StdErr}");

    var extracted = FsInteropToolbox.FindFile(outDir, "hello.txt");
    Assert.That(extracted, Is.Not.Null, "expected hello.txt in bsdtar extract output");
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(HelloBytes),
      "hello.txt bytes must round-trip through bsdtar");
  }

  // ═══════════════════════════════════════════════════════════════════
  // 2. CPIO → bsdtar -tvf (libarchive reads SVR4 "newc" cpio)
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Cpio_OurOutput_ListedByBsdtar() {
    RequireWslTool("bsdtar", "libarchive-tools");
    var imgPath = Path.Combine(this._tmpDir, "ours.cpio");
    using (var fs = File.Create(imgPath))
    using (var cpio = new CpioWriter(fs)) {
      cpio.AddFile("hello.txt", HelloBytes);
      cpio.AddFile("repeat.txt", RepeatBytes);
      cpio.Finish();
    }

    var r = FsInteropToolbox.RunWsl($"bsdtar -tvf {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"bsdtar -tvf rejected our cpio:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("hello.txt"), "cpio listing should mention hello.txt");
    Assert.That(r.StdOut, Does.Contain("repeat.txt"), "cpio listing should mention repeat.txt");
  }

  [Test]
  public void Cpio_OurOutput_ExtractedByBsdtar_RoundTrip() {
    RequireWslTool("bsdtar", "libarchive-tools");
    var imgPath = Path.Combine(this._tmpDir, "rt.cpio");
    using (var fs = File.Create(imgPath))
    using (var cpio = new CpioWriter(fs)) {
      cpio.AddFile("hello.txt", HelloBytes);
      cpio.Finish();
    }

    var outDir = Path.Combine(this._tmpDir, "cpio_x");
    Directory.CreateDirectory(outDir);
    var r = FsInteropToolbox.RunWsl(
      $"bsdtar -xf {FsInteropToolbox.WinToWsl(imgPath)} -C {FsInteropToolbox.WinToWsl(outDir)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"bsdtar -xf (cpio) failed:\n{r.StdErr}");

    var extracted = FsInteropToolbox.FindFile(outDir, "hello.txt");
    Assert.That(extracted, Is.Not.Null, "expected hello.txt in cpio extract output");
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(HelloBytes),
      "hello.txt bytes must round-trip through bsdtar (cpio)");
  }

  // ═══════════════════════════════════════════════════════════════════
  // 3. AR → bsdtar -tf (libarchive reads the Unix ar format)
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Ar_OurOutput_ListedByBsdtar() {
    RequireWslTool("bsdtar", "libarchive-tools");
    var imgPath = Path.Combine(this._tmpDir, "ours.a");
    using (var fs = File.Create(imgPath))
    using (var ar = new ArWriter(fs)) {
      ar.Write([
        new ArEntry { Name = "hello.txt", Data = HelloBytes },
        new ArEntry { Name = "repeat.txt", Data = RepeatBytes },
      ]);
    }

    var r = FsInteropToolbox.RunWsl($"bsdtar -tf {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"bsdtar -tf rejected our ar archive:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("hello.txt"), "ar listing should mention hello.txt");
    Assert.That(r.StdOut, Does.Contain("repeat.txt"), "ar listing should mention repeat.txt");
  }

  // ═══════════════════════════════════════════════════════════════════
  // 4. ISO9660 → bsdtar -tf (libarchive reads ISO9660)
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Iso_OurOutput_ListedByBsdtar() {
    RequireWslTool("bsdtar", "libarchive-tools");
    var iso = new IsoWriter();
    iso.AddFile("HELLO.TXT", HelloBytes);
    iso.AddFile("REPEAT.TXT", RepeatBytes);
    var imgPath = Path.Combine(this._tmpDir, "image.iso");
    File.WriteAllBytes(imgPath, iso.Build());

    var r = FsInteropToolbox.RunWsl($"bsdtar -tf {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"bsdtar -tf rejected our ISO:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
    // ISO9660 level-1 upper-cases names; libarchive may show "HELLO.TXT" or
    // "HELLO.TXT;1" (version suffix). Match case-insensitively on the stem.
    Assert.That(r.StdOut, Does.Contain("HELLO.TXT").IgnoreCase, "ISO listing should mention HELLO.TXT");
    Assert.That(r.StdOut, Does.Contain("REPEAT.TXT").IgnoreCase, "ISO listing should mention REPEAT.TXT");
  }

  [Test]
  public void Iso_OurOutput_ExtractedByBsdtar_RoundTrip() {
    RequireWslTool("bsdtar", "libarchive-tools");
    var iso = new IsoWriter();
    iso.AddFile("HELLO.TXT", HelloBytes);
    var imgPath = Path.Combine(this._tmpDir, "rt.iso");
    File.WriteAllBytes(imgPath, iso.Build());

    var outDir = Path.Combine(this._tmpDir, "iso_x");
    Directory.CreateDirectory(outDir);
    var r = FsInteropToolbox.RunWsl(
      $"bsdtar -xf {FsInteropToolbox.WinToWsl(imgPath)} -C {FsInteropToolbox.WinToWsl(outDir)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"bsdtar -xf (iso) failed:\n{r.StdErr}");

    var extracted = FsInteropToolbox.FindFile(outDir, "HELLO.TXT")
                    ?? FsInteropToolbox.FindFile(outDir, "hello.txt");
    Assert.That(extracted, Is.Not.Null, "expected HELLO.TXT in ISO extract output");
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(HelloBytes),
      "HELLO.TXT bytes must round-trip through bsdtar (iso)");
  }

  // ═══════════════════════════════════════════════════════════════════
  // 5. ZIP → bsdtar -tf + lsar (two independent reference readers)
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Zip_OurOutput_ListedByBsdtar() {
    RequireWslTool("bsdtar", "libarchive-tools");
    var imgPath = Path.Combine(this._tmpDir, "ours.zip");
    using (var fs = File.Create(imgPath))
    using (var zip = new ZipWriter(fs)) {
      zip.AddEntry("hello.txt", HelloBytes);
      zip.AddEntry("docs/repeat.txt", RepeatBytes);
      zip.Finish();
    }

    var r = FsInteropToolbox.RunWsl($"bsdtar -tf {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"bsdtar -tf rejected our ZIP:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("hello.txt"), "ZIP listing should mention hello.txt");
    Assert.That(r.StdOut, Does.Contain("docs/repeat.txt"), "ZIP listing should mention docs/repeat.txt");
  }

  [Test]
  public void Zip_OurOutput_ExtractedByBsdtar_RoundTrip() {
    RequireWslTool("bsdtar", "libarchive-tools");
    var imgPath = Path.Combine(this._tmpDir, "rt.zip");
    using (var fs = File.Create(imgPath))
    using (var zip = new ZipWriter(fs)) {
      zip.AddEntry("hello.txt", HelloBytes);
      zip.AddEntry("repeat.txt", RepeatBytes);
      zip.Finish();
    }

    var outDir = Path.Combine(this._tmpDir, "zip_x");
    Directory.CreateDirectory(outDir);
    var r = FsInteropToolbox.RunWsl(
      $"bsdtar -xf {FsInteropToolbox.WinToWsl(imgPath)} -C {FsInteropToolbox.WinToWsl(outDir)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"bsdtar -xf (zip) failed:\n{r.StdErr}");

    var hello = FsInteropToolbox.FindFile(outDir, "hello.txt");
    var repeat = FsInteropToolbox.FindFile(outDir, "repeat.txt");
    Assert.That(hello, Is.Not.Null, "expected hello.txt in zip extract output");
    Assert.That(repeat, Is.Not.Null, "expected repeat.txt in zip extract output");
    Assert.That(File.ReadAllBytes(hello!), Is.EqualTo(HelloBytes),
      "hello.txt must round-trip (deflate) through bsdtar");
    Assert.That(File.ReadAllBytes(repeat!), Is.EqualTo(RepeatBytes),
      "repeat.txt must round-trip (deflate) through bsdtar");
  }

  [Test]
  public void Zip_OurOutput_ListedByLsar() {
    RequireWslTool("lsar", "unar");
    var imgPath = Path.Combine(this._tmpDir, "lsar.zip");
    using (var fs = File.Create(imgPath))
    using (var zip = new ZipWriter(fs)) {
      zip.AddEntry("hello.txt", HelloBytes);
      zip.Finish();
    }

    var r = FsInteropToolbox.RunWsl($"lsar {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"lsar rejected our ZIP:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("hello.txt"), "lsar ZIP listing should mention hello.txt");
  }

  // ═══════════════════════════════════════════════════════════════════
  // 6. CAB → cabextract -l (lists) + cabextract (extracts, round-trip)
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Cab_OurOutput_ListedByCabextract() {
    RequireWslTool("cabextract", "cabextract");
    // Store (uncompressed) folder keeps the test independent of MSZIP fidelity.
    var cab = new CabWriter(CabCompressionType.None);
    cab.AddFile("hello.txt", HelloBytes);
    cab.AddFile("repeat.txt", RepeatBytes);
    var imgPath = Path.Combine(this._tmpDir, "ours.cab");
    using (var fs = File.Create(imgPath)) cab.WriteTo(fs);

    var r = FsInteropToolbox.RunWsl($"cabextract -l {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"cabextract -l rejected our CAB:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("hello.txt"), "CAB listing should mention hello.txt");
    Assert.That(r.StdOut, Does.Contain("repeat.txt"), "CAB listing should mention repeat.txt");
  }

  [Test]
  public void Cab_OurOutput_ExtractedByCabextract_RoundTrip() {
    RequireWslTool("cabextract", "cabextract");
    var cab = new CabWriter(CabCompressionType.None);
    cab.AddFile("hello.txt", HelloBytes);
    var imgPath = Path.Combine(this._tmpDir, "rt.cab");
    using (var fs = File.Create(imgPath)) cab.WriteTo(fs);

    var outDir = Path.Combine(this._tmpDir, "cab_x");
    Directory.CreateDirectory(outDir);
    var r = FsInteropToolbox.RunWsl(
      $"cabextract -d {FsInteropToolbox.WinToWsl(outDir)} {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"cabextract failed:\n{r.StdErr}");

    var extracted = FsInteropToolbox.FindFile(outDir, "hello.txt");
    Assert.That(extracted, Is.Not.Null, "expected hello.txt in cabextract output");
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(HelloBytes),
      "hello.txt bytes must round-trip through cabextract (store)");
  }

  // ═══════════════════════════════════════════════════════════════════
  // 7. LHA/LZH → lha l (lists) + lha x (extracts, round-trip)
  //    Use -lh0- (stored) so the test exercises the container format and
  //    header layout rather than lhasa's lh5 decode fidelity.
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Lha_OurOutput_ListedByLha() {
    RequireWslTool("lha", "lhasa");
    var lha = new LhaWriter(LhaConstants.MethodLh0);
    lha.AddFile("hello.txt", HelloBytes);
    lha.AddFile("repeat.txt", RepeatBytes);
    var imgPath = Path.Combine(this._tmpDir, "ours.lzh");
    File.WriteAllBytes(imgPath, lha.ToArray());

    // lhasa's `lha l` lists entries; run from inside the temp dir so relative
    // paths don't matter.
    var r = FsInteropToolbox.RunWsl($"lha l {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"lha l rejected our LZH:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("hello.txt"), "LZH listing should mention hello.txt");
    Assert.That(r.StdOut, Does.Contain("repeat.txt"), "LZH listing should mention repeat.txt");
  }

  [Test]
  public void Lha_OurOutput_ExtractedByLha_RoundTrip() {
    RequireWslTool("lha", "lhasa");
    var lha = new LhaWriter(LhaConstants.MethodLh0);
    lha.AddFile("hello.txt", HelloBytes);
    var imgPath = Path.Combine(this._tmpDir, "rt.lzh");
    File.WriteAllBytes(imgPath, lha.ToArray());

    var outDir = Path.Combine(this._tmpDir, "lha_x");
    Directory.CreateDirectory(outDir);
    // lhasa's `lha x` writes into the working directory; -w sets the dest dir.
    var r = FsInteropToolbox.RunWsl(
      $"cd {FsInteropToolbox.WinToWsl(outDir)} && lha xw={FsInteropToolbox.WinToWsl(outDir)} {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"lha x failed:\n{r.StdOut}\n{r.StdErr}");

    var extracted = FsInteropToolbox.FindFile(outDir, "hello.txt");
    Assert.That(extracted, Is.Not.Null, "expected hello.txt in lha extract output");
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(HelloBytes),
      "hello.txt bytes must round-trip through lha (lh0 store)");
  }
}
