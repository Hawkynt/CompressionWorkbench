#pragma warning disable CS1591
using FileFormat.Cpio;

namespace Compression.Tests.Cpio;

/// <summary>
/// Holds our cpio implementation against the reference tools in both
/// directions: every variant we write is handed to libarchive's
/// <c>bsdcpio</c>, and every variant those tools write is handed back to our
/// reader. Round-tripping through our own two halves cannot detect a mistake
/// they share — an external oracle can.
/// </summary>
/// <remarks>
/// libarchive is the oracle for <c>bin</c>, <c>odc</c> and <c>newc</c>; it
/// reads the SVR4 CRC variant but has no writer for it, so GNU <c>cpio -H
/// crc</c> supplies that direction. Both tools are behavioural oracles only:
/// the implementation comes from <c>cpio(5)</c> and POSIX, not from their
/// source.
/// <para>
/// Every test skips with an actionable hint when the tool is absent, so the
/// fixture is inert on a machine (or CI leg) without them.
/// </para>
/// </remarks>
[TestFixture]
[Category("ArchiveExternalInterop")]
public class CpioLibarchiveInteropTests {

  private static readonly byte[] Payload = "libarchive cpio interoperability payload\n"u8.ToArray();

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

  // ── Our output → the reference tool ────────────────────────────────

  [TestCase(CpioArchiveFormat.NewAscii, "newc")]
  [TestCase(CpioArchiveFormat.NewCrc, "crc")]
  [TestCase(CpioArchiveFormat.PortableAscii, "odc")]
  [TestCase(CpioArchiveFormat.BinaryLittleEndian, "bin-le")]
  [TestCase(CpioArchiveFormat.BinaryBigEndian, "bin-be")]
  public void OurOutput_ExtractsWithBsdcpio(CpioArchiveFormat format, string label) {
    Require("bsdcpio");

    var archivePath = Path.Combine(this._tmpDir, $"ours-{label}.cpio");
    using (var target = File.Create(archivePath))
    using (var writer = new CpioWriter(target, format, leaveOpen: true)) {
      writer.AddFile("payload.txt", Payload);
      writer.AddFile("docs/second.txt", "second entry\n"u8);
      writer.Finish();
    }

    var outputDir = Path.Combine(this._tmpDir, $"extract-{label}");
    Directory.CreateDirectory(outputDir);
    var result = FsInteropToolbox.RunWsl(
      $"cd {FsInteropToolbox.WinToWsl(outputDir)} && bsdcpio -idm --quiet < {FsInteropToolbox.WinToWsl(archivePath)}");

    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"libarchive rejected our {label} output:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");

