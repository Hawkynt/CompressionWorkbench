using FileFormat.Cpio;

namespace Compression.Tests.Cpio;

/// <summary>
/// Bidirectional compatibility checks against libarchive's bsdcpio/bsdtar tools.
/// These are behavioral-oracle tests only; the implementation itself is derived
/// from the documented cpio(5) layouts rather than from libarchive source code.
/// </summary>
[TestFixture]
[Category("ArchiveExternalInterop")]
public class CpioLibarchiveInteropTests {
  private static readonly byte[] Payload = "libarchive-cpio-interoperability"u8.ToArray();
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_cpio_libarchive_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  [TestCase(CpioArchiveFormat.NewCrc, "crc")]
  [TestCase(CpioArchiveFormat.PortableAscii, "odc")]
  [TestCase(CpioArchiveFormat.BinaryLittleEndian, "bin-le")]
  [TestCase(CpioArchiveFormat.BinaryBigEndian, "bin-be")]
  [TestCase(CpioArchiveFormat.PwbBinary, "pwb")]
  public void CwbWriter_OutputExtractsWithLibarchive(CpioArchiveFormat format, string suffix) {
    RequireTool("bsdtar");

    var archivePath = Path.Combine(this._tmpDir, $"cwb-{suffix}.cpio");
    using (var target = File.Create(archivePath))
    using (var writer = new CpioWriter(target, format)) {
      writer.AddFile("payload.txt", Payload);
      writer.Finish();
    }

    var outputDir = Path.Combine(this._tmpDir, $"extract-{suffix}");
    Directory.CreateDirectory(outputDir);
    var result = FsInteropToolbox.RunWsl(
      $"bsdtar -xf {FsInteropToolbox.WinToWsl(archivePath)} -C {FsInteropToolbox.WinToWsl(outputDir)}");

    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"libarchive rejected CWB {format} output:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    var extracted = FsInteropToolbox.FindFile(outputDir, "payload.txt");
    Assert.That(extracted, Is.Not.Null, "libarchive extraction did not produce payload.txt");
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(Payload));
  }

  [Test]
  public void CwbPwbWriter_ExtractsWithBsdcpioPwbOverride() {
    RequireTool("bsdcpio");

    var archivePath = Path.Combine(this._tmpDir, "cwb-pwb-explicit.cpio");
    using (var target = File.Create(archivePath))
    using (var writer = new CpioWriter(target, CpioArchiveFormat.PwbBinary)) {
      writer.AddFile("payload.txt", Payload);
      writer.Finish();
    }

    var outputDir = Path.Combine(this._tmpDir, "extract-pwb-explicit");
    Directory.CreateDirectory(outputDir);
    var result = FsInteropToolbox.RunWsl(
      $"cd {FsInteropToolbox.WinToWsl(outputDir)} && bsdcpio -i -6 < {FsInteropToolbox.WinToWsl(archivePath)}");

    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"bsdcpio -6 rejected CWB PWB output:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    var extracted = FsInteropToolbox.FindFile(outputDir, "payload.txt");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(Payload));
  }

  [TestCase("odc", CpioArchiveFormat.PortableAscii)]
  [TestCase("bin", CpioArchiveFormat.BinaryLittleEndian)]
  [TestCase("pwb", CpioArchiveFormat.PwbBinary)]
  public void LibarchiveWriter_OutputReadsWithCwb(string libarchiveFormat, CpioArchiveFormat expectedFormat) {
    RequireTool("bsdcpio");

    var inputPath = Path.Combine(this._tmpDir, "payload.txt");
    var archivePath = Path.Combine(this._tmpDir, $"libarchive-{libarchiveFormat}.cpio");
    File.WriteAllBytes(inputPath, Payload);

    var wslDir = FsInteropToolbox.WinToWsl(this._tmpDir);
    var wslArchive = FsInteropToolbox.WinToWsl(archivePath);
    var result = FsInteropToolbox.RunWsl(
      $"cd {wslDir} && printf 'payload.txt\\n' | bsdcpio -o --format={libarchiveFormat} > {wslArchive}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"bsdcpio failed to create {libarchiveFormat}:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");

    using var source = File.OpenRead(archivePath);
    using var reader = new CpioReader(
      source,
      assumePwbBinary: expectedFormat == CpioArchiveFormat.PwbBinary);
    var entries = reader.ReadAll();

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(entries[0].Entry.Name, Is.EqualTo("payload.txt"));
      Assert.That(entries[0].Entry.Format, Is.EqualTo(expectedFormat));
      Assert.That(entries[0].Data, Is.EqualTo(Payload));
    });
  }

  private static void RequireTool(string tool) {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL/Linux shell unavailable; install WSL to run libarchive interoperability tests.");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"'{tool}' unavailable; install libarchive-tools in the WSL/Linux environment.");
  }
}
