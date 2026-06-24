#pragma warning disable CS1591
using System.Text;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// External-tool acceptance gate that operates on REAL reference NTFS images —
/// volumes formatted by <c>mkfs.ntfs</c> and populated by <c>ntfscp</c>, not by our
/// own writer. It proves that <see cref="NtfsReader"/>, <see cref="NtfsInPlaceAdder"/>
/// and <see cref="NtfsRemover"/> work against the structures real tooling produces
/// (large reserved MFT zone, real $UpCase/$LogFile/$Secure, non-resident $Bitmap, the
/// reference update-sequence-array layout at usa_ofs 48, and a root directory that
/// spills into $INDEX_ALLOCATION as it grows) and that the result stays
/// <c>ntfsfix</c>-clean and byte-readable by <c>ntfsls</c>/<c>ntfscat</c>.
/// Skips cleanly when the ntfs-3g userspace tooling is unavailable.
/// </summary>
[TestFixture]
[Category("ExternalConformance")]
public class NtfsRealImageExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ntfs_real_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static void RequireTools(params string[] tools) {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("No Linux shell / WSL available.");
    foreach (var t in tools)
      if (!FsInteropToolbox.WslHasTool(t))
        Assert.Ignore($"ntfs-3g tooling '{t}' not installed (apt install ntfs-3g).");
  }

  // Formats a real NTFS volume of the given size with mkfs.ntfs (fast, forced,
  // 512-byte sectors so the geometry matches the common reference layout) and
  // returns its Windows/host path. An optional explicit cluster size exercises the
  // large-cluster mirror span (where $MFTMirr covers the root directory record).
  private string MakeRealImage(long sizeBytes = 64L * 1024 * 1024, int clusterSize = 0) {
    var img = Path.Combine(this._tmpDir, "real.ntfs");
    var wsl = FsInteropToolbox.WinToWsl(img);
    var clusterArg = clusterSize > 0 ? $"-c {clusterSize}" : "-s 512";
    var r = FsInteropToolbox.RunWsl(
      $"dd if=/dev/zero of={wsl} bs=1M count={sizeBytes / (1024 * 1024)} status=none && " +
      $"mkfs.ntfs --fast -F {clusterArg} {wsl}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"mkfs.ntfs failed:\n{r.StdOut}\n{r.StdErr}");
    return img;
  }

  // Seeds a file into a real NTFS image via ntfscp (no mount needed).
  private void NtfscpInto(string img, string nameInImage, byte[] content) {
    var src = Path.Combine(this._tmpDir, $"seed_{Guid.NewGuid():N}.bin");
    File.WriteAllBytes(src, content);
    var r = FsInteropToolbox.RunWsl(
      $"ntfscp {FsInteropToolbox.WinToWsl(img)} {FsInteropToolbox.WinToWsl(src)} {nameInImage}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"ntfscp seed failed:\n{r.StdOut}\n{r.StdErr}");
  }

  private static void AssertNtfsfixClean(string img) {
    var r = FsInteropToolbox.RunWsl($"ntfsfix -n {FsInteropToolbox.WinToWsl(img)}");
    var combined = r.StdOut + "\n" + r.StdErr;
    Assert.That(combined,
      Does.Contain("Processing of $MFT and $MFTMirr completed successfully").IgnoreCase,
      $"ntfsfix flagged the real image:\n{combined}");
    Assert.That(combined, Does.Not.Contain("inconsistent").IgnoreCase, combined);
    Assert.That(combined, Does.Not.Contain("corrupt").IgnoreCase, combined);
  }

  [Test, CancelAfter(120_000)]
  public void RealImage_InPlaceAdd_ReadableByNtfscat_AndNtfsfixClean() {
    RequireTools("mkfs.ntfs", "ntfscp", "ntfsls", "ntfscat", "ntfsfix");

    var img = MakeRealImage();
    NtfscpInto(img, "preexisting.txt", Encoding.ASCII.GetBytes("Hello from mkfs.ntfs world"));

    // Add a resident (small) and a non-resident (multi-cluster) file in place.
    var image = File.ReadAllBytes(img);
    var smallContent = Encoding.ASCII.GetBytes("in-place-resident-payload");
    var bigContent = new byte[60000];
    new Random(7).NextBytes(bigContent);
    NtfsInPlaceAdder.AddFile(image, "small.txt", smallContent);
    NtfsInPlaceAdder.AddFile(image, "large.bin", bigContent);
    File.WriteAllBytes(img, image);

    var expected = Path.Combine(this._tmpDir, "large_expected.bin");
    File.WriteAllBytes(expected, bigContent);
    var wsl = FsInteropToolbox.WinToWsl(img);

    var ls = FsInteropToolbox.RunWsl($"ntfsls {wsl}");
    Assert.That(ls.StdOut, Does.Contain("small.txt").And.Contain("large.bin").And.Contain("preexisting.txt"),
      $"ntfsls did not list the in-place-added + preexisting files:\n{ls.StdOut}\n{ls.StdErr}");

    var small = FsInteropToolbox.RunWsl($"ntfscat {wsl} small.txt");
    Assert.That(small.StdOut, Does.Contain("in-place-resident-payload"),
      $"ntfscat could not read the in-place resident file:\n{small.StdOut}\n{small.StdErr}");

    var pre = FsInteropToolbox.RunWsl($"ntfscat {wsl} preexisting.txt");
    Assert.That(pre.StdOut, Does.Contain("Hello from mkfs.ntfs world"),
      $"the preexisting mkfs.ntfs file is no longer readable after the in-place add:\n{pre.StdOut}");

    var big = FsInteropToolbox.RunWsl(
      $"ntfscat {wsl} large.bin | cmp - {FsInteropToolbox.WinToWsl(expected)} && echo CMP_OK");
    Assert.That(big.StdOut, Does.Contain("CMP_OK"),
      $"ntfscat large.bin did not match the original bytes:\n{big.StdOut}\n{big.StdErr}");

    AssertNtfsfixClean(img);

    // Our own reader must also round-trip the modified real image byte-for-byte.
    using var fs = File.OpenRead(img);
    var reader = new NtfsReader(fs);
    var entry = reader.Entries.Single(e => e.Name == "large.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(bigContent),
      "NtfsReader did not extract the in-place-added file from the real image byte-equal.");
  }

  [Test, CancelAfter(180_000)]
  public void RealImage_ManyAdds_SpillIndex_ThenRemove_StaysNtfsfixClean() {
    RequireTools("mkfs.ntfs", "ntfsls", "ntfscat", "ntfsfix");

    // 128 MiB so the MFT can grow (mkfs reserves only ~28 records up front) and the
    // root directory spills into $INDEX_ALLOCATION as files accumulate.
    var img = MakeRealImage(128L * 1024 * 1024);
    var image = File.ReadAllBytes(img);

    const int count = 60;
    for (var i = 1; i <= count; i++)
      NtfsInPlaceAdder.AddFile(image, $"file_{i:000}.txt", Encoding.ASCII.GetBytes($"content-{i}"));

    // Remove a handful from across the (now spilled) directory and a grown,
    // non-contiguous MFT.
    NtfsRemover.Remove(image, "file_030.txt");
    NtfsRemover.Remove(image, "file_001.txt");
    NtfsRemover.Remove(image, "file_060.txt");
    File.WriteAllBytes(img, image);

    var wsl = FsInteropToolbox.WinToWsl(img);
    var ls = FsInteropToolbox.RunWsl($"ntfsls {wsl}");
    Assert.That(ls.StdOut, Does.Not.Contain("file_030.txt"), $"removed file still listed:\n{ls.StdOut}");
    Assert.That(ls.StdOut, Does.Not.Contain("file_001.txt"), $"removed file still listed:\n{ls.StdOut}");
    Assert.That(ls.StdOut, Does.Not.Contain("file_060.txt"), $"removed file still listed:\n{ls.StdOut}");
    Assert.That(ls.StdOut, Does.Contain("file_031.txt"), $"surviving file missing:\n{ls.StdOut}");

    var cat = FsInteropToolbox.RunWsl($"ntfscat {wsl} file_045.txt");
    Assert.That(cat.StdOut, Does.Contain("content-45"),
      $"a surviving file is no longer readable after the removals:\n{cat.StdOut}\n{cat.StdErr}");

    AssertNtfsfixClean(img);

    // Reader agreement: exactly the 57 survivors, names intact.
    using var fs = File.OpenRead(img);
    var reader = new NtfsReader(fs);
    var names = reader.Entries.Select(e => e.Name).Where(n => n.StartsWith("file_")).ToHashSet();
    Assert.That(names, Has.Count.EqualTo(count - 3));
    Assert.That(names, Does.Not.Contain("file_030.txt").And.Contain("file_031.txt"));
  }

  // On large-cluster volumes $MFTMirr mirrors a whole cluster's worth of MFT records
  // (8 at 8 KiB, 16 at 16 KiB, …) — which INCLUDES the root directory (record 5).
  // Editing the directory index therefore has to keep $MFTMirr in sync or ntfsfix's
  // $MFTMirr-vs-$MFT comparison fails. This exercises add (with index spill), a
  // non-resident add, and remove for the cluster sizes where the root is mirrored.
  [Test, CancelAfter(180_000)]
  public void RealImage_LargeClusters_AddSpillRemove_StaysNtfsfixClean(
      [Values(8192, 16384, 32768, 65536)] int clusterSize) {
    RequireTools("mkfs.ntfs", "ntfsls", "ntfscat", "ntfsfix");

    var img = MakeRealImage(192L * 1024 * 1024, clusterSize);
    var image = File.ReadAllBytes(img);

    for (var i = 1; i <= 40; i++)
      NtfsInPlaceAdder.AddFile(image, $"g_{i:000}.dat", Encoding.ASCII.GetBytes($"payload-{i}"));
    var big = new byte[60000];
    new Random(clusterSize).NextBytes(big);
    NtfsInPlaceAdder.AddFile(image, "bigfile.bin", big);
    NtfsRemover.Remove(image, "g_020.dat");
    File.WriteAllBytes(img, image);

    var expected = Path.Combine(this._tmpDir, "big_expected.bin");
    File.WriteAllBytes(expected, big);
    var wsl = FsInteropToolbox.WinToWsl(img);

    var ls = FsInteropToolbox.RunWsl($"ntfsls {wsl}");
    Assert.That(ls.StdOut, Does.Contain("g_021.dat").And.Contain("bigfile.bin"),
      $"add/spill did not survive at cluster size {clusterSize}:\n{ls.StdOut}");
    Assert.That(ls.StdOut, Does.Not.Contain("g_020.dat"),
      $"removed file still listed at cluster size {clusterSize}:\n{ls.StdOut}");

    var bigCmp = FsInteropToolbox.RunWsl(
      $"ntfscat {wsl} bigfile.bin | cmp - {FsInteropToolbox.WinToWsl(expected)} && echo CMP_OK");
    Assert.That(bigCmp.StdOut, Does.Contain("CMP_OK"),
      $"non-resident content mismatch at cluster size {clusterSize}:\n{bigCmp.StdOut}\n{bigCmp.StdErr}");

    AssertNtfsfixClean(img);
  }
}
