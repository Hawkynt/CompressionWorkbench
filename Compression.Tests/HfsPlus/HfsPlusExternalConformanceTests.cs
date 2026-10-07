using System.Text;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Validates that images produced by <see cref="HfsPlusWriter"/> are accepted as
/// structurally sound by the reference HFS+ checker (<c>fsck.hfsplus</c> from
/// hfsprogs). The checker walks the volume header, the catalog and extents
/// B-trees (node descriptors, sibling links, key ordering under the declared
/// compare type, the node-allocation bitmap), the catalog hierarchy and the
/// volume allocation bitmap, reporting any deviation from Apple TN1150. A clean
/// run ("appears to be OK", exit 0) means the writer's on-disk structures match
/// what an independent implementation expects.
/// <para>
/// The tests run only on Linux when <c>fsck.hfsplus</c> is installed; otherwise
/// they are ignored so the suite stays green on machines without hfsprogs.
/// </para>
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class HfsPlusExternalConformanceTests {

  // Run natively on Linux and through WSL on Windows, like the other hfsprogs gates: a check that
  // only ran on a Linux host never ran on the machines these volumes are mostly written on.
  private static void RequireTool(string tool) {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not installed; fsck.hfsplus cannot run on this host");
    if (!FsInteropToolbox.WslHasTool(tool)) Assert.Ignore($"'{tool}' (hfsprogs) is not installed");
  }

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_hfsfsck_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  [Test]
  public void RepresentativeImage_PassesFsckCleanly() {
    RequireTool("fsck.hfsplus");

    var writer = new HfsPlusWriter();

    // Root-level files of differing sizes.
    writer.AddFile("readme.txt", "root file"u8.ToArray());
    writer.AddFile("small.txt", Encoding.ASCII.GetBytes("hello world"));

    // A nested directory tree.
    writer.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    writer.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());

    // A larger, multi-block file.
    var large = new byte[20_000];
    new Random(7).NextBytes(large);
    writer.AddFile("data/random.bin", large);

    // A directory holding ~1000 files: forces the catalog to spill across many
    // leaf nodes joined by an index level, exercising B-tree node descriptors,
    // sibling links and the node-allocation bitmap.
    for (var i = 0; i < 1000; i++)
      writer.AddFile($"bulk/file{i:D4}.dat", Encoding.ASCII.GetBytes($"content-{i}"));

    AssertFsckClean(writer.Build());
  }

  [Test]
  public void MultiLevelCatalogIndex_PassesFsckCleanly() {
    RequireTool("fsck.hfsplus");

    // Enough records that a single index node cannot point at every leaf, so
    // the catalog grows a second index level. Every index node at one level
    // must be chained to its siblings via fLink/bLink, which fsck verifies.
    var writer = new HfsPlusWriter();
    for (var i = 0; i < 5000; i++)
      writer.AddFile($"bulk/file{i:D5}.dat", Encoding.ASCII.GetBytes($"content-{i}"));

    AssertFsckClean(writer.Build());
  }

  [Test]
  public void NonAsciiAndMixedCaseNames_PassesFsckCleanly() {
    RequireTool("fsck.hfsplus");

    // Names whose case-folding order differs from a raw UTF-16 byte order
    // ('Z' < 'a' as bytes, but apple < Zebra when case-folded) plus accented
    // Latin and CJK names. The catalog must be sorted by the declared
    // case-folding compare or fsck reports "Keys out of order".
    var writer = new HfsPlusWriter();
    writer.AddFile("Zebra.txt", "z"u8.ToArray());
    writer.AddFile("apple.txt", "a"u8.ToArray());
    writer.AddFile("café/naïve.txt", "u"u8.ToArray());
    writer.AddFile("日本語/ファイル.txt", "j"u8.ToArray());

    AssertFsckClean(writer.Build());
  }

  [Test]
  public void FragmentedFileRemovedInPlace_PassesFsckCleanly() {
    RequireTool("fsck.hfsplus");

    // A file in three extents out of disk order, removed in place: every extent must be
    // freed, or fsck reports blocks allocated to no file and a wrong free count.
    var image = HfsPlusRemoveExtentsTests.FragmentedVolume(out _);
    AssertFsckClean(image);
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.That(HfsPlusModifier.RemoveFile(ms, "split.bin"), Is.True);
    AssertFsckClean(ms.ToArray());
  }

  [Test]
  public void FileRemovedFromAManyLeafCatalog_PassesFsckCleanly() {
    RequireTool("fsck.hfsplus");

    // A file in a later leaf of a catalog with an index level, removed in place: the leaf
    // chain, index keys, leafRecords and root valence must all still agree.
    var writer = new HfsPlusWriter();
    for (var i = 0; i < 600; i++) writer.AddFile($"file{i:D4}.txt", Encoding.ASCII.GetBytes($"content of file {i}"));
    var image = writer.Build(4096);
    // Take the last file whose records do not open a leaf (those removals are refused).
    for (var i = 599; i >= 0; i--) {
      using var ms = new MemoryStream();
      ms.Write(image);
      try {
        Assert.That(HfsPlusModifier.RemoveFile(ms, $"file{i:D4}.txt"), Is.True);
      } catch (NotSupportedException) {
        continue;
      }
      AssertFsckClean(ms.ToArray());
      return;
    }
    Assert.Fail("every file opened a leaf");
  }

  [TestCase(HfsPlusCompression.Zlib)]
  [TestCase(HfsPlusCompression.Lzvn)]
  [TestCase(HfsPlusCompression.Lzfse)]
  [TestCase(HfsPlusCompression.Lzbitmap)]
  [TestCase(HfsPlusCompression.Raw)]
  [TestCase(HfsPlusCompression.InlineUncompressed)]
  public void TransparentlyCompressedVolume_PassesFsckCleanly(HfsPlusCompression compression) {
    RequireTool("fsck.hfsplus");

    // The attributes B-tree, kHFSHasAttributesMask, UF_COMPRESSED and the resource forks must
    // all be what fsck_hfs expects of a compressed file.
    AssertFsckClean(HfsPlusDecmpfsMethodsTests.CompressedVolume(compression));
  }

  private void AssertFsckClean(byte[] image) {
    var imagePath = Path.Combine(_tmpDir, "volume.hfsplus");
    File.WriteAllBytes(imagePath, image);

    var (stdOut, stdErr, _) = FsInteropToolbox.RunWsl($"fsck.hfsplus -f -n {FsInteropToolbox.WinToWsl(imagePath)}");
    var combined = stdOut + stdErr;

    Assert.That(combined, Does.Contain("appears to be OK"),
      $"fsck.hfsplus did not report the volume clean.\n{combined}");
    Assert.That(combined, Does.Not.Contain("found corrupt"),
      $"fsck.hfsplus reported corruption.\n{combined}");
  }
}
