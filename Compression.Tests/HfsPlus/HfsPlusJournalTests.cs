using System.Buffers.Binary;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Journaled volumes. A journal whose start equals its end holds nothing, and the in-place editor
/// edits such a volume directly; one holding transactions is replayed into what the reader shows
/// (the image untouched) and refused by every in-place writer, since a mount's replay would write
/// those blocks over the edit. The journals here are laid out per Apple TN1150 "Journal"; volumes
/// <c>mkfs.hfsplus -J</c> formats, and <c>fsck.hfsplus</c>'s verdict on edited ones, are the
/// external checks at the end.
/// </summary>
[TestFixture]
public sealed class HfsPlusJournalTests {

  private const uint BlockSize = 4096;
  private const int SectorSize = 512;
  private const int JournalBlocks = 64;          // 256 KiB journal
  private const int BlockListHeaderSize = 4096;
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_hfsjnl_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  // ── Building journals ──────────────────────────────────────────────

  /// <summary>
  /// A journaled volume holding "old.txt": the root files ".journal_info_block" and ".journal"
  /// that TN1150 places there, the volume header's journaled bit and journalInfoBlock, and an
  /// empty journal (start = end = just after the header).
  /// </summary>
  internal static byte[] JournaledVolume(out long journalOffset, bool littleEndian = false) {
    var w = new HfsPlusWriter();
    w.AddFile(".journal_info_block", new byte[BlockSize]);
    w.AddFile(".journal", new byte[JournalBlocks * BlockSize]);
    w.AddFile("old.txt", "old"u8.ToArray());
    var image = w.Build(BlockSize);
    var files = new HfsPlusReader(new MemoryStream(image)).AllFiles;
    var info = files.Single(static e => e.Name == ".journal_info_block").FirstBlock;
    journalOffset = (long)files.Single(static e => e.Name == ".journal").FirstBlock * BlockSize;
    var vh = image.AsSpan(1024);
    BinaryPrimitives.WriteUInt32BigEndian(vh[4..], BinaryPrimitives.ReadUInt32BigEndian(vh[4..]) | (1u << 13));
    BinaryPrimitives.WriteUInt32BigEndian(vh[12..], info);
    var infoBlock = image.AsSpan((int)(info * BlockSize));
    BinaryPrimitives.WriteUInt32BigEndian(infoBlock, 1);   // kJIJournalInFSMask
    BinaryPrimitives.WriteUInt64BigEndian(infoBlock[36..], (ulong)journalOffset);
    BinaryPrimitives.WriteUInt64BigEndian(infoBlock[44..], JournalBlocks * BlockSize);
    WriteJournalHeader(image, journalOffset, SectorSize, SectorSize, littleEndian);
    image.AsSpan(1024, 512).CopyTo(image.AsSpan(image.Length - 1024));
    return image;
  }

  private static void WriteJournalHeader(byte[] image, long journalOffset, long start, long end, bool littleEndian) {
    var header = image.AsSpan((int)journalOffset, SectorSize);
    header.Clear();
    U32(header, 0, 0x4A4E4C78, littleEndian);
    U32(header, 4, 0x12345678, littleEndian);
    U64(header, 8, (ulong)start, littleEndian);
    U64(header, 16, (ulong)end, littleEndian);
    U64(header, 24, JournalBlocks * BlockSize, littleEndian);
    U32(header, 32, BlockListHeaderSize, littleEndian);
    U32(header, 40, SectorSize, littleEndian);
    U32(header, 36, HfsPlusJournal.Checksum(header[..44]), littleEndian);
  }

  private static void U32(Span<byte> b, int at, uint value, bool little) {
    if (little) BinaryPrimitives.WriteUInt32LittleEndian(b[at..], value);
    else BinaryPrimitives.WriteUInt32BigEndian(b[at..], value);
  }

  private static void U64(Span<byte> b, int at, ulong value, bool little) {
    if (little) BinaryPrimitives.WriteUInt64LittleEndian(b[at..], value);
    else BinaryPrimitives.WriteUInt64BigEndian(b[at..], value);
  }

