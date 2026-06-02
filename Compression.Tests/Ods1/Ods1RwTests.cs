using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Ods1;

namespace Compression.Tests.Ods1;

/// <summary>
/// R/W (random-access in-place mutation) suite for the DEC ODS-1 (Files-11 L1)
/// modifier. ODS-1 has no Linux fsck, so the gate is self-round-trip via
/// <see cref="Ods1Reader"/> plus a home-block-checksum recompute check: every
/// mutation must leave the image in a state where the reader still parses
/// every surviving entry byte-exact and the additive checksums at home-block
/// offsets +0x02C and +0x1FE still match the on-disk content.
/// </summary>
[TestFixture]
public class Ods1RwTests {

  private const int LbnSize = 512;
  private const int HomeBlockOffset = LbnSize;        // LBN 1
  private const int BitmapOffset = 2 * LbnSize;       // LBN 2
  private const int IndexfLbn = 4;
  private const int IndexfHeaderSlots = 64;

  private static byte[] BuildBaseline(params (string Name, byte[] Data)[] files)
    => Ods1Writer.Build(files);

  private static MemoryStream Open(byte[] image) {
    var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    ms.Position = 0;
    return ms;
  }

  private static byte[] ReadExtractedBytes(MemoryStream ms, string name) {
    ms.Position = 0;
    using var r = new Ods1Reader(ms);
    var entry = r.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    Assert.That(entry, Is.Not.Null, $"missing {name} after round-trip");
    return r.Extract(entry!);
  }

  private static (ushort sum1, ushort sum2) ReadHomeChecksums(MemoryStream ms) {
    ms.Position = HomeBlockOffset;
    var hb = new byte[LbnSize];
    var read = ms.Read(hb, 0, LbnSize);
    Assert.That(read, Is.EqualTo(LbnSize));
    return (
      BinaryPrimitives.ReadUInt16LittleEndian(hb.AsSpan(0x02C)),
      BinaryPrimitives.ReadUInt16LittleEndian(hb.AsSpan(0x1FE))
    );
  }

  /// <summary>Verifies the on-disk home-block additive checksums match what
  /// the spec requires (and what the writer/modifier compute) — independent
  /// of any internal helper, computed from the raw bytes.</summary>
  private static void AssertHomeChecksumsMatch(MemoryStream ms) {
    ms.Position = HomeBlockOffset;
    var hb = new byte[LbnSize];
    var read = ms.Read(hb, 0, LbnSize);
    Assert.That(read, Is.EqualTo(LbnSize));

    var diskSum1 = BinaryPrimitives.ReadUInt16LittleEndian(hb.AsSpan(0x02C));
    var diskSum2 = BinaryPrimitives.ReadUInt16LittleEndian(hb.AsSpan(0x1FE));

    // Recompute first-half sum (bytes 0..0x2C with the checksum slot itself zeroed
    // — the slot at +0x02C is not part of the first-half range so it doesn't
    // contribute regardless, but we don't include +0x1FE in this sum).
    BinaryPrimitives.WriteUInt16LittleEndian(hb.AsSpan(0x02C), 0);
    ushort sum1 = 0;
    for (var i = 0; i < 0x2C; i += 2)
      sum1 += BinaryPrimitives.ReadUInt16LittleEndian(hb.AsSpan(i));

    // Recompute second-half sum (bytes 0..0x1FE) with sum1 written into +0x02C
    // first — the algorithm folds the first-half checksum into the second-half
    // sum because the running sum walks the home block from offset 0 and so
    // picks up the freshly-written sum1 word along the way.
    BinaryPrimitives.WriteUInt16LittleEndian(hb.AsSpan(0x02C), sum1);
    BinaryPrimitives.WriteUInt16LittleEndian(hb.AsSpan(0x1FE), 0);
    ushort sum2 = 0;
    for (var i = 0; i < 0x1FE; i += 2)
      sum2 += BinaryPrimitives.ReadUInt16LittleEndian(hb.AsSpan(i));

    Assert.That(diskSum1, Is.EqualTo(sum1), "home-block first-half checksum stale");
    Assert.That(diskSum2, Is.EqualTo(sum2), "home-block second-half checksum stale");
  }

  // ── HappyPath: descriptor advertises CanModify ────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify() {
    var d = new Ods1FormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  // ── RoundTrip: Add then read ─────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_SingleFile_ReadsBackByteExact() {
    var baseline = BuildBaseline(("FIRST.TXT", "first"u8.ToArray()));
    using var ms = Open(baseline);

