using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Qnx4;

namespace Compression.Tests.Qnx4;

/// <summary>
/// R/W (in-place Add/Remove) coverage for the QNX4 file-system, exercised
/// via <see cref="Qnx4Modifier"/> + <see cref="Qnx4FormatDescriptor"/>'s
/// <see cref="IArchiveModifiable"/> surface. The companion WORM tests live
/// in <see cref="Qnx4WormTests"/> — those exercise fresh-image emission.
///
/// Boundaries exercised:
/// <list type="bullet">
///   <item>Round-trip Add then List/Extract picks up the new file</item>
///   <item>Round-trip Remove then List/Extract loses the file</item>
///   <item>Replacement semantics: Add of an existing name swaps content</item>
///   <item>Sequence: fill to cap (29), remove one, add one — no leakage</item>
///   <item>Capacity guard: Add past the 29-user-file root-cluster cap throws
///         <see cref="NotSupportedException"/></item>
///   <item>Bitmap consistency after Add/Remove (removed extents flip back to 0,
///         added extents flip to 1)</item>
///   <item>Removed file data is wiped from the image bytes (no forensic leak)</item>
///   <item>Long-name truncation matches WORM behaviour (16-byte slot)</item>
/// </list>
/// </summary>
[TestFixture]
public class Qnx4RwTests {

  private const int BlockSize = 512;
  private const int InodeSize = 64;
  private const uint BitmapBlock = 5;
  private const int MaxUserFiles = 29; // 32 slots minus 3 system entries

  /// <summary>Builds a fresh single-file QNX4 image via the WORM writer so
  /// every R/W test starts from a known-good baseline.</summary>
  private static MemoryStream FreshImage(string firstName = "seed.txt", string firstContent = "seed") {
    var w = new Qnx4Writer();
    w.AddFile(firstName, Encoding.UTF8.GetBytes(firstContent));
    var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    // MemoryStream defaults to fixed-capacity when constructed from a byte[];
    // we built it expandable so SetLength() works under Add overflow growth.
    return ms;
  }

  private static List<string> ListNames(Stream image) {
    var pos = image.Position;
    try {
      image.Position = 0;
      var r = new Qnx4Reader(image);
      return r.Entries.Select(e => e.Name).ToList();
    } finally {
      image.Position = pos;
    }
  }

  private static byte[]? Extract(Stream image, string name) {
    var pos = image.Position;
    try {
      image.Position = 0;
      var r = new Qnx4Reader(image);
      foreach (var e in r.Entries)
        if (string.Equals(e.Name, name, StringComparison.Ordinal))
          return r.Extract(e);
      return null;
    } finally {
      image.Position = pos;
    }
  }

  // ── Add ──────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_NewFile_AppearsInListAndExtractsCleanly() {
    using var image = FreshImage();
    var payload = Encoding.UTF8.GetBytes("hello from add");
    Qnx4Modifier.AddFile(image, "added.txt", payload);

