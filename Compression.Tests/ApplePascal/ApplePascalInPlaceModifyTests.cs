#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.ApplePascal;

namespace Compression.Tests.ApplePascal;

/// <summary>
/// R/W (in-place Add / Replace / Remove) coverage for the Apple UCSD Pascal
/// filesystem, exercised via <see cref="ApplePascalInPlaceModifier"/> and the
/// <see cref="ApplePascalFormatDescriptor"/>'s <see cref="IArchiveModifiable"/>
/// surface. The companion WORM tests live in the existing
/// <c>ApplePascalConversionTests</c> + detection suites; this file locks the
/// true in-place mutation semantics.
///
/// Boundaries exercised (given-when-then style per CLAUDE.md):
/// <list type="bullet">
///   <item>Add: appears in List / Extract picks up the new file</item>
///   <item>Add: untouched blocks byte-identical at their original offsets</item>
///   <item>Replace (fits): byte-identical untouched blocks, updates last-block size</item>
///   <item>Remove: dropped from List, untouched blocks byte-identical</item>
///   <item>Remove: data extent zero-wiped (no forensic recovery)</item>
///   <item>Capacity guard: Add past the 77-entry cap throws NotSupportedException</item>
///   <item>Sequence: Remove + Add reuses the freed extent</item>
/// </list>
/// </summary>
[TestFixture]
public class ApplePascalInPlaceModifyTests {

  private const int BlockSize = 512;
  private const int DirectoryOffset = 0x400;
  private const int EntrySize = 26;
  private const int FirstDataBlock = 6;

  /// <summary>
  /// Builds a fresh Apple Pascal image via the WORM writer so every R/W test
  /// starts from a known-good baseline. Uses an MS-expandable MemoryStream so
  /// SetLength() works under in-place mutation paths that grow the volume.
  /// </summary>
  private static MemoryStream FreshImage(int volumeBlocks = 280, params (string Name, byte[] Data)[] files) {
    var w = new ApplePascalWriter();
    foreach (var (n, d) in files)
      w.AddFile(n, d);
    var bytes = w.Build(volumeBlocks);
    var ms = new MemoryStream();
    ms.Write(bytes, 0, bytes.Length);
    ms.Position = 0;
    return ms;
  }

  private static List<string> ListNames(Stream image) {
    var pos = image.Position;
    try {
      image.Position = 0;
      using var r = new ApplePascalReader(image);
      return r.Entries.Select(e => e.Name).ToList();
    } finally {
      image.Position = pos;
    }
  }

  private static byte[]? Extract(Stream image, string name) {
    var pos = image.Position;
    try {
      image.Position = 0;
      using var r = new ApplePascalReader(image);
      foreach (var e in r.Entries)
        if (string.Equals(e.Name, name, StringComparison.Ordinal))
          return r.Extract(e);
      return null;
    } finally {
      image.Position = pos;
    }
  }

  /// <summary>Snapshots blocks [startBlock, endBlock) directly from the image
  /// bytes so untouched-extent assertions can compare against the pre-mutation
  /// snapshot byte-for-byte.</summary>
  private static byte[] SnapshotBlocks(Stream image, int startBlock, int endBlock) {
    var pos = image.Position;
    var len = (endBlock - startBlock) * BlockSize;
    var buf = new byte[len];
    image.Position = (long)startBlock * BlockSize;
    image.ReadExactly(buf);
    image.Position = pos;
    return buf;
  }

  // ── Add ──────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_NewFile_AppearsInListAndExtractsCleanly() {
    // given a fresh single-file volume
    using var image = FreshImage(280, ("SEED.TXT", "seed"u8.ToArray()));
    var payload = Encoding.ASCII.GetBytes("hello from add");

    // when adding a second file
    ApplePascalInPlaceModifier.AddFile(image, "ADDED.TXT", payload);