    var newData = "added"u8.ToArray();
    Ods1Modifier.AddFile(ms, "ADDED.TXT", newData);

    var got = ReadExtractedBytes(ms, "ADDED.TXT");
    Assert.That(got.Length, Is.GreaterThanOrEqualTo(newData.Length));
    Assert.That(got.AsSpan(0, newData.Length).ToArray(), Is.EqualTo(newData));
    // Original file still readable.
    var first = ReadExtractedBytes(ms, "FIRST.TXT");
    Assert.That(first.AsSpan(0, 5).ToArray(), Is.EqualTo("first"u8.ToArray()));

    AssertHomeChecksumsMatch(ms);
  }

  [Test, Category("RoundTrip")]
  public void Add_MultipleFiles_AllReadBackInIndexOrder() {
    var baseline = BuildBaseline();  // empty volume
    // Writer floors total size to fit index window + 1 data block, so the
    // empty-baseline image is large enough for additions.
    using var ms = Open(baseline);

    var alpha = "alpha"u8.ToArray();
    var beta = Encoding.ASCII.GetBytes("beta beta beta");
    var gamma = Enumerable.Range(0, 300).Select(i => (byte)(i & 0xFF)).ToArray();
    Ods1Modifier.AddFile(ms, "ALPHA.TXT", alpha);
    Ods1Modifier.AddFile(ms, "BETA.LOG", beta);
    Ods1Modifier.AddFile(ms, "GAMMA.BIN", gamma);

    Assert.That(ReadExtractedBytes(ms, "ALPHA.TXT").AsSpan(0, alpha.Length).ToArray(), Is.EqualTo(alpha));
    Assert.That(ReadExtractedBytes(ms, "BETA.LOG").AsSpan(0, beta.Length).ToArray(), Is.EqualTo(beta));
    Assert.That(ReadExtractedBytes(ms, "GAMMA.BIN").AsSpan(0, gamma.Length).ToArray(), Is.EqualTo(gamma));
    AssertHomeChecksumsMatch(ms);
  }

  // ── RoundTrip: Remove ─────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Remove_SingleFile_DisappearsAndDataIsWiped() {
    var data = "secret data that should be wiped"u8.ToArray();
    var baseline = BuildBaseline(("KEEP.TXT", "keep"u8.ToArray()), ("GONE.TXT", data));
    using var ms = Open(baseline);

    var removed = Ods1Modifier.RemoveFile(ms, "GONE.TXT");
    Assert.That(removed, Is.True);

    ms.Position = 0;
    using (var r = new Ods1Reader(ms)) {
      Assert.That(r.Entries.Any(e => e.Name == "GONE.TXT"), Is.False);
      Assert.That(r.Entries.Any(e => e.Name == "KEEP.TXT"), Is.True);
    }

    // The data region should be zero-wiped — no recovery of "secret" possible.
    var image = ms.ToArray();
    Assert.That(IndexOfSequence(image, "secret"u8), Is.LessThan(0),
      "removed payload still discoverable in image bytes");

    AssertHomeChecksumsMatch(ms);
  }

  [Test, Category("RoundTrip")]
  public void Remove_UnknownFile_ReturnsFalse() {
    var baseline = BuildBaseline(("EXISTS.TXT", "x"u8.ToArray()));
    using var ms = Open(baseline);
    Assert.That(Ods1Modifier.RemoveFile(ms, "GHOST.NOT"), Is.False);
    Assert.That(ReadExtractedBytes(ms, "EXISTS.TXT").AsSpan(0, 1).ToArray(), Is.EqualTo("x"u8.ToArray()));
    AssertHomeChecksumsMatch(ms);
  }

  // ── RoundTrip: Add then Remove ───────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_ThenRemove_LeavesOriginalEntriesIntact() {
    var baseline = BuildBaseline(("ORIG.TXT", "kept"u8.ToArray()));
    using var ms = Open(baseline);

    var transient = "transient payload"u8.ToArray();
    Ods1Modifier.AddFile(ms, "TEMP.DAT", transient);
    Assert.That(ReadExtractedBytes(ms, "TEMP.DAT").AsSpan(0, transient.Length).ToArray(), Is.EqualTo(transient));

    var removed = Ods1Modifier.RemoveFile(ms, "TEMP.DAT");
    Assert.That(removed, Is.True);

    ms.Position = 0;
    using (var r = new Ods1Reader(ms)) {
      Assert.That(r.Entries.Any(e => e.Name == "TEMP.DAT"), Is.False);
      Assert.That(r.Entries.Any(e => e.Name == "ORIG.TXT"), Is.True);
    }

    Assert.That(ReadExtractedBytes(ms, "ORIG.TXT").AsSpan(0, 4).ToArray(), Is.EqualTo("kept"u8.ToArray()));
    AssertHomeChecksumsMatch(ms);
  }

  // ── RoundTrip: interleaved Add/Remove sequence ───────────────────────────

  [Test, Category("RoundTrip")]
  public void AddRemoveSequence_FinalStateMatchesExpected() {
    var baseline = BuildBaseline(("A.TXT", "a"u8.ToArray()));
    using var ms = Open(baseline);

    // Add 4 files, remove 2, add 1 more, remove 1 from the originals.
    Ods1Modifier.AddFile(ms, "B.TXT", "bb"u8.ToArray());
    Ods1Modifier.AddFile(ms, "C.TXT", "ccc"u8.ToArray());
    Ods1Modifier.AddFile(ms, "D.TXT", "dddd"u8.ToArray());
    Ods1Modifier.AddFile(ms, "E.TXT", "eeeee"u8.ToArray());

    Assert.That(Ods1Modifier.RemoveFile(ms, "B.TXT"), Is.True);
    Assert.That(Ods1Modifier.RemoveFile(ms, "D.TXT"), Is.True);

    Ods1Modifier.AddFile(ms, "F.TXT", "ffffff"u8.ToArray());

    Assert.That(Ods1Modifier.RemoveFile(ms, "A.TXT"), Is.True);

    ms.Position = 0;
    using var r = new Ods1Reader(ms);
    var names = r.Entries.Select(e => e.Name).OrderBy(n => n).ToArray();
    Assert.That(names, Is.EqualTo(new[] { "C.TXT", "E.TXT", "F.TXT" }));

    AssertHomeChecksumsMatch(ms);
  }

  // ── RoundTrip: free-slot reuse ───────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_AfterRemove_ReusesFreedHeaderSlot() {
    var baseline = BuildBaseline(("ONE.TXT", "1"u8.ToArray()));
    using var ms = Open(baseline);

    Ods1Modifier.AddFile(ms, "TWO.TXT", "22"u8.ToArray());
    Ods1Modifier.AddFile(ms, "THREE.TXT", "333"u8.ToArray());

    // The "TWO.TXT" header currently sits in INDEXF slot 1 (slot 0 = ONE.TXT).
    Assert.That(Ods1Modifier.RemoveFile(ms, "TWO.TXT"), Is.True);

    // Slot 1 is now free; a new add should pick it up before slot 3.
    Ods1Modifier.AddFile(ms, "REUSE.TXT", "reuse"u8.ToArray());

    var reusedSlotHeader = (IndexfLbn + 1) * LbnSize;
    ms.Position = reusedSlotHeader + 2;
    var slot1FileNum = new byte[2];
    var r1 = ms.Read(slot1FileNum, 0, 2);
    Assert.That(r1, Is.EqualTo(2));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(slot1FileNum), Is.Not.EqualTo(0),
      "reused header slot must be live again");

    Assert.That(ReadExtractedBytes(ms, "REUSE.TXT").AsSpan(0, 5).ToArray(), Is.EqualTo("reuse"u8.ToArray()));
    AssertHomeChecksumsMatch(ms);
  }

  // ── Boundary: empty payload ──────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void Add_EmptyFile_AllocatesOneLbn() {
    var baseline = BuildBaseline(("EXISTS.TXT", "x"u8.ToArray()));
    using var ms = Open(baseline);

    Ods1Modifier.AddFile(ms, "EMPTY.TXT", []);

    ms.Position = 0;
    using var r = new Ods1Reader(ms);
    var entry = r.Entries.First(e => e.Name == "EMPTY.TXT");
    Assert.That(entry.BlockCount, Is.EqualTo(1u));
    var bytes = r.Extract(entry);
    Assert.That(bytes.Length, Is.EqualTo(LbnSize));
    Assert.That(bytes.All(b => b == 0), Is.True);
    AssertHomeChecksumsMatch(ms);
  }

  [Test, Category("Boundary")]
  public void Add_MultiBlockPayload_ContiguousExtentAllocated() {
    var baseline = BuildBaseline();
    using var ms = Open(baseline);

    var payload = new byte[5 * LbnSize - 7];  // 5 blocks
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 31) & 0xFF);
    Ods1Modifier.AddFile(ms, "BIG.DAT", payload);

    ms.Position = 0;
    using var r = new Ods1Reader(ms);
    var entry = r.Entries.First(e => e.Name == "BIG.DAT");
    Assert.That(entry.BlockCount, Is.EqualTo(5u));
    var got = r.Extract(entry);
    Assert.That(got.Length, Is.EqualTo(5 * LbnSize));
    Assert.That(got.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
    AssertHomeChecksumsMatch(ms);
  }

  // ── Sad: capacity exhaustion ─────────────────────────────────────────────

  [Test, Category("Sad")]
  public void Add_WhenIndexfWindowFull_ThrowsNotSupported() {
    // Fill the volume to exactly the 64-slot limit.
    var files = Enumerable.Range(0, IndexfHeaderSlots)
      .Select(i => ($"F{i:D5}.X", new byte[] { (byte)i }))
      .ToArray();
    var baseline = BuildBaseline(files);
    using var ms = Open(baseline);

    Assert.Throws<NotSupportedException>(
      () => Ods1Modifier.AddFile(ms, "OVERFLOW.TXT", "no room"u8.ToArray()));
  }

  [Test, Category("Sad")]
  public void Add_WhenBitmapHasNoContiguousRun_ThrowsNotSupported() {
    // A single bitmap LBN tracks 4096 LBNs. We poison every LBN past the
    // data-start boundary with an alternating allocated/free pattern so no
    // contiguous run of ≥ 2 free LBNs exists anywhere in the bitmap's
    // tracked range — any multi-block Add must fail.
    var baseline = BuildBaseline(("FILLER.TXT", "x"u8.ToArray()));
    using var ms = Open(baseline);
    var image = ms.ToArray();

    var bitmapCapacity = (uint)(LbnSize * 8);
    for (var lbn = (uint)(IndexfLbn + IndexfHeaderSlots); lbn < bitmapCapacity; lbn++) {
      var bitmapByte = BitmapOffset + (int)(lbn / 8);
      var bit = (int)(lbn % 8);
      // Force alternating allocated/free pattern so every free slot is isolated.
      var allocatedForThisLbn = (lbn & 1u) == 0;
      if (allocatedForThisLbn)
        image[bitmapByte] |= (byte)(1 << bit);
      else
        image[bitmapByte] = (byte)(image[bitmapByte] & ~(1 << bit));
    }
    ms.SetLength(0);
    ms.Write(image, 0, image.Length);
    ms.Position = 0;

    // Two-LBN file: needs contiguous run of 2 — must fail.
    var twoBlocks = new byte[LbnSize + 1];
    Assert.Throws<NotSupportedException>(
      () => Ods1Modifier.AddFile(ms, "BIG.DAT", twoBlocks));
  }

  // ── RoundTrip: via descriptor IArchiveModifiable surface ─────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_AddAndRemove_RoundTripsViaIArchiveModifiable() {
    var d = new Ods1FormatDescriptor();
    var baseline = BuildBaseline(("EXIST.TXT", "exist"u8.ToArray()));
    using var ms = Open(baseline);

    var modifiable = (IArchiveModifiable)d;
    var addPayload = "added via descriptor"u8.ToArray();
    modifiable.Add(ms, [ArchiveInputInfo.InMemory("NEW.TXT", addPayload)]);

    ms.Position = 0;
    var entries1 = d.List(ms, null);
    Assert.That(entries1.Any(e => string.Equals(e.Name, "NEW.TXT", StringComparison.OrdinalIgnoreCase)), Is.True);

    var got = ReadExtractedBytes(ms, "NEW.TXT");
    Assert.That(got.AsSpan(0, addPayload.Length).ToArray(), Is.EqualTo(addPayload));

    modifiable.Remove(ms, ["NEW.TXT"]);
    ms.Position = 0;
    var entries2 = d.List(ms, null);
    Assert.That(entries2.Any(e => string.Equals(e.Name, "NEW.TXT", StringComparison.OrdinalIgnoreCase)), Is.False);
    Assert.That(entries2.Any(e => string.Equals(e.Name, "EXIST.TXT", StringComparison.OrdinalIgnoreCase)), Is.True);

    AssertHomeChecksumsMatch(ms);
  }

  // ── Helper: byte-sequence search ─────────────────────────────────────────

  private static int IndexOfSequence(byte[] haystack, ReadOnlySpan<byte> needle) {
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
