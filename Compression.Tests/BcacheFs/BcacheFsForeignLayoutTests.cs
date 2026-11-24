using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// Volumes laid out by <c>bcachefs format</c> rather than this package's writer: other bucket
/// sizes (32 KiB, the 128 KiB default, 256 KiB) and the journal where the tool put it. Adds and
/// removes work in the volume's own buckets; defragmentation, which moves things within the
/// writer's layout, is still refused there.
/// </summary>
[TestFixture]
public sealed class BcacheFsForeignLayoutTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_bcachefs_foreign_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  /// <summary>The empty 1.3 volume Ubuntu's <c>bcachefs format --bucket=32k</c> made (ReferenceVectors).</summary>
  private static MemoryStream Formatted() {
    using var resource = typeof(BcacheFsForeignLayoutTests).Assembly.GetManifestResourceStream("BcacheFsVectors.bcachefs-tools-1.3-format.img.gz")!;
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  private static byte[] Payload(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static Dictionary<string, string> Read(Stream image) {
    image.Position = 0;
    using var reader = new BcacheFsReader(image);
    Assert.That(reader.Valid, Is.True, reader.Status);
    var files = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var entry in reader.Entries.Where(static e => e.LinkTarget == null)) {
      files[entry.Name] = Convert.ToHexStringLower(SHA256.HashData(reader.Read(entry)));
    }
    image.Position = 0;
    return files;
  }

  /// <summary>Adds a spread of files (one spanning several of the volume's buckets), removes one, replaces one.</summary>
  private static Dictionary<string, string> Edit(Stream image) {
    var descriptor = new BcacheFsFormatDescriptor();
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["ours/small.txt"] = Encoding.ASCII.GetBytes("a small file\n"),
      ["ours/big.bin"] = Payload(700_000, 1),
      ["ours/deep/a/b/c.bin"] = Payload(70_000, 2),
      ["ours/gone.bin"] = Payload(50_000, 3),
      ["ours/replaced.bin"] = Payload(40_000, 4),
    };
    image.Position = 0;
    descriptor.Add(image, [.. files.Select(static f => ArchiveInputInfo.InMemory(f.Key, f.Value))]);
    image.Position = 0;
    descriptor.Remove(image, ["ours/gone.bin"]);
    image.Position = 0;
    var replacement = Payload(90_000, 5);
    descriptor.Add(image, [ArchiveInputInfo.InMemory("ours/replaced.bin", replacement)]);
    files.Remove("ours/gone.bin");
    files["ours/replaced.bin"] = replacement;
    image.Position = 0;
    return files.ToDictionary(static f => f.Key, static f => Convert.ToHexStringLower(SHA256.HashData(f.Value)), StringComparer.Ordinal);
  }

  [Test, Category("HappyPath")]
  public void GivenTheVolumeBcachefsFormatMade_WhenFilesAreAddedRemovedAndReplaced_ThenTheyReadBack() {
    using var image = Formatted();
    var expected = Edit(image);
    Assert.That(Read(image), Is.EquivalentTo(expected));
  }

  [TestCase(DefragMode.ConsolidateAtStart, MetadataZone.Unchanged, TestName = "GivenTheVolumeBcachefsFormatMade_WhenConsolidatedAtStart_ThenEveryFileReadsBack")]
  [TestCase(DefragMode.ConsolidateAtEnd, MetadataZone.Unchanged, TestName = "GivenTheVolumeBcachefsFormatMade_WhenConsolidatedAtEnd_ThenEveryFileReadsBack")]
  [TestCase(DefragMode.ConsolidateAtStart, MetadataZone.Back, TestName = "GivenTheVolumeBcachefsFormatMade_WhenMetadataMovesToTheBack_ThenEveryFileReadsBack")]
  [Category("HappyPath")]
  public void DefragmentFormatted(DefragMode mode, MetadataZone zone) {
    using var image = Formatted();
    var expected = Edit(image);
    var length = image.Length;
    new BcacheFsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode, MetadataZonePlacement = zone });
    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(length), "defragmentation keeps the volume's size");
      Assert.That(Read(image), Is.EquivalentTo(expected), "and every file's bytes");
    });
  }

  [TestCase(1L, 0L, 100u, 1L, TestName = "GivenTheWritersUnits_WhenATimeIsEncoded_ThenOneUnitIsOneTick")]
  [TestCase(1L, 123L, 1u, -23L, TestName = "GivenNanosecondsFromAFormatTimeBase_WhenAnEarlierTimeIsEncoded_ThenItIsNegative")]
  [TestCase(10L, 1L, 1u, 999L, TestName = "GivenNanosecondsFromAFormatTimeBase_WhenATimeIsEncoded_ThenTheBaseIsSubtracted")]
  [TestCase(0L, 50L, 100u, -1L, TestName = "GivenATimeBetweenTwoUnits_WhenEncoded_ThenItRoundsDownNotTowardZero")]
  [Category("Boundary")]
  public void TimeUnits(long ticks, long timeBase, uint precision, long expected) {
    using (BcacheFsBucketGeometry.Use(128, 1, timeBase, precision))
      Assert.That(unchecked((long)BcacheFsWriter.ToBcacheTime(DateTimeOffset.UnixEpoch.AddTicks(ticks))), Is.EqualTo(expected));
    Assert.That(BcacheFsWriter.ToBcacheTime(DateTimeOffset.UnixEpoch.AddTicks(ticks)), Is.EqualTo(unchecked((ulong)ticks)),
      "outside the scope the writer's units apply again");
  }

  [TestCase(4, TestName = "GivenABucketThatIsNoPowerOfTwoBlock_WhenUsed_ThenItIsRefused")]
  [TestCase(96, TestName = "GivenABucketThatIsNoPowerOfTwo_WhenUsed_ThenItIsRefused")]
  [Category("ErrorHandling")]
  public void BadBuckets(int sectors)
    => Assert.Throws<NotSupportedException>(() => BcacheFsBucketGeometry.Use(sectors));

  private string Format(string name, string size, string options) {
    var path = Path.Combine(this._tmpDir, name);
    var wsl = FsInteropToolbox.WinToWsl(path);
    var run = FsInteropToolbox.RunWsl($"truncate -s {size} {wsl} && bcachefs format -q {options} {wsl}");
    Assert.That(run.ExitCode, Is.Zero, $"bcachefs format {options}: {run.StdOut}{run.StdErr}");
    return path;
  }

  [TestCase("64M", "", TestName = "GivenADefaultBcachefsFormatVolume_WhenEdited_ThenFsckIsClean")]
  [TestCase("64M", "--bucket=32k --btree_node_size=32k", TestName = "GivenA32KBucketVolume_WhenEdited_ThenFsckIsClean")]
  [TestCase("128M", "--bucket=256k --btree_node_size=256k", TestName = "GivenA256KBucketVolume_WhenEdited_ThenFsckIsClean")]
  [Category("ExternalFsInterop")]
  public void FormattedEditedFsck(string size, string options) {
    BcacheFsOracleGate.RequireBcachefsTools();
    var path = this.Format("formatted.img", size, options);
    BcacheFsKernel.AssertFsckClean(path);
    Dictionary<string, string> expected;
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      expected = Edit(image);
      Assert.That(Read(image), Is.EquivalentTo(expected));
    }
    BcacheFsKernel.AssertFsckClean(path);

    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      new BcacheFsFormatDescriptor().Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart, MetadataZonePlacement = MetadataZone.BeforeContent });
      Assert.That(Read(image), Is.EquivalentTo(expected));
    }
    BcacheFsKernel.AssertFsckClean(path);
  }

  /// <summary>
  /// A node smaller than a bucket: the nodes of one commit share metadata buckets. Enough files
  /// that the dirent and inode trees grow interior nodes, so several nodes land in one bucket.
  /// </summary>
  [TestCase("128M", "--bucket=256k --btree_node_size=64k", 40, TestName = "GivenNodesAQuarterOfTheBucket_WhenEdited_ThenFsckIsClean")]
  [TestCase("256M", "--bucket=128k --btree_node_size=16k", 600, TestName = "GivenManyNodesPerBucket_WhenManyFilesAreAdded_ThenFsckIsClean")]
  [Category("ExternalFsInterop")]
  public void SmallNodes(string size, string options, int fileCount) {
    BcacheFsOracleGate.RequireBcachefsTools();
    var path = this.Format("small-nodes.img", size, options);
    Dictionary<string, string> expected;
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      expected = Edit(image);
      var many = Enumerable.Range(0, fileCount)
        .Select(i => (Name: $"many/dir{i % 7}/file-with-a-longish-name-{i:D4}.txt", Data: Encoding.ASCII.GetBytes($"file {i}\n")))
        .ToArray();
      new BcacheFsFormatDescriptor().Add(image, [.. many.Select(static f => ArchiveInputInfo.InMemory(f.Name, f.Data))]);
      foreach (var (name, data) in many) expected[name] = Convert.ToHexStringLower(SHA256.HashData(data));
      image.Position = 0;
      new BcacheFsFormatDescriptor().Remove(image, [.. many.Where((_, i) => i % 5 == 0).Select(f => f.Name)]);
      foreach (var (name, _) in many.Where((_, i) => i % 5 == 0)) expected.Remove(name);
      Assert.That(Read(image), Is.EquivalentTo(expected));
    }
    BcacheFsKernel.AssertFsckClean(path);
  }
}
