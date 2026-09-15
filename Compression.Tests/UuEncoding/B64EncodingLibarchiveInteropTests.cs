using FileFormat.Cpio;
using FileFormat.UuEncoding;

namespace Compression.Tests.UuEncoding;

/// <summary>
/// Behavioral-oracle coverage for libarchive's b64encode filter. The wire
/// format is implemented independently from the documented begin-base64
/// envelope and published vectors; these tests only verify interoperability.
/// </summary>
[TestFixture]
[Category("ArchiveExternalInterop")]
public class B64EncodingLibarchiveInteropTests {
  private static readonly byte[] Payload = "libarchive-b64encode-interoperability"u8.ToArray();
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_b64_libarchive_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  [Test]
  public void CwbBase64WrappedCpio_IsReadByBsdtar() {
    RequireTool("bsdtar");

    using var cpio = new MemoryStream();
    using (var writer = new CpioWriter(cpio, CpioArchiveFormat.PortableAscii, leaveOpen: true)) {
      writer.AddFile("payload.txt", Payload);
      writer.Finish();
    }
    cpio.Position = 0;

    var wrappedPath = Path.Combine(this._tmpDir, "cwb.cpio.b64");
    using (var output = File.Create(wrappedPath))
      UuEncoder.EncodeBase64(cpio, output, "payload.cpio");

    var result = FsInteropToolbox.RunWsl($"bsdtar -tf {FsInteropToolbox.WinToWsl(wrappedPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"bsdtar rejected CWB b64encode output:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("payload.txt"));
  }

  [Test]
  public void LibarchiveBase64WrappedCpio_IsDecodedByCwb() {
    RequireTool("bsdcpio");

    var inputPath = Path.Combine(this._tmpDir, "payload.txt");
    var wrappedPath = Path.Combine(this._tmpDir, "libarchive.cpio.b64");
    File.WriteAllBytes(inputPath, Payload);

    var wslDir = FsInteropToolbox.WinToWsl(this._tmpDir);
    var wslWrapped = FsInteropToolbox.WinToWsl(wrappedPath);
    var result = FsInteropToolbox.RunWsl(
      $"cd {wslDir} && printf 'payload.txt\\n' | bsdcpio -o --format=odc --b64encode > {wslWrapped}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"bsdcpio failed to create b64encoded cpio:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");

    using var wrapped = File.OpenRead(wrappedPath);
    using var decoded = new MemoryStream();
    new B64EncodingFormatDescriptor().Decompress(wrapped, decoded);
    decoded.Position = 0;

    using var reader = new CpioReader(decoded, leaveOpen: true);
    var entries = reader.ReadAll();
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(entries[0].Entry.Name, Is.EqualTo("payload.txt"));
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