    var extracted = FsInteropToolbox.FindFile(outputDir, "payload.txt");
    Assert.That(extracted, Is.Not.Null, $"bsdcpio did not extract payload.txt from our {label} archive");
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(Payload),
      $"payload bytes must survive the trip through bsdcpio's {label} reader");

    var second = FsInteropToolbox.FindFile(outputDir, "second.txt");
    Assert.That(second, Is.Not.Null, "bsdcpio did not extract the second entry — an alignment slip after the first");
    Assert.That(File.ReadAllBytes(second!), Is.EqualTo("second entry\n"u8.ToArray()));
  }

  /// <summary>
  /// GNU cpio is the second opinion on the two variants it and libarchive
  /// disagree about the least: it verifies the CRC variant's checksum, which
  /// libarchive reads but ignores.
  /// </summary>
  [TestCase(CpioArchiveFormat.NewCrc, "crc")]
  [TestCase(CpioArchiveFormat.PortableAscii, "odc")]
  [TestCase(CpioArchiveFormat.BinaryLittleEndian, "bin")]
  public void OurOutput_PassesGnuCpioWithoutAWarning(CpioArchiveFormat format, string label) {
    Require("cpio");

    var archivePath = Path.Combine(this._tmpDir, $"gnu-check-{label}.cpio");
    using (var target = File.Create(archivePath))
    using (var writer = new CpioWriter(target, format, leaveOpen: true)) {
      writer.AddFile("payload.txt", Payload);
      writer.Finish();
    }

    var result = FsInteropToolbox.RunWsl($"cpio -it < {FsInteropToolbox.WinToWsl(archivePath)}");

    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"GNU cpio rejected our {label} output:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("payload.txt"));
    Assert.That(result.StdErr, Does.Not.Contain("checksum"),
      "GNU cpio verifies the CRC variant's payload sum; a warning here means we computed it wrong");
  }

  // ── The reference tool's output → our reader ───────────────────────

  [TestCase("newc", CpioArchiveFormat.NewAscii)]
  [TestCase("odc", CpioArchiveFormat.PortableAscii)]
  [TestCase("bin", CpioArchiveFormat.BinaryLittleEndian)]
  public void BsdcpioOutput_ReadsBackWithOurReader(string toolFormat, CpioArchiveFormat expected) {
    Require("bsdcpio");
    var archivePath = this.BuildWithTool("bsdcpio", toolFormat);

    AssertOurReaderAgrees(archivePath, expected);
  }

  /// <summary>
  /// libarchive has no writer for the SVR4 CRC variant, so GNU cpio produces
  /// it. GNU also writes its hexadecimal fields in upper case where libarchive
  /// uses lower — a reader that only handled one of the two would pass every
  /// self-round-trip and still fail here.
  /// </summary>
  [TestCase("crc", CpioArchiveFormat.NewCrc)]
  [TestCase("odc", CpioArchiveFormat.PortableAscii)]
  [TestCase("bin", CpioArchiveFormat.BinaryLittleEndian)]
  public void GnuCpioOutput_ReadsBackWithOurReader(string toolFormat, CpioArchiveFormat expected) {
    Require("cpio");
    var archivePath = this.BuildWithTool("cpio", toolFormat);

    AssertOurReaderAgrees(archivePath, expected);
  }

  // ── Helpers ────────────────────────────────────────────────────────

  /// <summary>
  /// Has the external tool pack two known files, one of them in a subdirectory
  /// so a pathname longer than the fixed header's own length is exercised.
  /// </summary>
  private string BuildWithTool(string tool, string toolFormat) {
    var stage = Path.Combine(this._tmpDir, $"stage-{tool}-{toolFormat}");
    Directory.CreateDirectory(Path.Combine(stage, "docs"));
    File.WriteAllBytes(Path.Combine(stage, "payload.txt"), Payload);
    File.WriteAllBytes(Path.Combine(stage, "docs", "second.txt"), "second entry\n"u8.ToArray());

    var archivePath = Path.Combine(this._tmpDir, $"{tool}-{toolFormat}.cpio");
    var formatFlag = tool == "cpio" ? $"-H {toolFormat}" : $"--format={toolFormat}";
    var result = FsInteropToolbox.RunWsl(
      $"cd {FsInteropToolbox.WinToWsl(stage)} && printf 'payload.txt\\ndocs/second.txt\\n' | "
      + $"{tool} -o {formatFlag} --quiet > {FsInteropToolbox.WinToWsl(archivePath)}");

    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"{tool} could not write a {toolFormat} archive:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    return archivePath;
  }

  private static void AssertOurReaderAgrees(string archivePath, CpioArchiveFormat expected) {
    using var source = File.OpenRead(archivePath);
    using var reader = new CpioReader(source, leaveOpen: true);
    var entries = reader.ReadAll();

    Assert.That(entries.Select(x => x.Entry.Name),
      Is.EqualTo(new[] { "payload.txt", "docs/second.txt" }));
    Assert.Multiple(() => {
      Assert.That(entries.All(x => x.Entry.Format == expected), Is.True,
        $"our reader identified {string.Join(", ", entries.Select(x => x.Entry.Format))} instead of {expected}");
      Assert.That(entries[0].Data, Is.EqualTo(Payload));
      Assert.That(entries[1].Data, Is.EqualTo("second entry\n"u8.ToArray()));
      Assert.That(entries[0].Entry.IsRegularFile, Is.True);
      Assert.That(entries[0].Entry.ModificationTime, Is.GreaterThan(0u),
        "the tool stamps a real mtime; reading zero means the field was parsed from the wrong offset");
    });
  }

  private static void Require(string tool) {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("No WSL / POSIX shell available. Run `wsl --install` in an elevated PowerShell, reboot, then install the package below inside the distro.");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"'{tool}' is not installed. Run inside the Linux environment: `sudo apt install -y {(tool == "cpio" ? "cpio" : "libarchive-tools")}`.");
  }
}
