#pragma warning disable CS1591
using FileFormat.PackIt;

namespace Compression.Tests.PackIt;

/// <summary>
/// Locks PackIt's modifier as TRUE in-place at the byte level:
/// <list type="bullet">
///   <item>Add: appends a new entry at EOF. The entire pre-existing byte range
///         <c>[0, oldLength)</c> must be byte-identical at the original offsets.</item>
///   <item>Remove of last entry: the surviving prefix at <c>[0, removedEntryOffset)</c>
///         must be byte-identical at the original offsets.</item>
/// </list>
/// These are the contract that distinguishes a real random-access modifier
/// from a rebuild-disguised one. PackIt has no central directory and no
/// trailing footer, so an EOF-append is a clean in-place mutation.
/// </summary>
[TestFixture]
public class PackItInPlaceModifyTests {

  // ── byte-identity contract ────────────────────────────────────────────────

  [Test, Category("Contract")]
  public void AddFile_PrefixBytesAreByteIdenticalAtOriginalOffsets() {
    // Seed with two entries so the prefix being preserved is non-trivial.
    var seed = BuildSeedPit(("alpha.txt", "alpha-payload"u8.ToArray()),
                             ("beta.txt",  "beta-payload-longer-string"u8.ToArray()));
    var before = seed.ToArray();
    var oldLength = before.Length;

    var ms = new MemoryStream();
    ms.Write(before);

    PackItModifier.AddFile(ms, "gamma.txt", "gamma-payload"u8.ToArray());

    var after = ms.ToArray();
    Assert.That(after.Length, Is.GreaterThan(oldLength),
      "Append must grow the archive.");
    Assert.That(after.AsSpan(0, oldLength).SequenceEqual(before),
      Is.True,
      "Pre-existing bytes [0, oldLength) must be byte-identical at original offsets.");
  }

  [Test, Category("Contract")]
  public void AddFile_AppendsNewEntryAtEofOnly() {
    var seed = BuildSeedPit(("alpha.txt", new byte[100]));
    var oldLength = seed.Length;

    var ms = new MemoryStream();
    ms.Write(seed);
    PackItModifier.AddFile(ms, "added.bin", new byte[42]);

    // The added entry occupies exactly [oldLength, newLength).
    // 87-byte header + 42-byte data = 129 byte entry.
    Assert.That(ms.Length, Is.EqualTo(oldLength + 87 + 42));
  }

  [Test, Category("Contract")]
  public void RemoveLastEntry_PrefixBytesAreByteIdenticalAtOriginalOffsets() {
    // Build seed with three entries; remove the last one and assert the
    // remaining prefix is byte-identical.
    var seed = BuildSeedPit(
      ("keep1.txt", "keep1-data"u8.ToArray()),
      ("keep2.txt", "keep2-data-payload"u8.ToArray()),
      ("drop.txt",  "drop-data"u8.ToArray()));

    // Compute the offset where the last entry starts by reading the index.
    var droppedEntryOffset = FindEntryOffset(seed, "drop.txt");

    var ms = new MemoryStream();
    ms.Write(seed);
    var removed = PackItModifier.RemoveFile(ms, "drop.txt", wipeData: false);
    Assert.That(removed, Is.True);

    var after = ms.ToArray();
    Assert.That(after.Length, Is.EqualTo(droppedEntryOffset),
      "Removing the last entry must truncate to exactly the dropped entry's start offset.");
    Assert.That(after.AsSpan().SequenceEqual(seed.AsSpan(0, (int)droppedEntryOffset)),
      Is.True,
      "Survivor prefix [0, removedEntryOffset) must be byte-identical at original offsets.");
  }

  [Test, Category("Contract")]
  public void RemoveFile_PreservesEarlierSurvivors() {
    // Survivors strictly before the removed entry must keep their original
    // offsets and bytes even when later survivors shift.
    var seed = BuildSeedPit(
      ("first.txt",   "first-data-content"u8.ToArray()),
      ("middle.txt",  "middle-data-content"u8.ToArray()),
      ("last.txt",    "last-data-content"u8.ToArray()));

    var middleOffset = FindEntryOffset(seed, "middle.txt");

    var ms = new MemoryStream();
    ms.Write(seed);
    Assert.That(PackItModifier.RemoveFile(ms, "middle.txt", wipeData: false),
      Is.True);

    var after = ms.ToArray();
    Assert.That(after.AsSpan(0, (int)middleOffset).SequenceEqual(seed.AsSpan(0, (int)middleOffset)),
      Is.True,
      "Survivors strictly before the removed entry must be byte-identical.");
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private static byte[] BuildSeedPit(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var w = new PackItWriter(ms, leaveOpen: true)) {
      foreach (var (n, d) in files) w.AddFile(n, d);
    }
    return ms.ToArray();
  }

  /// <summary>
  /// Walks the PackIt entry chain on a buffer copy and returns the byte
  /// offset of the named entry's "PMag"/"PMa4" magic (i.e. the entry's
  /// 87-byte fixed header begins at this offset).
  /// </summary>
  private static long FindEntryOffset(byte[] data, string targetName) {
    using var ms = new MemoryStream(data);
    using var r = new PackItReader(ms, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.Name, targetName, StringComparison.OrdinalIgnoreCase)) continue;
      // The reader exposes the data-fork offset; the entry's header magic is
      // 87 bytes earlier (PackItReader.EntryHeaderSize).
      return e.DataOffset - PackItReader.EntryHeaderSize;
    }
    throw new InvalidOperationException($"Entry '{targetName}' not found in seed archive.");
  }
}