  /// <summary>
  /// Writes one transaction per element of <paramref name="transactions"/> into the journal of
  /// <paramref name="image"/>, starting at <paramref name="start"/> in the circular buffer, and
  /// points the header's start and end at them. Each transaction is a single block list
  /// flagged as a transaction's first, holding the given (volume offset, bytes) blocks.
  /// </summary>
  private static void WriteTransactions(byte[] image, long journalOffset, long start, bool littleEndian,
      params List<(long Offset, byte[] Data)>[] transactions) {
    var size = JournalBlocks * BlockSize;
    var position = start;
    void Put(ReadOnlySpan<byte> bytes) {
      foreach (var b in bytes) {
        if (position >= size) position = SectorSize;
        image[journalOffset + position++] = b;
      }
      if (position >= size) position = SectorSize;
    }
    foreach (var transaction in transactions) {
      var header = new byte[BlockListHeaderSize];
      var count = transaction.Count + 1;
      var bytesUsed = BlockListHeaderSize + transaction.Sum(static b => b.Data.Length);
      BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)(littleEndian ? BinaryPrimitives.ReverseEndianness((ushort)250) : 250));
      BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)(littleEndian ? BinaryPrimitives.ReverseEndianness((ushort)count) : count));
      U32(header, 4, (uint)bytesUsed, littleEndian);
      U32(header, 12, 2, littleEndian);   // BLHDR_FIRST_HEADER
      for (var i = 0; i < transaction.Count; i++) {
        U64(header, 16 + 16 * (i + 1), (ulong)(transaction[i].Offset / SectorSize), littleEndian);
        U32(header, 24 + 16 * (i + 1), (uint)transaction[i].Data.Length, littleEndian);
      }
      U32(header, 8, HfsPlusJournal.Checksum(header.AsSpan(0, 32)), littleEndian);
      Put(header);
      foreach (var (_, data) in transaction) Put(data);
    }
    WriteJournalHeader(image, journalOffset, start, position, littleEndian);
  }

  /// <summary>
  /// The metadata blocks an in-place add of <paramref name="name"/> changes, as a transaction,
  /// and the volume with only the file's data blocks written — what a crash after the data writes
  /// and the journal commit, before the metadata reached its home, leaves on disk.
  /// </summary>
  private static (byte[] OnDisk, List<(long Offset, byte[] Data)> Transaction) Pending(byte[] image, string name, byte[] content) {
    var (_, data, transaction) = Step(image, name, content);
    var onDisk = image.ToArray();
    foreach (var (offset, bytes) in data) bytes.CopyTo(onDisk, offset);
    return (onDisk, transaction);
  }

  /// <summary>An in-place add, split into the file's data blocks and the metadata blocks it changed.</summary>
  private static (byte[] After, List<(long Offset, byte[] Data)> Data, List<(long Offset, byte[] Data)> Metadata) Step(byte[] image, string name, byte[] content) {
    using var ms = new MemoryStream();
    ms.Write(image);
    HfsPlusModifier.AddFile(ms, name, content);
    var after = ms.ToArray();
    var dataBlocks = new HfsPlusReader(new MemoryStream(after)).AllFiles.Single(e => e.Name == name).Extents
      .SelectMany(static e => Enumerable.Range((int)e.StartBlock, (int)e.BlockCount)).ToHashSet();
    var data = new List<(long, byte[])>();
    var metadata = new List<(long, byte[])>();
    for (var block = 0; block < after.Length / BlockSize; block++) {
      var span = after.AsSpan(block * (int)BlockSize, (int)BlockSize);
      if (span.SequenceEqual(image.AsSpan(block * (int)BlockSize, (int)BlockSize))) continue;
      (dataBlocks.Contains(block) ? data : metadata).Add(((long)block * BlockSize, span.ToArray()));
    }
    return (after, data, metadata);
  }

  private static List<string> Paths(byte[] image) => new HfsPlusReader(new MemoryStream(image)).Entries.Select(static e => e.FullPath).ToList();

  // ── Reading ────────────────────────────────────────────────────────

  [TestCase(false, TestName = "GivenABigEndianJournalHoldingATransaction_WhenRead_ThenTheReplayedVolumeIsShown")]
  [TestCase(true, TestName = "GivenALittleEndianJournalHoldingATransaction_WhenRead_ThenTheReplayedVolumeIsShown")]
  [Category("HappyPath")]
  public void ReplayOnRead(bool littleEndian) {
    var image = JournaledVolume(out var journal, littleEndian);
    var (onDisk, transaction) = Pending(image, "new.txt", "journaled"u8.ToArray());
    WriteTransactions(onDisk, journal, SectorSize, littleEndian, transaction);
    var before = onDisk.ToArray();
    var reader = new HfsPlusReader(new MemoryStream(onDisk));
    Assert.Multiple(() => {
      Assert.That(reader.JournalReplayed, Is.True);
      Assert.That(reader.Entries.Select(static e => e.FullPath), Is.EquivalentTo(new[] { "old.txt", "new.txt" }));
      Assert.That(reader.Extract(reader.Entries.Single(static e => e.FullPath == "new.txt")), Is.EqualTo("journaled"u8.ToArray()));
      Assert.That(onDisk, Is.EqualTo(before), "reading does not write the replay");
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenATransactionThatWrapsAroundTheJournalEnd_WhenRead_ThenItIsReplayedWhole() {
    var image = JournaledVolume(out var journal);
    var (onDisk, transaction) = Pending(image, "new.txt", "wrapped"u8.ToArray());
    var start = JournalBlocks * BlockSize - BlockListHeaderSize - 1000;   // the blocks run past the end
    WriteTransactions(onDisk, journal, start, false, transaction);
    var reader = new HfsPlusReader(new MemoryStream(onDisk));
    Assert.That(reader.Extract(reader.Entries.Single(static e => e.FullPath == "new.txt")), Is.EqualTo("wrapped"u8.ToArray()));
  }

  /// <summary>
  /// A damaged block list ends the replay, and the transaction opened last before it is dropped
  /// too: its end was never confirmed. Apple's replay (hfs_journal.c, replay_journal) does the
  /// same — it replays up to the start of the last transaction it saw begin.
  /// </summary>
  [Test, Category("ExceptionalCase")]
  public void GivenAThirdTransactionWithABadChecksum_WhenRead_ThenOnlyTheTransactionsConfirmedEndedAreReplayed() {
    var image = JournaledVolume(out var journal);
    var (withA, dataA, first) = Step(image, "a.txt", "a"u8.ToArray());
    var (withB, dataB, second) = Step(withA, "b.txt", "b"u8.ToArray());
    var (_, dataC, third) = Step(withB, "c.txt", "c"u8.ToArray());
    // On disk: the original metadata with every file's data blocks.
    var onDisk = image.ToArray();
    foreach (var (offset, bytes) in dataA.Concat(dataB).Concat(dataC)) bytes.CopyTo(onDisk, offset);
    WriteTransactions(onDisk, journal, SectorSize, false, first, second, third);
    Assert.That(Paths(onDisk), Is.EquivalentTo(new[] { "old.txt", "a.txt", "b.txt", "c.txt" }), "precondition: all three replay");
    var thirdHeader = journal + SectorSize + 2 * BlockListHeaderSize + first.Concat(second).Sum(static b => b.Data.Length);
    onDisk[thirdHeader + 8] ^= 0xFF;   // its checksum no longer matches
    Assert.That(Paths(onDisk), Is.EquivalentTo(new[] { "old.txt", "a.txt" }));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAJournalHeaderWithABadChecksum_WhenRead_ThenNothingIsReplayed() {
    var image = JournaledVolume(out var journal);
    var (onDisk, transaction) = Pending(image, "new.txt", "x"u8.ToArray());
    WriteTransactions(onDisk, journal, SectorSize, false, transaction);
    onDisk[journal + 36] ^= 0x01;
    var reader = new HfsPlusReader(new MemoryStream(onDisk));
    Assert.Multiple(() => {
      Assert.That(reader.JournalReplayed, Is.False);
      Assert.That(reader.Entries.Select(static e => e.FullPath), Is.EqualTo(new[] { "old.txt" }));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenAnEmptyJournal_WhenRead_ThenTheVolumeIsReadAsStoredAndTheJournalFilesAreHidden() {
    var reader = new HfsPlusReader(new MemoryStream(JournaledVolume(out _)));
    Assert.Multiple(() => {
      Assert.That(reader.JournalReplayed, Is.False);
      Assert.That(reader.Entries.Select(static e => e.FullPath), Is.EqualTo(new[] { "old.txt" }));
    });
  }

  // ── Writing ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenAnEmptyJournal_WhenFilesAreAddedAndRemovedInPlace_ThenTheJournalStaysEmptyAndValid() {
    var image = JournaledVolume(out var journal);
    var header = image.AsSpan((int)journal, SectorSize).ToArray();
    using var ms = new MemoryStream();
    ms.Write(image);
    HfsPlusModifier.AddFile(ms, "added.txt", "added"u8.ToArray());
    Assert.That(HfsPlusModifier.RemoveFile(ms, "old.txt"), Is.True);
    var after = ms.ToArray();
    Assert.Multiple(() => {
      Assert.That(after.AsSpan((int)journal, SectorSize).ToArray(), Is.EqualTo(header), "journal header untouched");
      Assert.That(Paths(after), Is.EqualTo(new[] { "added.txt" }));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(1024 + 4)) & (1u << 13), Is.Not.Zero, "still journaled");
    });
  }

  private static byte[] Dirty() {
    var image = JournaledVolume(out var journal);
    var (onDisk, transaction) = Pending(image, "new.txt", "pending"u8.ToArray());
    WriteTransactions(onDisk, journal, SectorSize, false, transaction);
    return onDisk;
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAJournalHoldingTransactions_WhenAFileIsAdded_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = Dirty();
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.AddFile(ms, "more.txt", [1]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAJournalHoldingTransactions_WhenAFileIsRemoved_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = Dirty();
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.RemoveFile(ms, "old.txt"));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAJournalHoldingTransactions_WhenDefragmentedOrWiped_ThenBothAreRefusedAndTheVolumeIsUnchanged() {
    var image = Dirty();
    using var ms = new MemoryStream();
    ms.Write(image);
    var descriptor = new HfsPlusFormatDescriptor();
    Assert.Multiple(() => {
      Assert.Throws<NotSupportedException>(() => descriptor.Defragment(ms));
      Assert.Throws<NotSupportedException>(() => descriptor.WipeUnusedSpace(ms));
      Assert.That(ms.ToArray(), Is.EqualTo(image));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnEmptyJournalBehindAHole_WhenDefragmented_ThenTheJournalFilesStayWhereTheVolumeHeaderSaysTheyAre() {
    var w = new HfsPlusWriter();
    w.AddFile("!first", new byte[8 * BlockSize]);   // sorts before ".journal", so it is laid out first
    w.AddFile(".journal_info_block", new byte[BlockSize]);
    w.AddFile(".journal", new byte[JournalBlocks * BlockSize]);
    w.AddFile("old.txt", "old"u8.ToArray());
    var image = w.Build(BlockSize);
    var files = new HfsPlusReader(new MemoryStream(image)).AllFiles;
    var info = files.Single(static e => e.Name == ".journal_info_block").FirstBlock;
    var journal = (long)files.Single(static e => e.Name == ".journal").FirstBlock * BlockSize;
    var vh = image.AsSpan(1024);
    BinaryPrimitives.WriteUInt32BigEndian(vh[4..], BinaryPrimitives.ReadUInt32BigEndian(vh[4..]) | (1u << 13));
    BinaryPrimitives.WriteUInt32BigEndian(vh[12..], info);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan((int)(info * BlockSize)), 1);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan((int)(info * BlockSize) + 36), (ulong)journal);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan((int)(info * BlockSize) + 44), JournalBlocks * BlockSize);
    WriteJournalHeader(image, journal, SectorSize, SectorSize, false);
    image.AsSpan(1024, 512).CopyTo(image.AsSpan(image.Length - 1024));
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.That(HfsPlusModifier.RemoveFile(ms, "!first"), Is.True);

    new HfsPlusFormatDescriptor().Defragment(ms);
    var after = ms.ToArray();
    var moved = new HfsPlusReader(new MemoryStream(after)).AllFiles;
    Assert.Multiple(() => {
      Assert.That(moved.Single(static e => e.Name == ".journal_info_block").FirstBlock, Is.EqualTo(info));
      Assert.That((long)moved.Single(static e => e.Name == ".journal").FirstBlock * BlockSize, Is.EqualTo(journal));
      Assert.That(Paths(after), Is.EqualTo(new[] { "old.txt" }), "the journal files are still recognised and hidden");
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAJournalOnAnotherDevice_WhenEdited_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = JournaledVolume(out _);
    var info = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(1024 + 12));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan((int)(info * BlockSize)), 2);   // kJIJournalOnOtherDeviceMask
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.AddFile(ms, "x.txt", [1]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  // ── External ───────────────────────────────────────────────────────

  /// <summary>An empty volume <c>mkfs.hfsplus</c> formats with the given options, read back from WSL.</summary>
  private byte[] Mkfs(string options) {
    HfsPlusOracles.RequireFsck();
    if (!FsInteropToolbox.WslHasTool("mkfs.hfsplus")) Assert.Ignore("mkfs.hfsplus is not installed in WSL.");
    var path = Path.Combine(this._tmp, $"mkfs-{Guid.NewGuid():N}.hfs");
    var wsl = FsInteropToolbox.WinToWsl(path);
    var (stdout, stderr, code) = FsInteropToolbox.RunWsl($"dd if=/dev/zero of={wsl} bs=1M count=32 status=none && mkfs.hfsplus {options} -v Probe {wsl}");
    Assert.That(code, Is.Zero, $"mkfs.hfsplus {options} failed:\n{stdout}\n{stderr}");
    return File.ReadAllBytes(path);
  }

  [TestCase("-J", TestName = "GivenAMkfsJournaledVolume_WhenEditedInPlace_ThenFsckHfsplusAcceptsIt")]
  [TestCase("-s", TestName = "GivenAMkfsCaseSensitiveVolume_WhenEditedInPlace_ThenFsckHfsplusAcceptsIt")]
  [TestCase("-s -J", TestName = "GivenAMkfsJournaledCaseSensitiveVolume_WhenEditedInPlace_ThenFsckHfsplusAcceptsIt")]
  [Category("ExternalFsInterop")]
  public void MkfsVolumeEdited(string options) {
    var image = this.Mkfs(options);
    using var ms = new MemoryStream();
    ms.Write(image);
    foreach (var name in new[] { "readme.txt", "README.txt", "Zeta", "alpha", "docs/guide.txt", "docs/Guide.txt" })
      HfsPlusModifier.AddFile(ms, name, System.Text.Encoding.UTF8.GetBytes("content of " + name));
    HfsPlusModifier.AddFile(ms, "packed.txt", HfsPlusDecmpfsMethodsTests.Text(200_000, 3), HfsPlusCompression.Zlib);
    HfsPlusModifier.AddFile(ms, "tagged.txt", [1], HfsPlusCompression.None, new Dictionary<string, byte[]> { ["com.example.tag"] = [1, 2, 3] });
    for (var i = 0; i < 300; i++) HfsPlusModifier.AddFile(ms, $"bulk/file{i:D3}.txt", [(byte)i]);
    Assert.That(HfsPlusModifier.RemoveFile(ms, "alpha"), Is.True);
    var after = ms.ToArray();
    HfsPlusOracles.AssertFsckClean(after, this._tmp, $"mkfs.hfsplus {options}, edited");
    var reader = new HfsPlusReader(new MemoryStream(after));
    var caseSensitive = options.Contains("-s", StringComparison.Ordinal);
    Assert.Multiple(() => {
      Assert.That(reader.Extract(reader.Entries.Single(static e => e.FullPath == "packed.txt")), Is.EqualTo(HfsPlusDecmpfsMethodsTests.Text(200_000, 3)));
      Assert.That(reader.Entries.Count(static e => e.FullPath.StartsWith("bulk/", StringComparison.Ordinal)), Is.EqualTo(300));
      Assert.That(reader.Entries.Count(static e => e.Name is "readme.txt" or "README.txt"), Is.EqualTo(caseSensitive ? 2 : 1));
    });
  }

  /// <summary>
  /// A volume consistent only once its journal is replayed: the catalog on disk already holds
  /// the new file, while the volume header and allocation bitmap that account for it are still in
  /// the journal. Whether fsck.hfsplus calls it clean says whether it replays journals.
  /// </summary>
  private static byte[] ConsistentOnlyAfterReplay() {
    var image = JournaledVolume(out var journal);
    using var ms = new MemoryStream();
    ms.Write(image);
    HfsPlusModifier.AddFile(ms, "new.txt", "replayed"u8.ToArray());
    var after = ms.ToArray();
    var onDisk = after.ToArray();
    var lastBlock = after.Length / (int)BlockSize - 1;
    var transaction = new List<(long, byte[])>();
    foreach (var block in new[] { 0, 1, lastBlock }) {
      var span = after.AsSpan(block * (int)BlockSize, (int)BlockSize);
      if (span.SequenceEqual(image.AsSpan(block * (int)BlockSize, (int)BlockSize))) continue;
      transaction.Add(((long)block * BlockSize, span.ToArray()));
      image.AsSpan(block * (int)BlockSize, (int)BlockSize).CopyTo(onDisk.AsSpan(block * (int)BlockSize));
    }
    WriteTransactions(onDisk, journal, SectorSize, false, transaction);
    return onDisk;
  }

  /// <summary>
  /// fsck.hfsplus 540.1 (hfsprogs) does not replay a journal — it checks the blocks as stored —
  /// so it is no oracle for the replay itself. What it can judge is the result: the volume as
  /// stored is rejected for exactly the accounting the journal holds, and the same volume with
  /// our replay's writes applied is accepted.
  /// </summary>
  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeConsistentOnlyAfterReplay_WhenOurReplayIsWrittenOut_ThenFsckHfsplusAcceptsIt() {
    var onDisk = ConsistentOnlyAfterReplay();
    var reader = new HfsPlusReader(new MemoryStream(onDisk));
    Assert.That(reader.JournalReplayed, Is.True);
    Assert.That(reader.Extract(reader.Entries.Single(static e => e.FullPath == "new.txt")), Is.EqualTo("replayed"u8.ToArray()));

    var (stored, _) = HfsPlusOracles.Fsck(onDisk, this._tmp);
    Assert.That(stored, Does.Contain("Invalid volume file count"), "as stored, the header still counts the old files");

    var replayed = onDisk.ToArray();
    var journal = HfsPlusJournal.Locate(replayed, replayed.AsSpan(1024, 512), BlockSize)!;
    foreach (var (offset, data) in journal.Replay((o, l) => replayed.AsSpan((int)o, l).ToArray()))
      data.CopyTo(replayed, offset);
    HfsPlusOracles.AssertFsckClean(replayed, this._tmp, "replayed by us");
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenOurJournaledVolumeWithAnEmptyJournal_WhenEditedInPlace_ThenFsckHfsplusAcceptsIt() {
    var image = JournaledVolume(out _);
    using var ms = new MemoryStream();
    ms.Write(image);
    HfsPlusModifier.AddFile(ms, "added.txt", "added"u8.ToArray());
    HfsPlusOracles.AssertFsckClean(ms.ToArray(), this._tmp, "journaled, edited");
  }
}
