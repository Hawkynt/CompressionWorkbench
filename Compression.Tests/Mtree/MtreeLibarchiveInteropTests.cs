using Compression.Registry;
using FileFormat.Mtree;

namespace Compression.Tests.Mtree;

[TestFixture]
[Category("ArchiveExternalInterop")]
public class MtreeLibarchiveInteropTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_mtree_libarchive_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  [Test]
  public void CwbWriter_ManifestListsWithLibarchive() {
    RequireBsdtar();

    var manifestPath = Path.Combine(this._tmpDir, "cwb.mtree");
    using (var target = File.Create(manifestPath)) {
      new MtreeFormatDescriptor().Create(
        target,
        [ArchiveInputInfo.InMemory("payload.txt", "mtree-libarchive"u8)],
        new FormatCreateOptions());
    }

    var result = FsInteropToolbox.RunWsl($"bsdtar -tf {FsInteropToolbox.WinToWsl(manifestPath)}");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"libarchive rejected the CWB mtree manifest:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Contain("payload.txt"));
  }

  [Test]
  public void LibarchiveWriter_ManifestReadsWithCwb() {
    RequireBsdtar();

    var inputPath = Path.Combine(this._tmpDir, "payload.txt");
    var manifestPath = Path.Combine(this._tmpDir, "libarchive.mtree");
    File.WriteAllBytes(inputPath, "mtree-libarchive"u8.ToArray());

    var result = FsInteropToolbox.RunWsl(
      $"bsdtar --format=mtree -cf {FsInteropToolbox.WinToWsl(manifestPath)} " +
      $"-C {FsInteropToolbox.WinToWsl(this._tmpDir)} payload.txt");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"libarchive failed to create mtree:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");

    using var source = File.OpenRead(manifestPath);
    var listed = new MtreeFormatDescriptor().List(source, password: null);
    var payload = listed.Single(x => x.Name.EndsWith("payload.txt", StringComparison.Ordinal));
    Assert.Multiple(() => {
      Assert.That(payload.IsDirectory, Is.False);
      Assert.That(payload.OriginalSize, Is.EqualTo(new FileInfo(inputPath).Length));
    });
  }

  private static void RequireBsdtar() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL/Linux shell unavailable; install WSL to run libarchive interoperability tests.");
    if (!FsInteropToolbox.WslHasTool("bsdtar"))
      Assert.Ignore("'bsdtar' unavailable; install libarchive-tools in the WSL/Linux environment.");
  }
}
