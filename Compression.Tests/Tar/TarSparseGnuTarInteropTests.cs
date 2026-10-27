using FileFormat.Tar;

namespace Compression.Tests.Tar;

/// <summary>
/// GNU tar is the reference writer for every sparse encoding: it builds a real sparse
/// file (on the Linux file system, where holes exist), archives it in each format, and
/// we must reconstruct the file byte for byte.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class TarSparseGnuTarInteropTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_tarsparse_ext_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static byte[] Expected() {
    // Mirrors the shell below: 1 MiB file, "head" at 0, "middle" at 300000, "tail" at 1048570.
    var file = new byte[1_048_576];
    "head"u8.CopyTo(file);
    "middle"u8.CopyTo(file.AsSpan(300_000));
    "tail\n\0"u8[..6].CopyTo(file.AsSpan(1_048_570));
    return file;
  }

  [TestCase("--format=gnu", TestName = "GnuTar_OldGnuSparse_IsReconstructed")]
  [TestCase("--format=pax --sparse-version=0.0", TestName = "GnuTar_PaxSparse00_IsReconstructed")]
  [TestCase("--format=pax --sparse-version=0.1", TestName = "GnuTar_PaxSparse01_IsReconstructed")]
  [TestCase("--format=pax --sparse-version=1.0", TestName = "GnuTar_PaxSparse10_IsReconstructed")]
  public void GnuTarSparseArchive_IsReconstructed(string formatArgs) {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("tar") || !FsInteropToolbox.WslHasTool("truncate"))
      Assert.Ignore("GNU tar / coreutils not available.");
    var archive = Path.Combine(this._tmpDir, "sparse.tar");
    var work = $"/tmp/cwb_sparse_{Guid.NewGuid():N}";
    var r = FsInteropToolbox.RunWsl(
      $"set -e; mkdir -p {work}; cd {work}; " +
      "truncate -s 1048576 sparse.bin; " +
      "printf head | dd of=sparse.bin conv=notrunc status=none; " +
      "printf middle | dd of=sparse.bin bs=1 seek=300000 conv=notrunc status=none; " +
      "printf 'tail\\n\\0' | dd of=sparse.bin bs=1 seek=1048570 conv=notrunc status=none; " +
      $"tar --sparse {formatArgs} -cf out.tar sparse.bin; " +
      $"tar --version | head -1; cp out.tar {FsInteropToolbox.WinToWsl(archive)}; rm -rf {work}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"GNU tar failed:\n{r.StdOut}\n{r.StdErr}");

    using var reader = new TarReader(File.OpenRead(archive));
    var entry = reader.GetNextEntry();
    Assert.That(entry, Is.Not.Null);
    using var ms = new MemoryStream();
    reader.CopyEntryDataTo(ms);
    Assert.Multiple(() => {
      Assert.That(entry!.Name, Is.EqualTo("sparse.bin"));
      Assert.That(entry.IsSparse, Is.True, "GNU tar must have stored the member as sparse");
      Assert.That(entry.Size, Is.EqualTo(1_048_576));
      Assert.That(entry.StoredSize, Is.LessThan(1_048_576));
      Assert.That(ms.ToArray(), Is.EqualTo(Expected()));
    });
  }
}
