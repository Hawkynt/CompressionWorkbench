using System.Security.Cryptography;
using System.Text;
using Compression.Lib;
using FileFormat.AppleSparse;

namespace Compression.Tests.AppleSparse;

/// <summary>
/// Our sparsebundle reader against bundles Apple's hdiutil wrote. The expected media hashes were
/// published by the projects the bundles come from (keramics' test, ewfprobe's manifest of the
/// image hdiutil was handed) and libmodi reads the same bytes; see ReferenceVectors/README.md.
/// The live libmodi comparison is <see cref="SparsebundleLibmodiOracleTests"/>.
/// </summary>
[TestFixture]
public sealed class SparsebundleReferenceVectorTests {

  private const string HfsPlusMediaMd5 = "7adf013daec71e509669a9315a6a173c";
  private const string EwfprobeMediaSha256 = "770be732aafc4962c6940a2076c35471419621d9d6ca57cda60cbd6e82a8b36f";
  private const int MiB = 1024 * 1024;

  private string _tmp = null!;

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_sbref_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilHfsPlusBundle_WhenReadingTheMedia_ThenItMatchesKeramicsAndLibmodi() {
    var reader = new SparsebundleReader(SparsebundleVectors.Materialize(SparsebundleVectors.HfsPlus, this._tmp));
    var disk = reader.ExtractDisk();
    Assert.Multiple(() => {
      Assert.That(reader.BandSize, Is.EqualTo(8 * MiB));
      Assert.That(reader.VirtualSize, Is.EqualTo(4 * MiB));
      Assert.That(disk.Length, Is.EqualTo(4 * MiB));
      Assert.That(Hex(MD5.HashData(disk)), Is.EqualTo(HfsPlusMediaMd5));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilUdsbBundle_WhenReadingTheMedia_ThenItIsTheImageHdiutilWasGiven() {
    var reader = new SparsebundleReader(SparsebundleVectors.Materialize(SparsebundleVectors.Ewfprobe, this._tmp));
    var disk = reader.ExtractDisk();
    Assert.Multiple(() => {
      Assert.That(reader.BandSize, Is.EqualTo(MiB));
      Assert.That(reader.VirtualSize, Is.EqualTo(3 * MiB));
      Assert.That(Hex(SHA256.HashData(disk)), Is.EqualTo(EwfprobeMediaSha256));
    });
  }

  /// <summary>hdiutil wrote a GUID partition map; the HFS+ volume keramics filled is its one partition.</summary>
  private static string HfsPartitionPrefix(IEnumerable<string> names)
    => names.Select(static n => n.Split('/')[0]).Distinct().Single(static p => p.StartsWith("Partition", StringComparison.Ordinal));

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilHfsPlusBundle_WhenListedThroughInfoPlist_ThenTheVolumeFilesKeramicsWroteAppear() {
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.HfsPlus, this._tmp);
    using var plist = File.OpenRead(Path.Combine(root, "Info.plist"));
    var names = new SparsebundleFormatDescriptor().List(plist, null).Select(e => e.Name.Replace('\\', '/')).ToList();
    var volume = HfsPartitionPrefix(names);
    Assert.Multiple(() => {
      Assert.That(volume, Does.Contain("Apple_HFS"));
      foreach (var entry in new[] { "emptyfile", "file_hardlink1", "testdir1/testfile1", "testdir1/xattr1", "testdir1/large_xattr", "testdir1/resourcefork1" })
        Assert.That(names, Does.Contain($"{volume}/{entry}"), entry);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilHfsPlusBundle_WhenExtracted_ThenTheSymlinksPointWhereKeramicsMadeThem() {
    // keramics' create_file_entries ran `ln -s "${MOUNT_POINT}/testdir1/testfile1"` and
    // `ln -s "${MOUNT_POINT}/testdir1"` with MOUNT_POINT=/Volumes/hfsplus_test.
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.HfsPlus, this._tmp);
    var output = Path.Combine(this._tmp, "out");
    var descriptor = new SparsebundleFormatDescriptor();
    string volume;
    using (var plist = File.OpenRead(Path.Combine(root, "Info.plist")))
      volume = HfsPartitionPrefix(descriptor.List(plist, null).Select(static e => e.Name.Replace('\\', '/')));
    using (var plist = File.OpenRead(Path.Combine(root, "Info.plist")))
      descriptor.Extract(plist, output, null, [$"{volume}/file_symboliclink1", $"{volume}/directory_symboliclink1", $"{volume}/emptyfile"]);
    Assert.Multiple(() => {
      Assert.That(File.ReadAllText(Path.Combine(output, volume, "file_symboliclink1")), Is.EqualTo("/Volumes/hfsplus_test/testdir1/testfile1"));
      Assert.That(File.ReadAllText(Path.Combine(output, volume, "directory_symboliclink1")), Is.EqualTo("/Volumes/hfsplus_test/testdir1"));
      Assert.That(File.ReadAllBytes(Path.Combine(output, volume, "emptyfile")), Is.Empty);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilUdsbBundle_WhenListed_ThenTheRawMediaIsOneDiskImage() {
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.Ewfprobe, this._tmp);
    using var plist = File.OpenRead(Path.Combine(root, "Info.plist"));
    var entries = new SparsebundleFormatDescriptor().List(plist, null);
    var disk = entries.Single(e => e.Name == "disk.img");
    Assert.That(disk.OriginalSize, Is.EqualTo(3 * MiB));
  }

  // ── Boundaries derived from the real bundles ──────────────────────

  [TestCase(MiB - 7, 14, TestName = "GivenAReadAcrossTheBand0To1Boundary_WhenRead_ThenBothBandsContribute")]
  [TestCase(2 * MiB - 1, 2, TestName = "GivenAReadAcrossTheBand1To2Boundary_WhenRead_ThenBothBandsContribute")]
  [TestCase(3 * MiB - 1, 1, TestName = "GivenTheLastMediaByte_WhenRead_ThenItIsReturned")]
  [TestCase(0, 3 * MiB, TestName = "GivenTheWholeMedia_WhenReadInOneCall_ThenItIsTheBandConcatenation")]
  [Category("BoundaryCase")]
  public void Stream_ReadsAcrossBandBoundariesOfARealBundle(int offset, int count) {
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.Ewfprobe, this._tmp);
    var expected = Concat(root, 3).AsSpan(offset, count).ToArray();
    using var stream = new SparsebundleStream(new SparsebundleReader(root));
    stream.Position = offset;
    var actual = new byte[count];
    stream.ReadExactly(actual);
    Assert.That(actual, Is.EqualTo(expected));
  }

  [Test, Category("BoundaryCase")]
  public void GivenReadsPastTheMediaEnd_WhenRead_ThenNothingIsReturned() {
    var reader = new SparsebundleReader(SparsebundleVectors.Materialize(SparsebundleVectors.Ewfprobe, this._tmp));
    Assert.That(reader.Read(3 * MiB, new byte[16]), Is.Zero);
  }

  [Test, Category("BoundaryCase")]
  public void GivenAMissingMiddleBand_WhenRead_ThenItsRangeIsZerosAndItsNeighboursAreIntact() {
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.Ewfprobe, this._tmp);
    var expected = Concat(root, 3);
    Array.Clear(expected, MiB, MiB);
    File.Delete(Path.Combine(root, "bands", "1"));
    Assert.That(new SparsebundleReader(root).ExtractDisk(), Is.EqualTo(expected));
  }

  [Test, Category("BoundaryCase")]
  public void GivenAMediumSmallerThanOneBand_WhenRead_ThenTheShortBandIsTheWholeMedia() {
    // hdiutil stores the 4 MiB HFS+ medium as one 4 MiB file under an 8 MiB band size.
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.HfsPlus, this._tmp);
    var band = File.ReadAllBytes(Path.Combine(root, "bands", "0"));
    var reader = new SparsebundleReader(root);
    Assert.Multiple(() => {
      Assert.That(band.Length, Is.LessThan(reader.BandSize));
      Assert.That(reader.ExtractDisk(), Is.EqualTo(band));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenABandFileShorterThanTheBandSize_WhenRead_ThenTheMissingTailIsZeros() {
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.Ewfprobe, this._tmp);
    var expected = Concat(root, 3);
    var band = Path.Combine(root, "bands", "1");
    File.WriteAllBytes(band, File.ReadAllBytes(band)[..4096]);
    Array.Clear(expected, MiB + 4096, MiB - 4096);
    Assert.That(new SparsebundleReader(root).ExtractDisk(), Is.EqualTo(expected));
  }

  [Test, Category("BoundaryCase")]
  public void GivenNoBandFilesAtAll_WhenRead_ThenTheMediaIsAllZeros() {
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.Ewfprobe, this._tmp);
    foreach (var band in Directory.GetFiles(Path.Combine(root, "bands"))) File.Delete(band);
    var disk = new SparsebundleReader(root).ExtractDisk();
    Assert.Multiple(() => {
      Assert.That(disk.Length, Is.EqualTo(3 * MiB));
      Assert.That(disk.All(static b => b == 0), Is.True);
    });
  }

  [Test, Category("EquivalenceClass")]
  public void GivenTheRealPlistWithCrLfLineEndings_WhenParsed_ThenGeometryIsUnchanged() {
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.Ewfprobe, this._tmp);
    var path = Path.Combine(root, "Info.plist");
    File.WriteAllText(path, File.ReadAllText(path).ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
    var reader = new SparsebundleReader(root);
    Assert.Multiple(() => {
      Assert.That(reader.BandSize, Is.EqualTo(MiB));
      Assert.That(reader.VirtualSize, Is.EqualTo(3 * MiB));
    });
  }

  private static byte[] Concat(string root, int bands)
    => Enumerable.Range(0, bands).SelectMany(i => File.ReadAllBytes(Path.Combine(root, "bands", i.ToString("x")))).ToArray();
}
