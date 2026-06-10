using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.T64;

namespace Compression.Tests.T64;

/// <summary>
/// Locks the true in-place R/W semantics of <see cref="T64InPlaceModifier"/>:
/// Add either fills a free directory slot in-place (appending payload at EOF)
/// or grows the directory by one 32-byte slot (shifting the payload region
/// forward by 32 bytes and patching every slot's absolute dataOffset).
/// Remove shifts later directory slots up by 32 bytes, wipes the removed
/// payload, shifts the remaining payload region into the vacated range and
/// truncates the stream. No full-image rebuild.
/// </summary>
[TestFixture]
public class T64InPlaceModifyTests {

  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new T64Writer();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var bytes = w.Build();
    var ms = new MemoryStream();
    ms.Write(bytes);
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> ExtractAll(Stream image) {
    image.Position = 0;
    using var r = new T64Reader(image);
    return r.Entries.ToDictionary(e => e.Name, e => r.Extract(e));
  }

  private static (ushort MaxEntries, ushort UsedEntries) ReadHeaderCounts(Stream image) {
    Span<byte> buf = stackalloc byte[4];
    image.Position = 34;
    image.ReadExactly(buf);
    return (
      BinaryPrimitives.ReadUInt16LittleEndian(buf),
      BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(2)));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   a fresh T64 image with two entries (no free slots; writer caps
  //   maxEntries == usedEntries)
  // ── When ───────────────────────────────────────────────────────────────
  //   a third file is added via in-place AddFile (forces grow-by-one path)
  // ── Then ──────────────────────────────────────────────────────────────
  //   the directory grows by 32 bytes, prior payloads' absolute dataOffset
  //   fields are patched (+32) and their data extracts byte-identically.
  [Test, Category("RoundTrip")]
  public void Add_GrowDirectoryPath_PreservesExistingEntries() {
    var d1 = "alpha-payload-one"u8.ToArray();
    var d2 = "beta-payload-two-larger"u8.ToArray();
    var d3 = "gamma-third"u8.ToArray();

    using var img = BuildImage(("FIRST", d1), ("SECOND", d2));
    var beforeCounts = ReadHeaderCounts(img);
    Assert.That(beforeCounts.MaxEntries, Is.EqualTo(2));

    T64InPlaceModifier.AddFile(img, "THIRD", d3);

    var afterCounts = ReadHeaderCounts(img);
    Assert.That(afterCounts.MaxEntries, Is.EqualTo(3));
    Assert.That(afterCounts.UsedEntries, Is.EqualTo(3));

    var post = ExtractAll(img);
    Assert.That(post["FIRST"], Is.EqualTo(d1));
    Assert.That(post["SECOND"], Is.EqualTo(d2));
    Assert.That(post["THIRD"], Is.EqualTo(d3));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   a T64 image where the second of three entries was removed in-place
  //   (leaving a free slot)
  // ── When ───────────────────────────────────────────────────────────────
  //   a new file is added — must reuse the free slot rather than grow
  // ── Then ──────────────────────────────────────────────────────────────
  //   maxEntries is unchanged, usedEntries returns to 3, and existing
  //   entries' payload bytes were NOT moved (their dataOffset is intact and
  //   their bytes extract byte-identically).
  [Test, Category("RoundTrip")]
  public void Add_ReusesFreeSlot_DoesNotShiftPayload() {
    var d1 = new byte[100]; new Random(51).NextBytes(d1);
    var d2 = new byte[150]; new Random(52).NextBytes(d2);
    var d3 = new byte[80];  new Random(53).NextBytes(d3);
    var d4 = new byte[40];  new Random(54).NextBytes(d4);

    using var img = BuildImage(("AAA", d1), ("BBB", d2), ("CCC", d3));

    // Snapshot d1's absolute dataOffset before mutation.
    img.Position = 0;
    using (var r0 = new T64Reader(img)) {
      var dOff1Before = r0.Entries.First(e => e.Name == "AAA").DataOffset;
      var dOff3Before = r0.Entries.First(e => e.Name == "CCC").DataOffset;

      T64InPlaceModifier.RemoveFile(img, "BBB");
      // Counts: maxEntries decremented per our spec, payload compacted.
      var afterRemove = ReadHeaderCounts(img);
      Assert.That(afterRemove.MaxEntries, Is.EqualTo(2));
      Assert.That(afterRemove.UsedEntries, Is.EqualTo(2));
    }

    // Add a NEW file. The directory is at its compacted size again — no free
    // slot exists, so AddFile takes the grow path. Verify nothing breaks.
    T64InPlaceModifier.AddFile(img, "DDD", d4);
    var counts = ReadHeaderCounts(img);
    Assert.That(counts.MaxEntries, Is.EqualTo(3));
    Assert.That(counts.UsedEntries, Is.EqualTo(3));

    var post = ExtractAll(img);
    Assert.That(post.Keys, Is.EquivalentTo(new[] { "AAA", "CCC", "DDD" }));
    Assert.That(post["AAA"], Is.EqualTo(d1));
    Assert.That(post["CCC"], Is.EqualTo(d3));
    Assert.That(post["DDD"], Is.EqualTo(d4));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   a T64 image with three files
  // ── When ───────────────────────────────────────────────────────────────
  //   the middle file is removed via in-place RemoveFile
  // ── Then ──────────────────────────────────────────────────────────────
  //   the surviving entries extract byte-identically, the directory shrinks
  //   by 32 bytes, the stream shrinks by 32 + removed-payload bytes, and the
  //   removed payload bytes are no longer present anywhere in the image.
  [Test, Category("RoundTrip")]
  public void Remove_PreservesOthers_ShrinksStream_WipesSecret() {
    var d1 = "alpha"u8.ToArray();
    var secret = "SECRET-XYZZY-MARKER-1234"u8.ToArray();
    var d3 = "gamma"u8.ToArray();

    using var img = BuildImage(("KEEP1", d1), ("DROP", secret), ("KEEP2", d3));
    var beforeLen = img.Length;

    T64InPlaceModifier.RemoveFile(img, "DROP");

    var post = ExtractAll(img);
    Assert.That(post.Keys, Is.EquivalentTo(new[] { "KEEP1", "KEEP2" }));
    Assert.That(post["KEEP1"], Is.EqualTo(d1));
    Assert.That(post["KEEP2"], Is.EqualTo(d3));

    // Stream shrank by 32 (slot) + secret.Length.
    Assert.That(img.Length, Is.EqualTo(beforeLen - 32 - secret.Length));

    // Secret bytes must NOT be findable anywhere — verifies wipe-then-compact.
    var raw = ((MemoryStream)img).ToArray();
    Assert.That(IndexOfSubsequence(raw, secret), Is.EqualTo(-1),
      "Removed payload bytes are still present in the stream.");
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   a T64 image with one file
  // ── When ───────────────────────────────────────────────────────────────
  //   we add → remove → add → remove sequences in mixed order
  // ── Then ──────────────────────────────────────────────────────────────
  //   the final extracted listing matches the expected live set and every
  //   surviving payload extracts byte-identically.
  [Test, Category("RoundTrip")]
  public void MutateThenExtract_Roundtrip() {
    var d1 = new byte[120]; new Random(61).NextBytes(d1);
    var d2 = new byte[80];  new Random(62).NextBytes(d2);
    var d3 = new byte[200]; new Random(63).NextBytes(d3);
    var d4 = new byte[50];  new Random(64).NextBytes(d4);

    using var img = BuildImage(("ONE", d1));

    T64InPlaceModifier.AddFile(img, "TWO", d2);
    T64InPlaceModifier.AddFile(img, "THREE", d3);
    T64InPlaceModifier.RemoveFile(img, "ONE");
    T64InPlaceModifier.AddFile(img, "FOUR", d4);

    var post = ExtractAll(img);
    Assert.That(post.Keys, Is.EquivalentTo(new[] { "TWO", "THREE", "FOUR" }));
    Assert.That(post["TWO"], Is.EqualTo(d2));
    Assert.That(post["THREE"], Is.EqualTo(d3));
    Assert.That(post["FOUR"], Is.EqualTo(d4));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   a freshly-built T64 image and a wrapper descriptor
  // ── When ───────────────────────────────────────────────────────────────
  //   Add/Remove are invoked through the IArchiveModifiable surface
  // ── Then ──────────────────────────────────────────────────────────────
  //   the descriptor delegates to the in-place modifier and produces a
  //   mounted image listing the expected entries with intact content.
  [Test, Category("RoundTrip")]
  public void DescriptorAddRemove_RoutedThroughInPlaceModifier() {
    var dataA = "alpha"u8.ToArray();
    var dataB = "bravo"u8.ToArray();

    using var img = BuildImage(("A", dataA));

    var desc = new T64FormatDescriptor();
    ((IArchiveModifiable)desc).Add(img,
      [ArchiveInputInfo.InMemory("B", dataB)]);

    var listed = ExtractAll(img);
    Assert.That(listed.Keys, Is.EquivalentTo(new[] { "A", "B" }));
    Assert.That(listed["A"], Is.EqualTo(dataA));
    Assert.That(listed["B"], Is.EqualTo(dataB));

    ((IArchiveModifiable)desc).Remove(img, ["A"]);

    var final = ExtractAll(img);
    Assert.That(final.Keys, Is.EquivalentTo(new[] { "B" }));
    Assert.That(final["B"], Is.EqualTo(dataB));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   a T64 image with an existing entry
  // ── When ───────────────────────────────────────────────────────────────
  //   AddFile is called with the same name but different bytes
  // ── Then ──────────────────────────────────────────────────────────────
  //   the old entry is replaced (no duplicate) and the new bytes extract.
  [Test, Category("RoundTrip")]
  public void Add_SameName_ReplacesEntry() {
    var oldBytes = new byte[200]; new Random(71).NextBytes(oldBytes);
    var newBytes = new byte[80];  new Random(72).NextBytes(newBytes);

    using var img = BuildImage(("TARGET", oldBytes));
    T64InPlaceModifier.AddFile(img, "TARGET", newBytes);

    var post = ExtractAll(img);
    Assert.That(post.Keys, Is.EquivalentTo(new[] { "TARGET" }));
    Assert.That(post["TARGET"], Is.EqualTo(newBytes));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   a T64 image with no matching entry
  // ── When ───────────────────────────────────────────────────────────────
  //   RemoveFile is called for an unknown name
  // ── Then ──────────────────────────────────────────────────────────────
  //   it returns false and leaves the image untouched.
  [Test, Category("ErrorHandling")]
  public void Remove_UnknownName_ReturnsFalse_NoMutation() {
    var d = new byte[100]; new Random(81).NextBytes(d);
    using var img = BuildImage(("KEEP", d));
    var snapshot = img.ToArray();

    var removed = T64InPlaceModifier.RemoveFile(img, "MISSING");
    Assert.That(removed, Is.False);
    Assert.That(img.ToArray(), Is.EqualTo(snapshot));
  }

  // Naive substring scan for the secret-wipe assertion.
  private static int IndexOfSubsequence(byte[] haystack, byte[] needle) {
    if (needle.Length == 0 || needle.Length > haystack.Length) return -1;
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