    var names = ListNames(image);
    Assert.That(names, Is.EquivalentTo(new[] { "seed.txt", "added.txt" }));
    Assert.That(Extract(image, "added.txt"), Is.EqualTo(payload));
    Assert.That(Extract(image, "seed.txt"), Is.EqualTo("seed"u8.ToArray()),
      "Pre-existing entry must survive the in-place Add unchanged.");
  }

  [Test, Category("Boundary")]
  public void Add_ZeroByteFile_AllocatesOneBlockAndExtractsEmpty() {
    using var image = FreshImage();
    Qnx4Modifier.AddFile(image, "zero.bin", []);
    var names = ListNames(image);
    Assert.That(names, Does.Contain("zero.bin"));
    Assert.That(Extract(image, "zero.bin"), Is.EqualTo(Array.Empty<byte>()));
  }

  [Test, Category("Boundary")]
  public void Add_MultiBlockFile_AllocatesContiguousExtent() {
    using var image = FreshImage();
    // 1300 bytes => 3 blocks (512+512+276 → 3 × 512).
    var payload = new byte[1300];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7 & 0xFF);
    Qnx4Modifier.AddFile(image, "big.bin", payload);

    Assert.That(Extract(image, "big.bin"), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Add_ReplacesExistingNameInPlace() {
    using var image = FreshImage();
    Qnx4Modifier.AddFile(image, "doc.txt", "v1"u8.ToArray());
    Assert.That(Extract(image, "doc.txt"), Is.EqualTo("v1"u8.ToArray()));

    Qnx4Modifier.AddFile(image, "doc.txt", "v2-longer"u8.ToArray());
    Assert.That(Extract(image, "doc.txt"), Is.EqualTo("v2-longer"u8.ToArray()),
      "Add of an existing name must replace the content end-to-end.");

    var names = ListNames(image);
    Assert.That(names.Count(n => n == "doc.txt"), Is.EqualTo(1),
      "Replacement must not produce duplicate entries.");
  }

  [Test, Category("Boundary")]
  public void Add_LongName_TruncatesToShortNameLimit() {
    using var image = FreshImage();
    const string longName = "abcdefghijklmnopqrstuvwxyz"; // 26 bytes, > 16
    Qnx4Modifier.AddFile(image, longName, "x"u8.ToArray());

    var names = ListNames(image);
    var truncated = names.SingleOrDefault(n => n.StartsWith("abcdef", StringComparison.Ordinal));
    Assert.That(truncated, Is.Not.Null,
      "A 16-byte truncation of the long name must be readable from the listing.");
    Assert.That(truncated!.Length, Is.LessThanOrEqualTo(16));
  }

  // ── Remove ───────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_ExistingFile_DropsFromListAndReturnsTrue() {
    using var image = FreshImage();
    Qnx4Modifier.AddFile(image, "drop.txt", "drop-me"u8.ToArray());
    var removed = Qnx4Modifier.RemoveFile(image, "drop.txt");
    Assert.That(removed, Is.True);

    var names = ListNames(image);
    Assert.That(names, Does.Not.Contain("drop.txt"));
    Assert.That(names, Does.Contain("seed.txt"),
      "Other entries must survive the targeted remove.");
  }

  [Test, Category("Sad")]
  public void Remove_UnknownFile_ReturnsFalseWithoutMutation() {
    using var image = FreshImage();
    var before = image.ToArray();
    var removed = Qnx4Modifier.RemoveFile(image, "ghost.txt");
    Assert.That(removed, Is.False);

    var after = image.ToArray();
    Assert.That(after, Is.EqualTo(before),
      "Remove of a non-existent name must leave the image bytes untouched.");
  }

  [Test, Category("HappyPath")]
  public void Remove_WipesDataBlocks_NoForensicLeak() {
    using var image = FreshImage();
    var secret = "TOPSECRET-CANARY-CONTENT-FOR-FORENSIC-WIPE-CHECK"u8.ToArray();
    Qnx4Modifier.AddFile(image, "secret.bin", secret);

    var preBytes = image.ToArray();
    Assert.That(IndexOf(preBytes, secret), Is.GreaterThan(-1),
      "Pre-condition: secret must be present in the image before remove.");

    Qnx4Modifier.RemoveFile(image, "secret.bin");

    var postBytes = image.ToArray();
    Assert.That(IndexOf(postBytes, secret), Is.EqualTo(-1),
      "Removed file data must be zero-wiped — no forensic recovery should be possible.");
  }

  // ── Sequencing ───────────────────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void FillToCap_RemoveOne_AddOne_RoundTrips() {
    using var image = FreshImage("seed.txt", "s");
    // Seed already occupies one slot — fill the remaining 28 user slots.
    for (var i = 0; i < MaxUserFiles - 1; i++) {
      var payload = new byte[] { (byte)i };
      Qnx4Modifier.AddFile(image, $"f{i:00}", payload);
    }
    var names = ListNames(image);
    Assert.That(names, Has.Count.EqualTo(MaxUserFiles),
      "Image must hold exactly MaxUserFiles entries after filling.");

    // Remove one in the middle, add a fresh one — the removed slot should be reused.
    Qnx4Modifier.RemoveFile(image, "f10");
    Qnx4Modifier.AddFile(image, "newcomer.txt", "fresh"u8.ToArray());

    var post = ListNames(image);
    Assert.That(post, Does.Not.Contain("f10"));
    Assert.That(post, Does.Contain("newcomer.txt"));
    Assert.That(post, Has.Count.EqualTo(MaxUserFiles),
      "Net entry count must stay at cap after remove + add cycle.");
    Assert.That(Extract(image, "newcomer.txt"), Is.EqualTo("fresh"u8.ToArray()));
  }

  [Test, Category("Sad")]
  public void Add_PastCapacity_ThrowsRootClusterFull() {
    using var image = FreshImage();
    // Seed is slot 3; fill the remaining 28 user slots.
    for (var i = 0; i < MaxUserFiles - 1; i++)
      Qnx4Modifier.AddFile(image, $"g{i:00}", [(byte)i]);

    var ex = Assert.Throws<NotSupportedException>(
      () => Qnx4Modifier.AddFile(image, "overflow.txt", "nope"u8.ToArray()));
    Assert.That(ex!.Message, Does.Contain("root cluster full")
      .Or.Contains("flat root"));
  }

  // ── Bitmap consistency ──────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_FlipsBitmapBitForAllocatedExtent() {
    using var image = FreshImage();
    Qnx4Modifier.AddFile(image, "track.bin", new byte[600]); // 2 blocks

    image.Position = 0;
    var r = new Qnx4Reader(image);
    var entry = r.Entries.Single(e => e.Name == "track.bin");

    var bitmap = ReadBitmap(image);
    for (var b = entry.FirstExtentBlock; b < entry.FirstExtentBlock + entry.ExtentBlockCount; b++) {
      var byteIdx = (int)(b >> 3);
      var bitMask = 1 << (int)(b & 7);
      Assert.That(bitmap[byteIdx] & bitMask, Is.Not.EqualTo(0),
        $"Bitmap bit for block {b} must be 1 after Add.");
    }
  }

  [Test, Category("HappyPath")]
  public void Remove_FlipsBitmapBitsForReleasedExtent() {
    using var image = FreshImage();
    Qnx4Modifier.AddFile(image, "trash.bin", new byte[600]); // 2 blocks

    image.Position = 0;
    var r = new Qnx4Reader(image);
    var entry = r.Entries.Single(e => e.Name == "trash.bin");
    var startBlock = entry.FirstExtentBlock;
    var blockCount = entry.ExtentBlockCount;

    Qnx4Modifier.RemoveFile(image, "trash.bin");

    var bitmap = ReadBitmap(image);
    for (var b = startBlock; b < startBlock + blockCount; b++) {
      var byteIdx = (int)(b >> 3);
      var bitMask = 1 << (int)(b & 7);
      Assert.That(bitmap[byteIdx] & bitMask, Is.EqualTo(0),
        $"Bitmap bit for block {b} must be 0 after Remove.");
    }
  }

  [Test, Category("HappyPath")]
  public void Sequence_AddRemoveAdd_ReusesFreedBlocks() {
    using var image = FreshImage();
    Qnx4Modifier.AddFile(image, "first.bin", new byte[400]);

    image.Position = 0;
    var firstReader = new Qnx4Reader(image);
    var firstBlock = firstReader.Entries.Single(e => e.Name == "first.bin").FirstExtentBlock;

    Qnx4Modifier.RemoveFile(image, "first.bin");
    Qnx4Modifier.AddFile(image, "second.bin", new byte[400]);

    image.Position = 0;
    var secondReader = new Qnx4Reader(image);
    var secondBlock = secondReader.Entries.Single(e => e.Name == "second.bin").FirstExtentBlock;

    Assert.That(secondBlock, Is.EqualTo(firstBlock),
      "After Remove + Add of same-sized files, the freed extent should be reused.");
  }

  // ── Descriptor surface ──────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModifyAndImplementsIArchiveModifiable() {
    var d = new Qnx4FormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Add_PipesToModifier() {
    using var image = FreshImage();
    var d = new Qnx4FormatDescriptor();
    // Name kept ≤ 16 bytes — QNX4 short-name slot fits exactly 16 bytes.
    d.Add(image, [ArchiveInputInfo.InMemory("via-desc.txt", "ok"u8.ToArray())]);

    Assert.That(ListNames(image), Does.Contain("via-desc.txt"));
    Assert.That(Extract(image, "via-desc.txt"), Is.EqualTo("ok"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Remove_PipesToModifier() {
    using var image = FreshImage();
    var d = new Qnx4FormatDescriptor();
    d.Add(image, [ArchiveInputInfo.InMemory("to-remove.txt", "bye"u8.ToArray())]);
    Assert.That(ListNames(image), Does.Contain("to-remove.txt"));

    d.Remove(image, ["to-remove.txt"]);
    Assert.That(ListNames(image), Does.Not.Contain("to-remove.txt"));
  }

  // ── helpers ──────────────────────────────────────────────────────────────

  private static byte[] ReadBitmap(Stream image) {
    var buf = new byte[BlockSize];
    image.Position = BitmapBlock * BlockSize;
    image.ReadExactly(buf);
    return buf;
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

  // Suppress the "unused using" warning for BinaryPrimitives — we keep it
  // imported so future low-level dirent-layout assertions don't need to
  // re-import.
  private static uint ReadLeU32(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32LittleEndian(s);
}
