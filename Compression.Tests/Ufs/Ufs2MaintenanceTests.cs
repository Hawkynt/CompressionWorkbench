#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>
/// UFS2 defragmentation, wiping and block moves: the image keeps its length and every
/// file its bytes, and what each operation promises about the layout holds. The same
/// volumes are judged by <c>fsck_ffs</c> and the kernel in <see cref="Ufs2ExternalTests"/>.
/// </summary>
[TestFixture]
public sealed class Ufs2MaintenanceTests {
  private const int Block = 16384;

  /// <summary>
  /// A volume whose big.bin (19 blocks, so with a single-indirect block) was laid into
  /// the holes two removed files left, around b.bin — so it lies in three pieces.
  /// </summary>
  internal static (MemoryStream Image, Dictionary<string, byte[]> Files) Fragmented() {
    var files = new Dictionary<string, byte[]> {
      ["hello.txt"] = "hello ufs2\n"u8.ToArray(),
      ["a.bin"] = Ufs2WriterTests.Pattern(5 * Block, 1),
      ["b.bin"] = Ufs2WriterTests.Pattern(2 * Block, 2),
      ["c.bin"] = Ufs2WriterTests.Pattern(5 * Block, 3),
      ["keep/small.txt"] = "small\n"u8.ToArray(),
      ["keep/tail.bin"] = Ufs2WriterTests.Pattern(Block + 5000, 4),
    };
    var ms = new MemoryStream();
    ms.Write(Ufs2WriterTests.Create(files));
    var d = new UfsFormatDescriptor();
    d.Remove(ms, ["a.bin", "c.bin"]);
    files.Remove("a.bin");
    files.Remove("c.bin");
    var big = Ufs2WriterTests.Pattern(19 * Block - 777, 5);
    d.Add(ms, [ArchiveInputInfo.InMemory("big.bin", big)]);
    files["big.bin"] = big;
    return (ms, files);
  }

  internal static void AssertHolds(Stream image, IReadOnlyDictionary<string, byte[]> files) {
    image.Position = 0;
    using var r = new UfsReader(image, leaveOpen: true);
    var regular = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(regular.Keys, Is.EquivalentTo(files.Keys));
    foreach (var (name, data) in files)
      Assert.That(r.Extract(regular[name]), Is.EqualTo(data), name);
  }

  private static List<DefragBlockInfo> Pieces(Stream image, string name)
    => [.. new UfsFormatDescriptor().EnumerateExtents(image).Where(e => e.Kind == DefragBlockKind.Used && e.FileName == name)];

  [Test]
  public void GivenAFileInThreePieces_WhenDefragmented_ThenEveryFileIsOneRunAndKeepsItsBytes() {
    var (ms, files) = Fragmented();
    Assert.That(Pieces(ms, "big.bin"), Has.Count.GreaterThan(1), "the scenario fragments big.bin");
    var length = ms.Length;

    new UfsFormatDescriptor().Defragment(ms);

    Assert.That(ms.Length, Is.EqualTo(length));
    AssertHolds(ms, files);
    foreach (var name in files.Keys)
      Assert.That(Pieces(ms, name), Has.Count.EqualTo(1), name);
    // 19 data blocks and the single-indirect block, in one run.
    Assert.That(Pieces(ms, "big.bin")[0].Length, Is.EqualTo(20L * Block));
  }

  [Test]
  public void GivenADefragmentedVolume_WhenDefragmentedAgain_ThenNothingMoves() {
    var (ms, _) = Fragmented();
    var d = new UfsFormatDescriptor();
    d.Defragment(ms);
    var once = ms.ToArray();
    Assert.That(UfsModifier.Defragment(ms), Is.Zero);
    Assert.That(ms.ToArray(), Is.EqualTo(once));
  }