    // then both entries are visible and extract cleanly
    var names = ListNames(image);
    Assert.That(names, Has.Count.EqualTo(2));
    Assert.That(names, Does.Contain("SEED.TXT"));
    Assert.That(names, Does.Contain("ADDED.TXT"));
    Assert.That(Extract(image, "ADDED.TXT"), Is.EqualTo(payload));
    Assert.That(Extract(image, "SEED.TXT"), Is.EqualTo("seed"u8.ToArray()),
      "Pre-existing entry must survive the in-place Add unchanged.");
  }

  [Test, Category("HappyPath")]
  public void Add_LeavesUntouchedBlocksByteIdentical() {
    // given a volume with one existing 1-block file at block 6
    using var image = FreshImage(280, ("SEED.TXT", "seed"u8.ToArray()));
    var seedSnapshot = SnapshotBlocks(image, FirstDataBlock, FirstDataBlock + 1);

    // when adding a new file (which goes to block 7)
    ApplePascalInPlaceModifier.AddFile(image, "NEW.TXT", "new data here"u8.ToArray());

    // then the original file's block bytes are identical
    var seedAfter = SnapshotBlocks(image, FirstDataBlock, FirstDataBlock + 1);
    Assert.That(seedAfter, Is.EqualTo(seedSnapshot),
      "Untouched blocks must be byte-identical after Add — only the new extent + dir entry may change.");
  }

  [Test, Category("Boundary")]
  public void Add_ZeroByteFile_AllocatesOneBlockAndExtractsEmpty() {
    using var image = FreshImage(280);
    ApplePascalInPlaceModifier.AddFile(image, "ZERO.BIN", []);

    var names = ListNames(image);
    Assert.That(names, Does.Contain("ZERO.BIN"));
    Assert.That(Extract(image, "ZERO.BIN"), Has.Length.EqualTo(0).Or.Length.EqualTo(BlockSize),
      "Zero-byte file reserves 1 block; either an empty Extract or a 512-byte block-padded read is acceptable, " +
      "but the file MUST be present.");
  }

  [Test, Category("Boundary")]
  public void Add_MultiBlockFile_AllocatesContiguousExtent() {
    using var image = FreshImage(280);
    var payload = new byte[1300]; // → 3 blocks (512+512+276)
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7 & 0xFF);

    ApplePascalInPlaceModifier.AddFile(image, "BIG.BIN", payload);
    var extracted = Extract(image, "BIG.BIN");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(extracted!.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Add_ReplacesExistingNameInPlace() {
    using var image = FreshImage(280);
    ApplePascalInPlaceModifier.AddFile(image, "DOC.TXT", "v1"u8.ToArray());
    Assert.That(Extract(image, "DOC.TXT")!.AsSpan(0, 2).ToArray(), Is.EqualTo("v1"u8.ToArray()));

    ApplePascalInPlaceModifier.AddFile(image, "DOC.TXT", "v2-longer"u8.ToArray());
    var v2 = Extract(image, "DOC.TXT");
    Assert.That(v2, Is.Not.Null);
    Assert.That(v2!.AsSpan(0, 9).ToArray(), Is.EqualTo("v2-longer"u8.ToArray()));

    var names = ListNames(image);
    Assert.That(names.Count(n => n == "DOC.TXT"), Is.EqualTo(1),
      "Replacement must not produce duplicate entries.");
  }

  [Test, Category("Boundary")]
  public void Add_LongName_TruncatesTo15Chars() {
    using var image = FreshImage(280);
    const string longName = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"; // 26 bytes, > 15
    ApplePascalInPlaceModifier.AddFile(image, longName, "x"u8.ToArray());

    var names = ListNames(image);
    var truncated = names.SingleOrDefault(n => n.StartsWith("ABCDEFG", StringComparison.Ordinal));
    Assert.That(truncated, Is.Not.Null,
      "A 15-byte truncation of the long name must be readable from the listing.");
    Assert.That(truncated!.Length, Is.LessThanOrEqualTo(15));
  }

  [Test, Category("HappyPath")]
  public void Add_PathStyleName_StripsToLeaf() {
    using var image = FreshImage(280);
    ApplePascalInPlaceModifier.AddFile(image, "subdir/NESTED.TXT", "x"u8.ToArray());

    var names = ListNames(image);
    Assert.That(names, Does.Contain("NESTED.TXT"),
      "Apple Pascal is flat-only; path-style inputs must collapse to the leaf name.");
    Assert.That(names.Any(n => n.Contains('/') || n.Contains('\\')), Is.False);
  }

  // ── Replace ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ReplaceIfFits_SameSize_LeavesUntouchedBlocksByteIdentical() {
    // given a volume with two files; the second one occupies blocks 7..8 (1 block)
    using var image = FreshImage(280,
      ("SEED.TXT", "seed"u8.ToArray()),
      ("DATA.TXT", "old-data"u8.ToArray()));

    // snapshot the seed file's block to compare after the replace
    var seedSnapshot = SnapshotBlocks(image, FirstDataBlock, FirstDataBlock + 1);

    // when replacing DATA.TXT with same-or-smaller content
    var fits = ApplePascalInPlaceModifier.ReplaceFileIfFits(image, "DATA.TXT", "new-data"u8.ToArray());
    Assert.That(fits, Is.True);

    // then the seed block stays byte-identical
    var seedAfter = SnapshotBlocks(image, FirstDataBlock, FirstDataBlock + 1);
    Assert.That(seedAfter, Is.EqualTo(seedSnapshot),
      "Replace-in-place must not touch any other file's blocks.");

    var extracted = Extract(image, "DATA.TXT");
    Assert.That(extracted!.AsSpan(0, 8).ToArray(), Is.EqualTo("new-data"u8.ToArray()));
  }

  [Test, Category("Sad")]
  public void ReplaceIfFits_LargerThanExtent_ReturnsFalse() {
    using var image = FreshImage(280, ("TINY.TXT", "x"u8.ToArray()));
    // Tiny file has 1 block (512 B). A 600-byte payload needs 2 blocks → should refuse.
    var fits = ApplePascalInPlaceModifier.ReplaceFileIfFits(image, "TINY.TXT", new byte[600]);
    Assert.That(fits, Is.False,
      "ReplaceFileIfFits must return false when the new payload would overflow the existing extent.");
  }

  [Test, Category("Sad")]
  public void ReplaceIfFits_MissingFile_ReturnsFalse() {
    using var image = FreshImage(280);
    var fits = ApplePascalInPlaceModifier.ReplaceFileIfFits(image, "GHOST.TXT", "data"u8.ToArray());
    Assert.That(fits, Is.False);
  }

  // ── Remove ───────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_ExistingFile_DropsFromListAndReturnsTrue() {
    using var image = FreshImage(280,
      ("SEED.TXT", "seed"u8.ToArray()),
      ("DROP.TXT", "drop-me"u8.ToArray()));

    var removed = ApplePascalInPlaceModifier.RemoveFile(image, "DROP.TXT");
    Assert.That(removed, Is.True);

    var names = ListNames(image);
    Assert.That(names, Does.Not.Contain("DROP.TXT"));
    Assert.That(names, Does.Contain("SEED.TXT"),
      "Other entries must survive the targeted remove.");
  }

  [Test, Category("HappyPath")]
  public void Remove_LeavesUntouchedBlocksByteIdentical() {
    // given a volume where SEED.TXT lives at block 6 and DROP.TXT at block 7
    using var image = FreshImage(280,
      ("SEED.TXT", "seed"u8.ToArray()),
      ("DROP.TXT", "drop-me"u8.ToArray()));
    var seedSnapshot = SnapshotBlocks(image, FirstDataBlock, FirstDataBlock + 1);

    // when removing DROP.TXT
    ApplePascalInPlaceModifier.RemoveFile(image, "DROP.TXT");

    // then SEED.TXT's block stays byte-identical
    var seedAfter = SnapshotBlocks(image, FirstDataBlock, FirstDataBlock + 1);
    Assert.That(seedAfter, Is.EqualTo(seedSnapshot),
      "Untouched blocks must be byte-identical after Remove — only the freed extent + dir entry shift may change.");
  }

  [Test, Category("Sad")]
  public void Remove_UnknownFile_ReturnsFalseWithoutMutation() {
    using var image = FreshImage(280, ("SEED.TXT", "seed"u8.ToArray()));
    var before = image.ToArray();
    var removed = ApplePascalInPlaceModifier.RemoveFile(image, "GHOST.TXT");
    Assert.That(removed, Is.False);

    var after = image.ToArray();
    Assert.That(after, Is.EqualTo(before),
      "Remove of a non-existent name must leave the image bytes untouched.");
  }

  [Test, Category("HappyPath")]
  public void Remove_WipesDataBlocks_NoForensicLeak() {
    using var image = FreshImage(280);
    var secret = "TOPSECRET-CANARY-CONTENT-FOR-FORENSIC-WIPE-CHECK"u8.ToArray();
    ApplePascalInPlaceModifier.AddFile(image, "SECRET.BIN", secret);

    var preBytes = image.ToArray();
    Assert.That(IndexOf(preBytes, secret), Is.GreaterThan(-1),
      "Pre-condition: secret must be present in the image before remove.");

    ApplePascalInPlaceModifier.RemoveFile(image, "SECRET.BIN");

    var postBytes = image.ToArray();
    Assert.That(IndexOf(postBytes, secret), Is.EqualTo(-1),
      "Removed file data must be zero-wiped — no forensic recovery should be possible.");
  }

  // ── Sequencing ───────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Sequence_RemoveThenAdd_ReusesFreedExtent() {
    using var image = FreshImage(280);
    ApplePascalInPlaceModifier.AddFile(image, "FIRST.BIN", new byte[400]);

    image.Position = 0;
    using (var r = new ApplePascalReader(image)) {
      var first = r.Entries.Single(e => e.Name == "FIRST.BIN");
      Assert.That(first.StartBlock, Is.EqualTo(FirstDataBlock));
    }

    ApplePascalInPlaceModifier.RemoveFile(image, "FIRST.BIN");
    ApplePascalInPlaceModifier.AddFile(image, "SECOND.BIN", new byte[400]);

    image.Position = 0;
    using (var r2 = new ApplePascalReader(image)) {
      var second = r2.Entries.Single(e => e.Name == "SECOND.BIN");
      Assert.That(second.StartBlock, Is.EqualTo(FirstDataBlock),
        "After Remove + Add of same-sized file, the freed extent at block 6 should be reused.");
    }
  }

  [Test, Category("Boundary")]
  public void Add_PastCapacity_ThrowsDirectoryFull() {
    // A volume big enough for 78 single-block files (78 + 6 reserved = 84 → round to 88).
    using var image = FreshImage(88);
    // Fill all 77 entry slots with 1-block payloads.
    for (var i = 0; i < ApplePascalReader.MaxEntries; i++)
      ApplePascalInPlaceModifier.AddFile(image, $"F{i:00}.TXT", [(byte)i]);

    var ex = Assert.Throws<NotSupportedException>(
      () => ApplePascalInPlaceModifier.AddFile(image, "OVERFLOW.TXT", "nope"u8.ToArray()));
    Assert.That(ex!.Message, Does.Contain("directory full")
      .Or.Contain("Apple Pascal").IgnoreCase);
  }

  [Test, Category("Boundary")]
  public void Add_PastVolumeCapacity_ThrowsVolumeFull() {
    // 8-block volume = 2 reserved + 4 dir = 6 reserved, leaves blocks 6..7 (2 free).
    using var image = FreshImage(8);
    // Fill the 2 available data blocks.
    ApplePascalInPlaceModifier.AddFile(image, "FILL.BIN", new byte[BlockSize * 2]);

    var ex = Assert.Throws<IOException>(
      () => ApplePascalInPlaceModifier.AddFile(image, "OVERFLOW.BIN", new byte[BlockSize]));
    Assert.That(ex!.Message, Does.Contain("volume full").IgnoreCase
      .Or.Contain("contiguous").IgnoreCase);
  }

  // ── Descriptor surface ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModifyAndImplementsIArchiveModifiable() {
    var d = new ApplePascalFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Add_PipesToModifier() {
    using var image = FreshImage(280, ("SEED.TXT", "seed"u8.ToArray()));
    var d = new ApplePascalFormatDescriptor();
    d.Add(image, [ArchiveInputInfo.InMemory("VIA-DESC.TXT", "ok"u8.ToArray())]);

    var names = ListNames(image);
    Assert.That(names, Does.Contain("VIA-DESC.TXT"));
    var extracted = Extract(image, "VIA-DESC.TXT");
    Assert.That(extracted!.AsSpan(0, 2).ToArray(), Is.EqualTo("ok"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Remove_PipesToModifier() {
    using var image = FreshImage(280);
    var d = new ApplePascalFormatDescriptor();
    d.Add(image, [ArchiveInputInfo.InMemory("TO-REMOVE.TXT", "bye"u8.ToArray())]);
    Assert.That(ListNames(image), Does.Contain("TO-REMOVE.TXT"));

    d.Remove(image, ["TO-REMOVE.TXT"]);
    Assert.That(ListNames(image), Does.Not.Contain("TO-REMOVE.TXT"));
  }

  // ── Header integrity ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_IncrementsHeaderFileCount() {
    using var image = FreshImage(280, ("SEED.TXT", "seed"u8.ToArray()));
    var preCount = ReadHeaderFileCount(image);
    ApplePascalInPlaceModifier.AddFile(image, "TWO.TXT", "x"u8.ToArray());
    var postCount = ReadHeaderFileCount(image);
    Assert.That(postCount, Is.EqualTo(preCount + 1));
  }

  [Test, Category("HappyPath")]
  public void Remove_DecrementsHeaderFileCount() {
    using var image = FreshImage(280,
      ("A.TXT", "a"u8.ToArray()),
      ("B.TXT", "b"u8.ToArray()));
    var preCount = ReadHeaderFileCount(image);
    ApplePascalInPlaceModifier.RemoveFile(image, "A.TXT");
    var postCount = ReadHeaderFileCount(image);
    Assert.That(postCount, Is.EqualTo(preCount - 1));
  }

  private static int ReadHeaderFileCount(Stream image) {
    var pos = image.Position;
    try {
      image.Position = DirectoryOffset + 16;
      Span<byte> buf = stackalloc byte[2];
      image.ReadExactly(buf);
      return BinaryPrimitives.ReadUInt16LittleEndian(buf);
    } finally {
      image.Position = pos;
    }
  }

  /// <summary>Trivial substring search over a byte array.</summary>
  private static int IndexOf(byte[] haystack, byte[] needle) {
    if (needle.Length == 0) return 0;
    for (var i = 0; i <= haystack.Length - needle.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++) {
        if (haystack[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return i;
    }
    return -1;
  }
}
