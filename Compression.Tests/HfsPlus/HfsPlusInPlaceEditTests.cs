using System.Buffers.Binary;
using System.Text;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// In-place edits the editor used to refuse: removing files with extended attributes, with
/// transparent compression and with forks continued in the extents overflow file; adding files
/// compressed, with extended attributes, spread over more than eight free runs, into new folders,
/// and so many that the catalog splits nodes and grows. Each is judged by the volume's own
/// invariants and read back by our reader here, and by <c>fsck.hfsplus</c> and libfshfs in the
/// external tests at the end.
/// </summary>
[TestFixture]
public sealed class HfsPlusInPlaceEditTests {

  private const uint BlockSize = 4096;
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_hfsedit_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static byte[] Add(byte[] image, string name, byte[] data, HfsPlusCompression compression = HfsPlusCompression.None,
      IReadOnlyDictionary<string, byte[]>? attributes = null) {
    using var ms = new MemoryStream();
    ms.Write(image);
    HfsPlusModifier.AddFile(ms, name, data, compression, attributes);
    return ms.ToArray();
  }

  private static byte[] Remove(byte[] image, string name) {
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.That(HfsPlusModifier.RemoveFile(ms, name), Is.True, name);
    return ms.ToArray();
  }

  private static byte[] Read(byte[] image, string path) {
    var reader = new HfsPlusReader(new MemoryStream(image));
    return reader.Extract(reader.Entries.Single(e => e.FullPath == path));
  }

  private static List<string> Paths(byte[] image) => new HfsPlusReader(new MemoryStream(image)).Entries.Select(static e => e.FullPath).ToList();

