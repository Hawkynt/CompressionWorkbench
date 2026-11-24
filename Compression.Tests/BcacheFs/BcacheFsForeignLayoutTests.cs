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

  [Test, Category("ErrorHandling")]
  public void GivenTheVolumeBcachefsFormatMade_WhenDefragmented_ThenItIsRefusedAndUnchanged() {
    using var image = Formatted();
    var before = image.ToArray();
    Assert.Throws<NotSupportedException>(() =>
      new BcacheFsFormatDescriptor().Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart }));
    Assert.That(image.ToArray().SequenceEqual(before), Is.True);
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
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenANodeSizeSmallerThanTheBucket_WhenEdited_ThenItIsRefusedAndUnchanged() {
    BcacheFsOracleGate.RequireBcachefsTools();
    var path = this.Format("split.img", "64M", "--bucket=256k --btree_node_size=64k");
    var before = File.ReadAllBytes(path);
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      Assert.Throws<NotSupportedException>(() =>
        new BcacheFsFormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("x.txt", [1])]));
    Assert.That(File.ReadAllBytes(path).SequenceEqual(before), Is.True);
  }
}
