using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;
using Compression.Lib;
using Compression.Registry;
using Compression.Tests.AppleSparse;
using FileFormat.AppleSparse;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Hard links, the ':'/'/' name swap and the volume's own bookkeeping, judged against the HFS+
/// volume Apple's hdiutil and macOS wrote into keramics' <c>hfsplus.sparsebundle</c> (expected
/// values as libfshfs 20260922 reads them; the live comparison is
/// <see cref="HfsPlusLibfshfsOracleTests"/>) and against volumes our writer builds and the tests
/// patch into the shapes TN1150 describes.
/// </summary>
[TestFixture]
public sealed class HfsPlusLinksAndNamesTests {

  private const uint BlockSize = 4096;
  private const string MetadataDirectory = "␀␀␀␀HFS+ Private Data";
  private static readonly byte[] KeramicsTestFile = "Keramics\n"u8.ToArray();

  private string _tmp = null!;

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_hfslink_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  // ── The hdiutil volume ─────────────────────────────────────────────

  /// <summary>The Apple_HFS partition of the keramics bundle's medium.</summary>
  internal static byte[] KeramicsVolume(string tmp) {
    var disk = new SparsebundleReader(SparsebundleVectors.Materialize(SparsebundleVectors.HfsPlus, tmp)).ExtractDisk();
    var partition = PartitionTableDetector.Detect(disk).Partitions.Single();
    return disk.AsSpan((int)partition.StartOffset, (int)partition.Size).ToArray();
  }

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilVolume_WhenListed_ThenBothNamesOfTheHardLinkCarryItsData() {
    var reader = new HfsPlusReader(new MemoryStream(KeramicsVolume(this._tmp)));
    var link = reader.Entries.Single(e => e.FullPath == "file_hardlink1");
    var original = reader.Entries.Single(e => e.FullPath == "testdir1/testfile1");
    Assert.Multiple(() => {
      Assert.That(link.Size, Is.EqualTo(KeramicsTestFile.Length));
      Assert.That(original.Size, Is.EqualTo(KeramicsTestFile.Length));
      Assert.That(reader.Extract(link), Is.EqualTo(KeramicsTestFile));
      Assert.That(reader.Extract(original), Is.EqualTo(KeramicsTestFile));
      // libfshfs reports both names as CNID 20, the indirect node file iNode20.
      Assert.That(link.Cnid, Is.EqualTo(20u));
      Assert.That(original.Cnid, Is.EqualTo(20u));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilVolume_WhenListed_ThenTheSlashNameShowsAsAColonAndIsNoFolder() {
    var entries = new HfsPlusReader(new MemoryStream(KeramicsVolume(this._tmp))).Entries;
    Assert.Multiple(() => {
      Assert.That(entries.Select(e => e.FullPath), Does.Contain("forward:slash"));
      Assert.That(entries.Any(e => e.FullPath.StartsWith("forward/", StringComparison.Ordinal) || e.FullPath == "forward"), Is.False);
      // macOS stored the link target as typed in Terminal; it is data, not a catalog name.
      Assert.That(entries.Single(e => e.FullPath == "file_symboliclink2").LinkTarget, Is.EqualTo("/Volumes/hfsplus_test/forward:slash"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilVolume_WhenListed_ThenTheMetadataDirectoriesAreNotShown() {
    var paths = new HfsPlusReader(new MemoryStream(KeramicsVolume(this._tmp))).Entries.Select(e => e.FullPath).ToList();
    Assert.Multiple(() => {
      Assert.That(paths.Any(p => p.StartsWith(MetadataDirectory, StringComparison.Ordinal)), Is.False);
      Assert.That(paths.Any(p => p.Contains("HFS+ Private", StringComparison.Ordinal)), Is.False);
      Assert.That(paths.Any(p => p.Contains("iNode", StringComparison.Ordinal)), Is.False);
      Assert.That(paths.Any(p => p.Any(char.IsControl)), Is.False, "no name with a control character is left");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilVolume_WhenListed_ThenEveryEntryLibfshfsShowsIsListedWithItsSize() {
    // Captured from libfshfs 20260922 (pyfshfs) over the same partition, metadata directories
    // left out. Symbolic links report their target's length, as both readers do.
    (string Path, long Size, bool Directory)[] expected = [
      (".fseventsd", 0, true), (".fseventsd/000000002475c378", 321, false), (".fseventsd/000000002475c379", 72, false),
      (".fseventsd/fseventsd-uuid", 36, false), ("case_folding_\u00B5", 0, false), ("directory_symboliclink1", 30, false),
      ("emptyfile", 0, false), ("file_hardlink1", 9, false), ("file_symboliclink1", 40, false),
      ("file_symboliclink2", 35, false), ("forward:slash", 0, false), ("nfc_te\u0301stfile\u0300", 0, false),
      ("nfd_te\u0301stfile\u0300", 0, false), ("nfd_\u00BE", 0, false), ("nfkd_3\u20444", 0, false), ("testdir1", 0, true),
      ("testdir1/large_xattr", 0, false), ("testdir1/pipe1", 0, false), ("testdir1/resourcefork1", 0, false),
      ("testdir1/testfile1", 9, false), ("testdir1/xattr1", 0, false), ("testdir1/xattr2", 0, true),
    ];
    var actual = new HfsPlusReader(new MemoryStream(KeramicsVolume(this._tmp))).Entries
      .Select(e => (e.FullPath, e.Size, e.IsDirectory)).OrderBy(e => e.FullPath, StringComparer.Ordinal).ToArray();
    Assert.That(actual, Is.EqualTo(expected.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray()));
  }

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilBundle_WhenTheWholeVolumeIsExtracted_ThenItIsTheFilesNotARawPartition() {
    var root = SparsebundleVectors.Materialize(SparsebundleVectors.HfsPlus, this._tmp);
    var output = Path.Combine(this._tmp, "out");
    var descriptor = new SparsebundleFormatDescriptor();
    using (var plist = File.OpenRead(Path.Combine(root, "Info.plist")))
      descriptor.Extract(plist, output, null, null);

    var volume = Directory.GetDirectories(output).Single();
    var windows = OperatingSystem.IsWindows();
    Assert.Multiple(() => {
      Assert.That(Directory.GetFiles(output, "*.raw", SearchOption.AllDirectories), Is.Empty, "fell back to dumping the partition");
      Assert.That(File.ReadAllBytes(Path.Combine(volume, "file_hardlink1")), Is.EqualTo(KeramicsTestFile));
      Assert.That(File.ReadAllBytes(Path.Combine(volume, "testdir1", "testfile1")), Is.EqualTo(KeramicsTestFile));
      Assert.That(File.Exists(Path.Combine(volume, windows ? "forward_slash" : "forward:slash")), Is.True);
      Assert.That(Directory.GetFileSystemEntries(volume).Any(p => Path.GetFileName(p).Contains("Private", StringComparison.Ordinal)), Is.False);
    });
  }

  // ── Hard links on built volumes ────────────────────────────────────

  /// <summary>
  /// Turns the root-level or nested file record named <paramref name="name"/> into a TN1150 hard
  /// link to "iNode<paramref name="linkReference"/>": type 'hlnk', creator 'hfs+', the reference
  /// in permissions.special and kHFSHasLinkChainMask set.
  /// </summary>
  private static void MakeHardLink(byte[] image, string name, uint linkReference) {
    var dataOffset = FileRecordOffset(image, name);
    var flags = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(dataOffset + 2));
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(dataOffset + 2), (ushort)(flags | 0x20));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(dataOffset + 44), linkReference);
    "hlnkhfs+"u8.CopyTo(image.AsSpan(dataOffset + 48));
  }

  /// <summary>Where the file record whose catalog key names <paramref name="name"/> starts.</summary>
  private static int FileRecordOffset(byte[] image, string name) {
    var key = new byte[2 + name.Length * 2];
    BinaryPrimitives.WriteUInt16BigEndian(key, (ushort)name.Length);
    Encoding.BigEndianUnicode.GetBytes(name).CopyTo(key, 2);
    for (var from = 0; ;) {
      var at = image.AsSpan(from).IndexOf(key);
      Assert.That(at, Is.GreaterThanOrEqualTo(0), $"no catalog key for {name}");
      at += from;
      var keyStart = at - 6;
      if (keyStart >= 0 && BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(keyStart)) == 6 + name.Length * 2) {
        var dataOffset = keyStart + 2 + 6 + name.Length * 2;
        if ((dataOffset & 1) != 0) dataOffset++;
        if (BinaryPrimitives.ReadInt16BigEndian(image.AsSpan(dataOffset)) == 2) return dataOffset;
      }
      from = at + 1;
    }
  }

  /// <summary>A volume whose iNode77 is shared by "link-one" in the root and "dir/link-two".</summary>
  private static byte[] HardLinkedVolume(byte[] payload, string secondPath = "dir/link-two") {
    var w = new HfsPlusWriter();
    w.AddFile(MetadataDirectory + "/iNode77", payload);
    w.AddFile("link-one", []);
    w.AddFile(secondPath, []);
    w.AddFile("plain.txt", "plain"u8.ToArray());
    var image = w.Build(BlockSize);
    MakeHardLink(image, "link-one", 77);
    MakeHardLink(image, secondPath[(secondPath.LastIndexOf('/') + 1)..], 77);
    return image;
  }

  private static byte[] Pattern(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  [Test, Category("HappyPath")]
  public void GivenTwoNamesForOneIndirectNode_WhenExtracted_ThenBothAreItsBytes() {
    var payload = Pattern(5000, 1);
    var reader = new HfsPlusReader(new MemoryStream(HardLinkedVolume(payload)));
    var one = reader.Entries.Single(e => e.FullPath == "link-one");
    var two = reader.Entries.Single(e => e.FullPath == "dir/link-two");
    Assert.Multiple(() => {
      Assert.That(one.Size, Is.EqualTo(payload.Length));
      Assert.That(two.Size, Is.EqualTo(payload.Length));
      Assert.That(reader.Extract(one), Is.EqualTo(payload));
      Assert.That(reader.Extract(two), Is.EqualTo(payload));
      Assert.That(one.Cnid, Is.EqualTo(two.Cnid), "one inode behind both names");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAHardLinkedVolume_WhenListedThroughTheDescriptor_ThenTheIndirectNodeIsNotAnEntry() {
    var payload = Pattern(100, 2);
    var entries = new HfsPlusFormatDescriptor().List(new MemoryStream(HardLinkedVolume(payload)), null);
    Assert.Multiple(() => {
      Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "dir", "dir/link-two", "link-one", "plain.txt" }));
      Assert.That(entries.Where(e => !e.IsDirectory && e.Name != "plain.txt").Select(e => e.OriginalSize), Is.All.EqualTo(100));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAHardLinkedVolume_WhenExtractedThroughTheDescriptor_ThenBothFilesAreWrittenAndNoMetadataFolder() {
    var payload = Pattern(3000, 3);
    var output = Path.Combine(this._tmp, "x");
    new HfsPlusFormatDescriptor().Extract(new MemoryStream(HardLinkedVolume(payload)), output, null, null);
    Assert.Multiple(() => {
      Assert.That(File.ReadAllBytes(Path.Combine(output, "link-one")), Is.EqualTo(payload));
      Assert.That(File.ReadAllBytes(Path.Combine(output, "dir", "link-two")), Is.EqualTo(payload));
      Assert.That(Directory.GetDirectories(output).Select(Path.GetFileName), Is.EqualTo(new[] { "dir" }));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAHardLink_WhenOpenedByName_ThenTheStreamIsTheSharedData() {
    var payload = Pattern(777, 4);
    var bytes = new HfsPlusFormatDescriptor().ExtractEntryToMemory(new MemoryStream(HardLinkedVolume(payload)), "dir/link-two", null);
    Assert.That(bytes, Is.EqualTo(payload));
  }

  [Test, Category("BoundaryCase")]
  public void GivenAnEmptyIndirectNode_WhenItsLinksAreExtracted_ThenBothAreEmpty() {
    var reader = new HfsPlusReader(new MemoryStream(HardLinkedVolume([])));
    var links = reader.Entries.Where(e => e.Name.StartsWith("link-", StringComparison.Ordinal)).ToList();
    Assert.Multiple(() => {
      Assert.That(links, Has.Count.EqualTo(2));
      Assert.That(links.Select(e => e.Size), Is.All.Zero);
      Assert.That(links.Select(reader.Extract), Is.All.Empty);
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenAnIndirectNodeInTwoExtents_WhenItsLinksAreExtracted_ThenBothExtentsAreRead() {
    var payload = new byte[2 * BlockSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i / BlockSize == 0 ? 0xA1 : 0xB2);
    var image = HardLinkedVolume(payload);
    var node = new HfsPlusReader(new MemoryStream(image)).AllFiles.Single(e => e.Name == "iNode77");
    var start = node.FirstBlock;
    Assert.That(node.BlockCount, Is.EqualTo(2u), "precondition: one run of two blocks");

    // Swap the blocks on disk and describe them as (start+1, 1), (start, 1).
    var at = FileRecordOffset(image, "iNode77") + 88 + 16;   // dataFork.extents[0]
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(at)), Is.EqualTo(start));
    var first = image.AsSpan((int)(start * BlockSize), (int)BlockSize).ToArray();
    image.AsSpan((int)((start + 1) * BlockSize), (int)BlockSize).CopyTo(image.AsSpan((int)(start * BlockSize)));
    first.CopyTo(image, (int)((start + 1) * BlockSize));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at), start + 1);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at + 4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at + 8), start);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at + 12), 1);

    var reader = new HfsPlusReader(new MemoryStream(image));
    Assert.That(reader.Entries.Where(e => e.Name.StartsWith("link-", StringComparison.Ordinal)).Select(reader.Extract),
      Is.All.EqualTo(payload));
  }

  [Test, Category("BoundaryCase")]
  public void GivenAHardLinkTwelveFoldersDeep_WhenExtracted_ThenItIsTheSharedData() {
    var deep = string.Join('/', Enumerable.Range(1, 12).Select(i => $"level{i}")) + "/link-deep";
    var payload = Pattern(64, 5);
    var reader = new HfsPlusReader(new MemoryStream(HardLinkedVolume(payload, deep)));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.FullPath == deep)), Is.EqualTo(payload));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAHardLinkWhoseIndirectNodeIsMissing_WhenListed_ThenItIsAnEmptyFileAndNothingThrows() {
    var image = HardLinkedVolume(Pattern(10, 6));
    MakeHardLink(image, "link-one", 78);   // no iNode78
    var reader = new HfsPlusReader(new MemoryStream(image));
    var broken = reader.Entries.Single(e => e.FullPath == "link-one");
    Assert.Multiple(() => {
      Assert.That(broken.Size, Is.Zero);
      Assert.That(reader.Extract(broken), Is.Empty);
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FullPath == "dir/link-two")), Has.Length.EqualTo(10));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAFileTypedLikeAHardLinkWithTheWrongCreator_WhenListed_ThenItIsItsOwnFile() {
    var image = HardLinkedVolume(Pattern(10, 7));
    var dataOffset = FileRecordOffset(image, "plain.txt");
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(dataOffset + 44), 77);
    "hlnkMACS"u8.CopyTo(image.AsSpan(dataOffset + 48));
    var reader = new HfsPlusReader(new MemoryStream(image));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.FullPath == "plain.txt")), Is.EqualTo("plain"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void GivenAHardLinkedVolume_WhenDefragmented_ThenBothNamesStillReadTheSharedData() {
    var payload = Pattern(9000, 8);
    using var ms = new MemoryStream();
    ms.Write(HardLinkedVolume(payload));
    new HfsPlusFormatDescriptor().Defragment(ms);
    ms.Position = 0;
    var reader = new HfsPlusReader(ms, leaveOpen: true);
    Assert.That(reader.Entries.Where(e => e.Name.StartsWith("link-", StringComparison.Ordinal)).Select(reader.Extract),
      Is.All.EqualTo(payload));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAHardLinkInTheRoot_WhenRemovedInPlace_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = HardLinkedVolume(Pattern(10, 9));
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => new HfsPlusFormatDescriptor().Remove(ms, ["link-one"]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAHardLinkInTheRoot_WhenReplacedInPlace_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = HardLinkedVolume(Pattern(10, 10));
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.AddFile(ms, "link-one", [1, 2, 3]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  // ── Names ──────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenAPosixNameWithAColon_WhenWritten_ThenTheCatalogStoresASlashAndItReadsBackWithTheColon() {
    var w = new HfsPlusWriter();
    w.AddFile("forward:slash", [42]);
    w.AddFile("a:b/inner:file", [43]);
    var image = w.Build(BlockSize);
    var reader = new HfsPlusReader(new MemoryStream(image));
    Assert.Multiple(() => {
      Assert.That(image.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes("forward/slash")), Is.GreaterThan(0));
      Assert.That(image.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes("forward:slash")), Is.LessThan(0));
      Assert.That(reader.Entries.Select(e => e.FullPath), Is.EquivalentTo(new[] { "forward:slash", "a:b", "a:b/inner:file" }));
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FullPath == "a:b/inner:file")), Is.EqualTo(new byte[] { 43 }));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAColonName_WhenRemovedInPlace_ThenTheSlashRecordGoes() {
    var w = new HfsPlusWriter();
    w.AddFile("keep.txt", [1]);
    w.AddFile("go:away", [2]);
    using var ms = new MemoryStream();
    ms.Write(w.Build(BlockSize));
    new HfsPlusFormatDescriptor().Remove(ms, ["go:away"]);
    ms.Position = 0;
    Assert.That(new HfsPlusReader(ms, leaveOpen: true).Entries.Select(e => e.FullPath), Is.EqualTo(new[] { "keep.txt" }));
  }

  [Test, Category("HappyPath")]
  public void GivenAColonName_WhenAddedInPlace_ThenItReadsBackUnderTheSameName() {
    var w = new HfsPlusWriter();
    w.AddFile("keep.txt", [1]);
    using var ms = new MemoryStream();
    ms.Write(w.Build(BlockSize));
    HfsPlusModifier.AddFile(ms, "new:name", [9, 9]);
    ms.Position = 0;
    var reader = new HfsPlusReader(ms, leaveOpen: true);
    Assert.That(reader.Extract(reader.Entries.Single(e => e.FullPath == "new:name")), Is.EqualTo(new byte[] { 9, 9 }));
  }

  [Test, Category("HappyPath")]
  public void GivenAColonName_WhenMapped_ThenTheExtentCarriesTheListedName() {
    var w = new HfsPlusWriter();
    w.AddFile("a:b", [1, 2, 3]);
    var extents = HfsPlusExtentMap.Enumerate(new MemoryStream(w.Build(BlockSize)));
    Assert.That(extents.Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.FileName), Does.Contain("a:b"));
  }

  // ── Journal and directory-link bookkeeping ─────────────────────────

  /// <summary>
  /// A volume holding root files named like the journal's; when <paramref name="journaled"/>,
  /// the volume header and journal info block point at them as TN1150 lays out.
  /// </summary>
  private static byte[] JournalVolume(bool journaled) {
    var w = new HfsPlusWriter();
    w.AddFile(".journal_info_block", new byte[BlockSize]);
    w.AddFile(".journal", new byte[2 * BlockSize]);
    w.AddFile("data.txt", [7]);
    var image = w.Build(BlockSize);
    if (!journaled) return image;
    var files = new HfsPlusReader(new MemoryStream(image)).AllFiles;
    var info = files.Single(e => e.Name == ".journal_info_block").FirstBlock;
    var journal = files.Single(e => e.Name == ".journal").FirstBlock;
    var vh = image.AsSpan(1024);
    BinaryPrimitives.WriteUInt32BigEndian(vh[4..], BinaryPrimitives.ReadUInt32BigEndian(vh[4..]) | (1u << 13));
    BinaryPrimitives.WriteUInt32BigEndian(vh[12..], info);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan((int)(info * BlockSize) + 36), (ulong)journal * BlockSize);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan((int)(info * BlockSize) + 44), 2 * BlockSize);
    return image;
  }

  [Test, Category("HappyPath")]
  public void GivenAJournaledVolume_WhenListed_ThenTheJournalFilesAreNotShown() {
    var reader = new HfsPlusReader(new MemoryStream(JournalVolume(journaled: true)));
    Assert.Multiple(() => {
      Assert.That(reader.Entries.Select(e => e.FullPath), Is.EqualTo(new[] { "data.txt" }));
      Assert.That(reader.AllFiles, Has.Count.EqualTo(3), "the content check still sees them");
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenAVolumeWithoutAJournal_WhenFilesAreNamedLikeTheJournal_ThenTheyAreShown() {
    var reader = new HfsPlusReader(new MemoryStream(JournalVolume(journaled: false)));
    Assert.That(reader.Entries.Select(e => e.FullPath), Is.EquivalentTo(new[] { ".journal", ".journal_info_block", "data.txt" }));
  }

  [Test, Category("BoundaryCase")]
  public void GivenADirectoryMetadataFolderThatHoldsFiles_WhenListed_ThenItStaysVisible() {
    // Directory hard links are not resolved; hiding their targets would hide the files.
    var w = new HfsPlusWriter();
    w.AddFile(".HFS+ Private Directory Data\r/dir_30/kept.txt", [5]);
    var reader = new HfsPlusReader(new MemoryStream(w.Build(BlockSize)));
    Assert.That(reader.Entries.Select(e => e.FullPath), Does.Contain(".HFS+ Private Directory Data\r/dir_30/kept.txt"));
  }

  [Test, Category("BoundaryCase")]
  public void GivenAFileNamedLikeTheMetadataDirectoryBelowTheRoot_WhenListed_ThenItIsShown() {
    var w = new HfsPlusWriter();
    w.AddFile("sub/" + MetadataDirectory + "/iNode5", [5]);
    var reader = new HfsPlusReader(new MemoryStream(w.Build(BlockSize)));
    Assert.That(reader.Entries.Select(e => e.FullPath), Does.Contain("sub/" + MetadataDirectory + "/iNode5"));
  }
}