  /// <summary>The attributes B-tree's records for <paramref name="cnid"/>.</summary>
  private static int AttributeRecords(byte[] image, uint cnid) {
    var volume = HfsPlusVolumeEditor.Open(image.ToArray());
    return volume.Attributes?.Records.Count(r => BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4)) == cnid) ?? 0;
  }

  private static int OverflowRecords(byte[] image, uint cnid) {
    var volume = HfsPlusVolumeEditor.Open(image.ToArray());
    return volume.Extents.Records.Count(r => BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4)) == cnid);
  }

  private static uint Cnid(byte[] image, string path) => new HfsPlusReader(new MemoryStream(image)).AllFiles.Single(e => e.FullPath == path).Cnid;

  private static byte[] Base(params string[] names) {
    var w = new HfsPlusWriter();
    foreach (var name in names) w.AddFile(name, Encoding.UTF8.GetBytes("content of " + name));
    return w.Build(BlockSize);
  }

  /// <summary>
  /// A volume whose free space is <paramref name="runs"/> one-block holes plus a run of
  /// <see cref="Spare"/> blocks at its end (room for the B-trees to grow, too short for any file
  /// the tests add in one piece): the volume is filled, then every other filler block removed.
  /// </summary>
  internal static byte[] Fragmented(int runs) {
    var w = new HfsPlusWriter();
    w.AddFile("keep.txt", "keep"u8.ToArray());
    for (var i = 0; i < 2 * runs; i++) w.AddFile($"filler{i:D3}", new byte[BlockSize]);
    var image = w.Build(BlockSize);
    var free = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(1024 + 48));
    image = Add(image, "tail.bin", new byte[(free - Spare) * BlockSize]);
    for (var i = 0; i < 2 * runs; i += 2) image = Remove(image, $"filler{i:D3}");
    return image;
  }

  private const int Spare = 7;

  // ── Removing ───────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilFileWithExtendedAttributes_WhenRemoved_ThenItsAttributeRecordsGoToo() {
    var image = HfsPlusLinksAndNamesTests.KeramicsVolume(this._tmp);
    var cnid = Cnid(image, "testdir1/xattr1");
    Assert.That(AttributeRecords(image, cnid), Is.GreaterThan(0), "precondition: macOS gave it attributes");
    var after = Remove(image, "testdir1/xattr1");
    Assert.Multiple(() => {
      Assert.That(AttributeRecords(after, cnid), Is.Zero);
      Assert.That(Paths(after), Does.Not.Contain("testdir1/xattr1"));
      Assert.That(Read(after, "testdir1/testfile1"), Is.EqualTo("Keramics\n"u8.ToArray()));
      Assert.That(Read(after, "file_hardlink1"), Is.EqualTo("Keramics\n"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilFileWithALargeAttribute_WhenRemoved_ThenTheAttributeForkIsFreed() {
    var image = HfsPlusLinksAndNamesTests.KeramicsVolume(this._tmp);
    var cnid = Cnid(image, "testdir1/large_xattr");
    var freeBefore = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(1024 + 48));
    var after = Remove(image, "testdir1/large_xattr");
    Assert.Multiple(() => {
      Assert.That(AttributeRecords(after, cnid), Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(1024 + 48)), Is.GreaterThan(freeBefore), "its attribute fork's blocks are free");
    });
  }

  [TestCase(HfsPlusCompression.Zlib, TestName = "GivenAZlibCompressedFile_WhenRemoved_ThenItsDecmpfsAttributeAndResourceForkGo")]
  [TestCase(HfsPlusCompression.Lzfse, TestName = "GivenAnLzfseCompressedFile_WhenRemoved_ThenItsDecmpfsAttributeAndResourceForkGo")]
  [Category("HappyPath")]
  public void RemoveCompressed(HfsPlusCompression compression) {
    var w = new HfsPlusWriter { TransparentCompression = compression };
    w.AddFile("keep.txt", HfsPlusDecmpfsMethodsTests.Text(5000, 1));
    w.AddFile("big.txt", HfsPlusDecmpfsMethodsTests.Text(300_000, 2));
    var image = w.Build(BlockSize);
    var cnid = Cnid(image, "big.txt");
    Assert.That(AttributeRecords(image, cnid), Is.EqualTo(1), "precondition: compressed");
    var after = Remove(image, "big.txt");
    Assert.Multiple(() => {
      Assert.That(AttributeRecords(after, cnid), Is.Zero);
      Assert.That(Paths(after), Is.EqualTo(new[] { "keep.txt" }));
      Assert.That(Read(after, "keep.txt"), Is.EqualTo(HfsPlusDecmpfsMethodsTests.Text(5000, 1)));
    });
    HfsPlusRemoveExtentsTests.AssertBitmapConsistent(after);
  }

  [Test, Category("HappyPath")]
  public void GivenAFileContinuedInTheOverflowFile_WhenRemoved_ThenItsOverflowRecordsAndEveryBlockGo() {
    var image = Add(Fragmented(12), "scattered.bin", HfsPlusDecmpfsMethodsTests.Noise(12 * (int)BlockSize, 3));
    var cnid = Cnid(image, "scattered.bin");
    Assert.That(OverflowRecords(image, cnid), Is.EqualTo(1), "precondition: twelve runs, four past the record");
    var freeBefore = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(1024 + 48));
    var after = Remove(image, "scattered.bin");
    Assert.Multiple(() => {
      Assert.That(OverflowRecords(after, cnid), Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(1024 + 48)), Is.EqualTo(freeBefore + 12));
    });
    HfsPlusRemoveExtentsTests.AssertBitmapConsistent(after);
  }

  [Test, Category("HappyPath")]
  public void GivenAFileInASubfolder_WhenRemovedByPath_ThenOnlyItGoes() {
    var image = Base("docs/a.txt", "docs/b.txt", "a.txt");
    var after = Remove(image, "docs/a.txt");
    Assert.That(Paths(after), Is.EquivalentTo(new[] { "docs", "docs/b.txt", "a.txt" }));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAFolder_WhenRemovedAsAFile_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = Base("docs/a.txt");
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.RemoveFile(ms, "docs"));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAMissingFolderOnThePath_WhenRemoved_ThenItIsReportedMissing() {
    var image = Base("docs/a.txt");
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.That(HfsPlusModifier.RemoveFile(ms, "nope/a.txt"), Is.False);
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  // ── Adding ─────────────────────────────────────────────────────────

  [TestCase(HfsPlusCompression.Zlib, 2000, TestName = "GivenZlib_WhenASmallFileIsAdded_ThenItIsStoredInlineAndReadsBack")]
  [TestCase(HfsPlusCompression.Zlib, 300_000, TestName = "GivenZlib_WhenALargeFileIsAdded_ThenItIsStoredInTheResourceForkAndReadsBack")]
  [TestCase(HfsPlusCompression.Lzvn, 300_000, TestName = "GivenLzvn_WhenALargeFileIsAdded_ThenItReadsBack")]
  [TestCase(HfsPlusCompression.Lzfse, 300_000, TestName = "GivenLzfse_WhenALargeFileIsAdded_ThenItReadsBack")]
  [TestCase(HfsPlusCompression.Lzbitmap, 300_000, TestName = "GivenLzbitmap_WhenALargeFileIsAdded_ThenItReadsBack")]
  [TestCase(HfsPlusCompression.InlineUncompressed, 1000, TestName = "GivenMethodOne_WhenASmallFileIsAdded_ThenItReadsBack")]
  [Category("HappyPath")]
  public void AddCompressed(HfsPlusCompression compression, int length) {
    var content = HfsPlusDecmpfsMethodsTests.Text(length, length);
    var after = Add(Base("keep.txt"), "packed.txt", content, compression);
    var cnid = Cnid(after, "packed.txt");
    var entry = new HfsPlusReader(new MemoryStream(after)).Entries.Single(static e => e.FullPath == "packed.txt");
    Assert.Multiple(() => {
      Assert.That(Read(after, "packed.txt"), Is.EqualTo(content));
      Assert.That(entry.Size, Is.EqualTo(content.Length));
      Assert.That(AttributeRecords(after, cnid), Is.EqualTo(1), "the decmpfs attribute");
      Assert.That(Read(after, "keep.txt"), Is.EqualTo("content of keep.txt"u8.ToArray()));
    });
    HfsPlusRemoveExtentsTests.AssertBitmapConsistent(after);
  }

  [Test, Category("BoundaryCase")]
  public void GivenNoise_WhenAddedCompressed_ThenItStaysUncompressedAsTheWriterWouldStoreIt() {
    var noise = HfsPlusDecmpfsMethodsTests.Noise(100_000, 4);
    var after = Add(Base("keep.txt"), "noise.bin", noise, HfsPlusCompression.Zlib);
    Assert.Multiple(() => {
      Assert.That(Read(after, "noise.bin"), Is.EqualTo(noise));
      Assert.That(AttributeRecords(after, Cnid(after, "noise.bin")), Is.Zero);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenExtendedAttributes_WhenAFileIsAdded_ThenTheyAreStoredInlineOrInAForkByTheirSize() {
    var large = HfsPlusDecmpfsMethodsTests.Noise(10_000, 5);
    var after = Add(Base("keep.txt"), "tagged.txt", "tagged"u8.ToArray(), attributes: new Dictionary<string, byte[]> {
      ["com.example.small"] = "small value"u8.ToArray(),
      ["com.example.large"] = large,
    });
    var volume = HfsPlusVolumeEditor.Open(after.ToArray());
    var cnid = Cnid(after, "tagged.txt");
    var records = volume.Attributes!.Records.Where(r => BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4)) == cnid).ToList();
    var types = records.Select(static r => BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(HfsPlusBTreeFile.DataOffset(r)))).ToList();
    Assert.Multiple(() => {
      Assert.That(types, Is.EquivalentTo(new uint[] { 0x10, 0x20 }), "one inline, one fork");
      Assert.That(Read(after, "tagged.txt"), Is.EqualTo("tagged"u8.ToArray()));
    });
    HfsPlusRemoveExtentsTests.AssertBitmapConsistent(after);
  }

  [Test, Category("HappyPath")]
  public void GivenFreeSpaceInTwelveRuns_WhenAFileNeedsThemAll_ThenTheRunsPastTheEighthGoToTheOverflowFile() {
    var content = HfsPlusDecmpfsMethodsTests.Noise(12 * (int)BlockSize - 100, 6);
    var after = Add(Fragmented(12), "scattered.bin", content);
    Assert.Multiple(() => {
      Assert.That(Read(after, "scattered.bin"), Is.EqualTo(content));
      Assert.That(OverflowRecords(after, Cnid(after, "scattered.bin")), Is.EqualTo(1));
    });
    HfsPlusRemoveExtentsTests.AssertBitmapConsistent(after);
  }

  [Test, Category("BoundaryCase")]
  public void GivenFreeSpaceInExactlyEightRuns_WhenAFileNeedsThemAll_ThenNoOverflowRecordIsWritten() {
    var content = HfsPlusDecmpfsMethodsTests.Noise(8 * (int)BlockSize, 7);
    var after = Add(Fragmented(8), "eight.bin", content);
    Assert.Multiple(() => {
      Assert.That(Read(after, "eight.bin"), Is.EqualTo(content));
      Assert.That(OverflowRecords(after, Cnid(after, "eight.bin")), Is.Zero);
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenTooLittleFreeSpace_WhenAFileIsAdded_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = Fragmented(4);
    using var ms = new MemoryStream();
    ms.Write(image);
    // Four holes and the spare run: one block short.
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.AddFile(ms, "too-big.bin", new byte[(4 + Spare + 1) * BlockSize]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("HappyPath")]
  public void GivenAPathIntoNewFolders_WhenAdded_ThenTheFoldersAreCreatedAndCounted() {
    var image = Base("keep.txt");
    var after = Add(image, "a/b/c/deep.txt", "deep"u8.ToArray());
    var folderCount = BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(1024 + 36));
    var fileCount = BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(1024 + 32));
    Assert.Multiple(() => {
      Assert.That(Paths(after), Is.EquivalentTo(new[] { "keep.txt", "a", "a/b", "a/b/c", "a/b/c/deep.txt" }));
      Assert.That(Read(after, "a/b/c/deep.txt"), Is.EqualTo("deep"u8.ToArray()));
      Assert.That(folderCount, Is.EqualTo(3u), "folderCount");
      Assert.That(fileCount, Is.EqualTo(2u), "fileCount");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenASingleLeafCatalog_WhenSixHundredFilesAreAdded_ThenItSplitsIntoAnIndexedTreeAndEveryFileReads() {
    var image = Base("keep.txt");
    using var ms = new MemoryStream();
    ms.Write(image);
    for (var i = 0; i < 600; i++) HfsPlusModifier.AddFile(ms, $"dir{i % 3}/file{i:D4}.txt", Encoding.ASCII.GetBytes($"#{i}"));
    var after = ms.ToArray();
    var volume = HfsPlusVolumeEditor.Open(after.ToArray());
    var treeDepth = BinaryPrimitives.ReadUInt16BigEndian(after.AsSpan((int)(BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(1024 + 288)) * BlockSize) + 14));
    var reader = new HfsPlusReader(new MemoryStream(after));
    Assert.Multiple(() => {
      Assert.That(treeDepth, Is.GreaterThan(1), "treeDepth");
      Assert.That(volume.Catalog.Records, Has.Count.EqualTo(2 * (1 + 1 + 3 + 600)));
      Assert.That(reader.Entries.Count(static e => !e.IsDirectory), Is.EqualTo(601));
      for (var i = 0; i < 600; i += 41)
        Assert.That(reader.Extract(reader.Entries.Single(e => e.FullPath == $"dir{i % 3}/file{i:D4}.txt")), Is.EqualTo(Encoding.ASCII.GetBytes($"#{i}")));
    });
    HfsPlusRemoveExtentsTests.AssertBitmapConsistent(after);
  }

  [Test, Category("BoundaryCase")]
  public void GivenACatalogThatCannotGrowInPlace_WhenItNeedsMoreNodes_ThenItGrowsIntoASecondExtentTheReaderFollows() {
    // A one-block catalog fenced in by file data: its growth has to go elsewhere.
    var w = new HfsPlusWriter();
    w.AddFile("first.txt", "first"u8.ToArray());
    var image = w.Build(BlockSize);
    using var ms = new MemoryStream();
    ms.Write(image);
    for (var i = 0; i < 200; i++) HfsPlusModifier.AddFile(ms, $"many-{i:D4}-with-a-longer-name.txt", Encoding.ASCII.GetBytes($"{i}"));
    var after = ms.ToArray();
    var extents = Enumerable.Range(0, 8).Count(k => BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(1024 + 272 + 20 + 8 * k)) != 0);
    var reader = new HfsPlusReader(new MemoryStream(after));
    Assert.Multiple(() => {
      Assert.That(extents, Is.GreaterThan(1), "the catalog continues in another extent");
      Assert.That(reader.Entries, Has.Count.EqualTo(201));
      Assert.That(reader.Extract(reader.Entries.Single(static e => e.FullPath == "many-0199-with-a-longer-name.txt")), Is.EqualTo("199"u8.ToArray()));
    });
    HfsPlusRemoveExtentsTests.AssertBitmapConsistent(after);
  }

  [Test, Category("HappyPath")]
  public void GivenACaseFoldingVolume_WhenANameDifferingOnlyInCaseIsAdded_ThenItReplacesTheFile() {
    var after = Add(Base("readme.txt"), "README.TXT", "new"u8.ToArray());
    Assert.Multiple(() => {
      Assert.That(Paths(after), Is.EqualTo(new[] { "README.TXT" }));
      Assert.That(Read(after, "README.TXT"), Is.EqualTo("new"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAHfsxVolume_WhenANameDifferingOnlyInCaseIsAdded_ThenBothFilesExist() {
    var w = new HfsPlusWriter(caseSensitive: true);
    w.AddFile("readme.txt", "old"u8.ToArray());
    var after = Add(w.Build(BlockSize), "README.TXT", "new"u8.ToArray());
    Assert.Multiple(() => {
      Assert.That(Paths(after), Is.EquivalentTo(new[] { "readme.txt", "README.TXT" }));
      Assert.That(Read(after, "readme.txt"), Is.EqualTo("old"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenACaseFoldingVolume_WhenNamesWhoseByteOrderDiffersFromTheFoldedOrderAreAdded_ThenTheCatalogStaysInKeyOrder() {
    var image = Base("alpha.txt");
    foreach (var name in new[] { "Zeta.txt", "beta.txt", "BETB.txt", "_under.txt", "Ärger.txt" }) image = Add(image, name, [1]);
    // Opening re-reads every key and refuses a catalog out of order.
    Assert.DoesNotThrow(() => _ = HfsPlusVolumeEditor.Open(image.ToArray()).Catalog.Records);
    Assert.That(Paths(image), Has.Count.EqualTo(6));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAPathThatClimbsOut_WhenAdded_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = Base("keep.txt");
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<ArgumentException>(() => HfsPlusModifier.AddFile(ms, "docs/../../escape.txt", [1]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("BoundaryCase")]
  public void GivenAPathWithDotComponents_WhenAdded_ThenTheyNameNoFolder() {
    var after = Add(Base("keep.txt"), "./docs/./a.txt", [1]);
    Assert.That(Paths(after), Is.EquivalentTo(new[] { "keep.txt", "docs", "docs/a.txt" }));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAFileWhereAFolderIsNeeded_WhenAddedBelowIt_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = Base("plain");
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.AddFile(ms, "plain/child.txt", [1]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenTheMetadataFolder_WhenAFileIsAddedInIt_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = Base("keep.txt");
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.AddFile(ms, "␀␀␀␀HFS+ Private Data/iNode99", [1]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("HappyPath")]
  public void GivenTheDescriptor_WhenFilesInFoldersAreAddedAndRemoved_ThenThePathsAreKept() {
    var image = Base("keep.txt");
    using var ms = new MemoryStream();
    ms.Write(image);
    var descriptor = new HfsPlusFormatDescriptor();
    descriptor.Add(ms, [new Compression.Registry.ArchiveInputInfo("", "docs/guide.txt", false) { InMemoryContent = "guide"u8.ToArray() }]);
    ms.Position = 0;
    Assert.That(new HfsPlusReader(ms, leaveOpen: true).Entries.Select(static e => e.FullPath), Does.Contain("docs/guide.txt"));
    descriptor.Remove(ms, ["docs/guide.txt"]);
    ms.Position = 0;
    Assert.That(new HfsPlusReader(ms, leaveOpen: true).Entries.Select(static e => e.FullPath), Is.EquivalentTo(new[] { "keep.txt", "docs" }));
  }

  // ── Oracles ────────────────────────────────────────────────────────

  /// <summary>The volume after each kind of edit, for the external tools to judge.</summary>
  private byte[] Edited(string kind) => kind switch {
    "remove-xattrs" => Remove(Remove(HfsPlusLinksAndNamesTests.KeramicsVolume(this._tmp), "testdir1/xattr1"), "testdir1/large_xattr"),
    "remove-compressed" => Remove(CompressedVolume(), "big.txt"),
    "remove-overflow" => Remove(Add(Fragmented(12), "scattered.bin", HfsPlusDecmpfsMethodsTests.Noise(12 * (int)BlockSize, 3)), "scattered.bin"),
    "add-compressed" => Add(Add(Base("keep.txt"), "inline.txt", HfsPlusDecmpfsMethodsTests.Text(2000, 8), HfsPlusCompression.Zlib),
      "chunked.txt", HfsPlusDecmpfsMethodsTests.Text(300_000, 9), HfsPlusCompression.Lzfse),
    "add-xattrs" => Add(Base("keep.txt"), "tagged.txt", "tagged"u8.ToArray(), attributes: new Dictionary<string, byte[]> {
      ["com.example.small"] = "small value"u8.ToArray(), ["com.example.large"] = HfsPlusDecmpfsMethodsTests.Noise(10_000, 5),
    }),
    "add-overflow" => Add(Fragmented(12), "scattered.bin", HfsPlusDecmpfsMethodsTests.Noise(12 * (int)BlockSize - 100, 6)),
    "add-many" => ManyFiles(),
    "add-hfsx" => HfsxEdits(),
    "add-to-hdiutil" => Add(Add(HfsPlusLinksAndNamesTests.KeramicsVolume(this._tmp), "testdir1/added.txt", "added"u8.ToArray()),
      "new folder/zlib.txt", HfsPlusDecmpfsMethodsTests.Text(100_000, 10), HfsPlusCompression.Zlib),
    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
  };

  private static byte[] CompressedVolume() {
    var w = new HfsPlusWriter { TransparentCompression = HfsPlusCompression.Zlib };
    w.AddFile("keep.txt", HfsPlusDecmpfsMethodsTests.Text(5000, 1));
    w.AddFile("big.txt", HfsPlusDecmpfsMethodsTests.Text(300_000, 2));
    return w.Build(BlockSize);
  }

  private static byte[] ManyFiles() {
    using var ms = new MemoryStream();
    ms.Write(Base("keep.txt"));
    for (var i = 0; i < 600; i++) HfsPlusModifier.AddFile(ms, $"dir{i % 3}/file{i:D4}.txt", Encoding.ASCII.GetBytes($"#{i}"));
    for (var i = 0; i < 600; i += 7) HfsPlusModifier.RemoveFile(ms, $"dir{i % 3}/file{i:D4}.txt");
    return ms.ToArray();
  }

  private static byte[] HfsxEdits() {
    var w = new HfsPlusWriter(caseSensitive: true);
    w.AddFile("readme.txt", "old"u8.ToArray());
    var image = w.Build(BlockSize);
    foreach (var name in new[] { "README.TXT", "Zeta", "alpha", "Alpha", "sub/Inner" }) image = Add(image, name, [1]);
    return image;
  }

  private static readonly string[] Kinds = [
    "remove-xattrs", "remove-compressed", "remove-overflow", "add-compressed", "add-xattrs", "add-overflow", "add-many", "add-hfsx", "add-to-hdiutil",
  ];

  [TestCaseSource(nameof(Kinds)), Category("ExternalFsInterop")]
  public void GivenAnEditedVolume_WhenCheckedByFsckHfsplus_ThenItIsClean(string kind)
    => HfsPlusOracles.AssertFsckClean(this.Edited(kind), this._tmp, kind);

  [TestCaseSource(nameof(Kinds)), Category("ArchiveExternal")]
  public void GivenAnEditedVolume_WhenLibfshfsReadsIt_ThenEveryFileIsOurs(string kind) {
    var image = this.Edited(kind);
    var oracle = HfsPlusOracles.Libfshfs(image, this._tmp)
      // The metadata directories libfshfs shows are hidden here: their content is reached through links.
      .Where(static e => !e.Path.StartsWith("␀␀␀␀HFS+ Private Data", StringComparison.Ordinal)
                         && !e.Path.StartsWith(".HFS+ Private Directory Data\r", StringComparison.Ordinal))
      .ToList();
    var reader = new HfsPlusReader(new MemoryStream(image));
    var ours = reader.Entries.ToDictionary(static e => e.FullPath, StringComparer.Ordinal);
    Assert.That(ours.Keys, Is.EquivalentTo(oracle.Select(static e => e.Path)), "listing");
    Assert.Multiple(() => {
      foreach (var expected in oracle.Where(static e => e.Sha256 is not null))
        Assert.That(HfsPlusOracles.Sha256(reader.Extract(ours[expected.Path])), Is.EqualTo(expected.Sha256), expected.Path);
      if (kind == "add-xattrs") {
        var xattrs = oracle.Single(static e => e.Path == "tagged.txt").Xattrs;
        Assert.That(xattrs["com.example.small"], Is.EqualTo(HfsPlusOracles.Sha256("small value"u8.ToArray())));
        Assert.That(xattrs["com.example.large"], Is.EqualTo(HfsPlusOracles.Sha256(HfsPlusDecmpfsMethodsTests.Noise(10_000, 5))));
      }
    });
  }
}