  [Test]
  public void GivenAFragmentedVolume_WhenPackedTowardsTheEnd_ThenFilesAreOneRunAndMovedUp() {
    var (ms, files) = Fragmented();
    var before = Pieces(ms, "b.bin")[0].Offset;
    new UfsFormatDescriptor().Defragment(ms, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });
    AssertHolds(ms, files);
    foreach (var name in files.Keys)
      Assert.That(Pieces(ms, name), Has.Count.EqualTo(1), name);
    Assert.That(Pieces(ms, "b.bin")[0].Offset, Is.GreaterThan(before));
  }

  [TestCase(DefragMode.FillHolesLazy)]
  [TestCase(DefragMode.CarveHole)]
  public void GivenAUfs2Volume_WhenAnUnofferedModeIsAsked_ThenItIsRefusedAndUntouched(DefragMode mode) {
    var (ms, _) = Fragmented();
    var before = ms.ToArray();
    Assert.Throws<NotSupportedException>(() => new UfsFormatDescriptor().Defragment(ms, new DefragOptions { Mode = mode }));
    Assert.That(ms.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenLeftoversInFreeSpaceAndTips_WhenWiped_ThenTheyAreZeroAndFilesKeepTheirBytes() {
    var files = new Dictionary<string, byte[]> {
      ["hello.txt"] = "hello ufs2\n"u8.ToArray(),
      ["keep/tail.bin"] = Ufs2WriterTests.Pattern(Block + 5000, 4),
    };
    using var ms = new MemoryStream();
    ms.Write(Ufs2WriterTests.Create(files));
    // A removed file whose data is left behind, and garbage past hello.txt's last byte.
    var secret = Enumerable.Repeat((byte)0xA5, 3 * Block).ToArray();
    UfsModifier.AddFile(ms, "secret.bin", secret);
    var secretAt = Pieces(ms, "secret.bin")[0].Offset;
    Assert.That(UfsModifier.RemoveFile(ms, "secret.bin", wipeData: false), Is.True);
    var helloAt = Pieces(ms, "hello.txt")[0].Offset;
    ms.Position = helloAt + 11;
    ms.Write(Enumerable.Repeat((byte)0xEE, 100).ToArray());
    var length = ms.Length;

    var wiped = new UfsFormatDescriptor().WipeUnusedSpace(ms);

    Assert.That(wiped, Is.GreaterThanOrEqualTo(3L * Block + 100));
    Assert.That(ms.Length, Is.EqualTo(length));
    AssertHolds(ms, files);
    var image = ms.ToArray();
    Assert.That(image.AsSpan((int)secretAt, 3 * Block).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "freed data");
    Assert.That(image.AsSpan((int)helloAt + 11, 2048 - 11).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "tip");
  }

  [Test]
  public void GivenAWipedVolume_WhenWipedAgain_ThenTheImageIsUnchanged() {
    var (ms, _) = Fragmented();
    var d = new UfsFormatDescriptor();
    d.WipeUnusedSpace(ms);
    var once = ms.ToArray();
    d.WipeUnusedSpace(ms);
    Assert.That(ms.ToArray(), Is.EqualTo(once));
  }

  /// <summary>Moves b.bin into the hole a.bin left: the reader follows the new pointers.</summary>
  internal static (MemoryStream Image, Dictionary<string, byte[]> Files, long To) Moved(bool zeroSource) {
    var files = new Dictionary<string, byte[]> {
      ["a.bin"] = Ufs2WriterTests.Pattern(5 * Block, 1),
      ["dir/b.bin"] = Ufs2WriterTests.Pattern(14 * Block, 2),
      ["c.txt"] = "c\n"u8.ToArray(),
    };
    var ms = new MemoryStream();
    ms.Write(Ufs2WriterTests.Create(files));
    var d = new UfsFormatDescriptor();
    var hole = Pieces(ms, "a.bin")[0].Offset;
    d.Remove(ms, ["a.bin"]);
    files.Remove("a.bin");
    // dir/b.bin: the first five of its blocks move into the hole, the rest stay.
    var b = Pieces(ms, "dir/b.bin")[0];
    d.MoveExtent(ms, b.Offset, hole, 5L * Block, zeroSource);
    d.UpdateAllocationAfterMove(ms, "dir/b.bin", b.Offset, hole, 5L * Block);
    return (ms, files, hole);
  }

  [TestCase(false)]
  [TestCase(true)]
  public void GivenABlockRunMovedIntoAHole_WhenRead_ThenTheFileFollowsAndKeepsItsBytes(bool zeroSource) {
    var (ms, files, to) = Moved(zeroSource);
    AssertHolds(ms, files);
    Assert.That(Pieces(ms, "dir/b.bin")[0].Offset, Is.EqualTo(to));
  }

  [Test]
  public void GivenADestinationAnotherFileHolds_WhenMoved_ThenItIsRefusedAndUntouched() {
    var files = new Dictionary<string, byte[]> {
      ["a.bin"] = Ufs2WriterTests.Pattern(2 * Block, 1),
      ["b.bin"] = Ufs2WriterTests.Pattern(2 * Block, 2),
    };
    using var ms = new MemoryStream();
    ms.Write(Ufs2WriterTests.Create(files));
    var before = ms.ToArray();
    var a = Pieces(ms, "a.bin")[0];
    var b = Pieces(ms, "b.bin")[0];
    Assert.Throws<NotSupportedException>(() => new UfsFormatDescriptor().MoveExtent(ms, a.Offset, b.Offset, a.Length));
    Assert.That(ms.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenARangeThatCutsATailRun_WhenRepointed_ThenItIsRefusedAndUntouched() {
    var files = new Dictionary<string, byte[]> { ["t.bin"] = Ufs2WriterTests.Pattern(6000, 1) }; // three fragments
    using var ms = new MemoryStream();
    ms.Write(Ufs2WriterTests.Create(files));
    var before = ms.ToArray();
    var t = Pieces(ms, "t.bin")[0];
    Assert.Throws<NotSupportedException>(() =>
      new UfsFormatDescriptor().UpdateAllocationAfterMove(ms, "t.bin", t.Offset, t.Offset + 64 * Block, 2048));
    Assert.That(ms.ToArray(), Is.EqualTo(before));
  }

  [TestCase("makefs-ufs2-le.img.gz")]
  [TestCase("newfs-ufs2.img.gz")]
  public void GivenAReferenceUfs2Volume_WhenDefragmentedAndWiped_ThenEveryFileKeepsItsBytes(string vector) {
    using var resource = typeof(Ufs2MaintenanceTests).Assembly.GetManifestResourceStream("UfsVectors." + vector)!;
    using var gzip = new System.IO.Compression.GZipStream(resource, System.IO.Compression.CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gzip.CopyTo(ms);
    ms.Position = 0;
    Dictionary<string, byte[]> files;
    using (var r = new UfsReader(ms, leaveOpen: true))
      files = r.Entries.Where(e => !e.IsDirectory && !e.IsSymlink).ToDictionary(e => e.Name, r.Extract);
    var length = ms.Length;
    var d = new UfsFormatDescriptor();
    d.Defragment(ms);
    d.WipeUnusedSpace(ms);
    Assert.That(ms.Length, Is.EqualTo(length));
    ms.Position = 0;
    using var after = new UfsReader(ms, leaveOpen: true);
    foreach (var (name, data) in files)
      Assert.That(after.Extract(after.Entries.Single(e => e.Name == name)), Is.EqualTo(data), name);
  }
}
