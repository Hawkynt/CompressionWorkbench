#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Vdfs;

namespace Compression.Tests.Vdfs;

[TestFixture]
public class VdfsInPlaceModifyTests {

  /// <summary>
  /// Builds a VDFS image with the given seed files using <see cref="VdfsWriter"/>
  /// and returns it as an expandable seekable stream ready for in-place mutation.
  /// </summary>
  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new VdfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var bytes = w.Build();
    var ms = new MemoryStream(bytes.Length * 4);
    ms.Write(bytes);
    ms.SetLength(bytes.Length);
    ms.Position = 0;
    return ms;
  }

  /// <summary>
  /// Returns the (offset, length) window of every live file's payload as the
  /// original writer placed it: data starts immediately after the writer's
  /// default 36 + N*80 table and is laid down in declaration order.
  /// </summary>
  private static List<(long Offset, long Length, byte[] Bytes)> CaptureOriginalExtents(
    byte[] image, params (string Name, byte[] Data)[] files) {
    const int defaultDataStart = 36 + 0; // base; entries occupy N*80
    var entrySize = 80;
    var dataStart = 36 + files.Length * entrySize;
    var extents = new List<(long, long, byte[])>();
    var cursor = (long)dataStart;
    foreach (var (_, d) in files) {
      var slice = image.AsSpan((int)cursor, d.Length).ToArray();
      extents.Add((cursor, d.Length, slice));
      cursor += d.Length;
    }
    _ = defaultDataStart;
    return extents;
  }

  // ── Add (true in-place) ───────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Add_AppendsAtTail_ExistingDataByteIdenticalAtOriginalOffsets() {
    var seedA = "ALPHA_SEED_BYTES"u8.ToArray();
    var seedB = new byte[200];
    for (var i = 0; i < seedB.Length; i++) seedB[i] = (byte)(i ^ 0x5A);

    using var img = BuildImage(("a.txt", seedA), ("b.bin", seedB));
    var originalBytes = img.ToArray();
    var originalExtents = CaptureOriginalExtents(originalBytes,
      ("a.txt", seedA), ("b.bin", seedB));

    VdfsInPlaceModifier.AddFile(img, "c.dat", "CHARLIE"u8.ToArray());

    var newBytes = img.ToArray();
    // Every original data window must contain the exact original bytes.
    foreach (var (off, len, expected) in originalExtents) {
      var actual = newBytes.AsSpan((int)off, (int)len).ToArray();
      Assert.That(actual, Is.EqualTo(expected),
        $"original payload at offset {off}..+{len} was disturbed");
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Add_LeavesSurvivingEntryRecordsByteIdentical() {
    // After Add, the new entry table lives at a fresh offset, but the
    // surviving entry records must be byte-identical to the writer's originals
    // (they carry the unchanged jump/size/type fields).
    using var img = BuildImage(("a.txt", "AAA"u8.ToArray()), ("b.txt", "BBB"u8.ToArray()));
    var original = img.ToArray();
    var entrySize = 80;
    var origEntries = new[] {
      original.AsSpan(36, entrySize).ToArray(),
      original.AsSpan(36 + entrySize, entrySize).ToArray(),
    };

    VdfsInPlaceModifier.AddFile(img, "c.txt", "CCC"u8.ToArray());

    img.Position = 0;
    var ctx = new VdfsReader(img);
    img.Position = 0;
    var newBytes = img.ToArray();
    var newRootOffset = BinaryPrimitives.ReadUInt32LittleEndian(newBytes.AsSpan(32));

    var actualA = newBytes.AsSpan((int)newRootOffset, entrySize).ToArray();
    var actualB = newBytes.AsSpan((int)newRootOffset + entrySize, entrySize).ToArray();
    Assert.That(actualA, Is.EqualTo(origEntries[0]),
      "surviving entry a.txt must be byte-identical after relocation");
    Assert.That(actualB, Is.EqualTo(origEntries[1]),
      "surviving entry b.txt must be byte-identical after relocation");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Add_NewFileRoundTripsThroughReader() {
    using var img = BuildImage(("seed.txt", "SEED"u8.ToArray()));
    var payload = new byte[1024];
    new Random(123).NextBytes(payload);

    VdfsInPlaceModifier.AddFile(img, "added.bin", payload);

    img.Position = 0;
    var r = new VdfsReader(img);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "seed.txt", "added.bin" }));
    var entry = r.Entries.First(e => e.Name == "added.bin");
    Assert.That(r.Extract(entry), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Add_HeaderRootOffsetMovedToNewTableLocation() {
    using var img = BuildImage(("a.txt", "A"u8.ToArray()));
    VdfsInPlaceModifier.AddFile(img, "b.txt", "B"u8.ToArray());

    var bytes = img.ToArray();
    var rootOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32));
    Assert.That(rootOffset, Is.GreaterThan((uint)(36 + 80)),
      "root offset must point past the original entry-table region");
    Assert.That(rootOffset + 2 * 80, Is.LessThanOrEqualTo((uint)bytes.Length),
      "relocated table must fit inside the image");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Add_MultipleAdds_AllReadable_AllOriginalSurvives() {
    var seedAlpha = "ALPHA"u8.ToArray();
    var seedBeta = "BETA"u8.ToArray();
    using var img = BuildImage(("alpha.txt", seedAlpha), ("beta.txt", seedBeta));
    var originalExtents = CaptureOriginalExtents(img.ToArray(),
      ("alpha.txt", seedAlpha), ("beta.txt", seedBeta));

    VdfsInPlaceModifier.AddFile(img, "c.txt", "CCC"u8.ToArray());
    VdfsInPlaceModifier.AddFile(img, "d.txt", "DDDD"u8.ToArray());
    VdfsInPlaceModifier.AddFile(img, "e.txt", "EEEEE"u8.ToArray());

    var snapshot = img.ToArray();
    foreach (var (off, len, expected) in originalExtents) {
      Assert.That(snapshot.AsSpan((int)off, (int)len).ToArray(),
        Is.EqualTo(expected),
        $"original payload at {off}..+{len} survived the add cascade");
    }

    img.Position = 0;
    var r = new VdfsReader(img);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "alpha.txt", "beta.txt", "c.txt", "d.txt", "e.txt" }));
  }

  // ── Remove (true in-place) ────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Remove_LeavesRemainingDataByteIdenticalAtOriginalOffsets() {
    var keepA = "KEEP_AAA"u8.ToArray();
    var dropB = new byte[64];
    for (var i = 0; i < dropB.Length; i++) dropB[i] = (byte)(i * 7);
    var keepC = "KEEP_CCC"u8.ToArray();

    using var img = BuildImage(("a.txt", keepA), ("b.txt", dropB), ("c.txt", keepC));
    // Original data layout for survivors (a is before b, c is after b).
    var originalBytes = img.ToArray();
    var entrySize = 80;
    var dataStart = 36 + 3 * entrySize;
    var aOff = dataStart;
    var aBytes = originalBytes.AsSpan(aOff, keepA.Length).ToArray();
    var cOff = aOff + keepA.Length + dropB.Length;
    var cBytes = originalBytes.AsSpan(cOff, keepC.Length).ToArray();

    VdfsInPlaceModifier.RemoveFile(img, "b.txt", wipeData: true);

    var snapshot = img.ToArray();
    Assert.That(snapshot.AsSpan(aOff, keepA.Length).ToArray(), Is.EqualTo(aBytes),
      "kept a.txt data must remain byte-identical at original offset");
    Assert.That(snapshot.AsSpan(cOff, keepC.Length).ToArray(), Is.EqualTo(cBytes),
      "kept c.txt data must remain byte-identical at original offset");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Remove_LeavesSurvivingEntryRecordsByteIdentical() {
    var entrySize = 80;
    using var img = BuildImage(
      ("a.txt", "A"u8.ToArray()),
      ("b.txt", "B"u8.ToArray()),
      ("c.txt", "C"u8.ToArray()));
    var original = img.ToArray();
    var aRec = original.AsSpan(36, entrySize).ToArray();
    var cRec = original.AsSpan(36 + 2 * entrySize, entrySize).ToArray();

    VdfsInPlaceModifier.RemoveFile(img, "b.txt");

    var snapshot = img.ToArray();
    Assert.That(snapshot.AsSpan(36, entrySize).ToArray(), Is.EqualTo(aRec),
      "a.txt entry record must be untouched");
    Assert.That(snapshot.AsSpan(36 + 2 * entrySize, entrySize).ToArray(), Is.EqualTo(cRec),
      "c.txt entry record must be untouched");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Remove_DisappearsFromListing() {
    using var img = BuildImage(("a.txt", "A"u8.ToArray()), ("b.txt", "B"u8.ToArray()));
    var removed = VdfsInPlaceModifier.RemoveFile(img, "a.txt");
    Assert.That(removed, Is.True);

    img.Position = 0;
    var r = new VdfsReader(img);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "b.txt" }));
  }

  [Test, Category("HappyPath")]
  public void Remove_MissingEntry_ReturnsFalse() {
    using var img = BuildImage(("a.txt", "A"u8.ToArray()));
    var removed = VdfsInPlaceModifier.RemoveFile(img, "missing.bin");
    Assert.That(removed, Is.False);
  }

  [Test, Category("Security")]
  public void Remove_WipesDataExtent() {
    var marker = "VDFS_INPLACE_REMOVE_MARKER_4242"u8.ToArray();
    using var img = BuildImage(("secret.dat", marker), ("other.dat", "OTHER"u8.ToArray()));

    VdfsInPlaceModifier.RemoveFile(img, "secret.dat", wipeData: true);

    var bytes = img.ToArray();
    Assert.That(IndexOf(bytes, marker), Is.LessThan(0),
      "secret payload bytes must be wiped from the archive");
  }

  // ── Replace (in-place when it fits) ───────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Replace_Fits_RewritesAtOriginalOffset_LeavesOtherDataByteIdentical() {
    var entrySize = 80;
    var seedA = "ALPHA_ORIGINAL_"u8.ToArray(); // 15 bytes
    var seedB = "BETA_KEEP"u8.ToArray();
    using var img = BuildImage(("a.txt", seedA), ("b.txt", seedB));
    var original = img.ToArray();
    var dataStart = 36 + 2 * entrySize;
    var aOff = dataStart;
    var bOff = aOff + seedA.Length;
    var bBytes = original.AsSpan(bOff, seedB.Length).ToArray();

    var smallerA = "FITS"u8.ToArray(); // 4 bytes (< 15)
    var ok = VdfsInPlaceModifier.ReplaceFile(img, "a.txt", smallerA);
    Assert.That(ok, Is.True);

    var snap = img.ToArray();
    // Other file's data untouched.
    Assert.That(snap.AsSpan(bOff, seedB.Length).ToArray(), Is.EqualTo(bBytes),
      "neighbour file payload must be byte-identical after in-place replace");
    // a.txt's data now at the same offset, new bytes.
    Assert.That(snap.AsSpan(aOff, smallerA.Length).ToArray(), Is.EqualTo(smallerA),
      "replaced payload must live at the original extent offset");

    img.Position = 0;
    var r = new VdfsReader(img);
    var entry = r.Entries.First(e => e.Name == "a.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(smallerA));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Replace_TooLarge_FallsBackToRemoveAdd_StillRoundTrips() {
    using var img = BuildImage(("a.txt", "AAA"u8.ToArray()), ("b.txt", "BBB"u8.ToArray()));
    var big = new byte[500];
    new Random(7).NextBytes(big);

    var ok = VdfsInPlaceModifier.ReplaceFile(img, "a.txt", big);
    Assert.That(ok, Is.True);

    img.Position = 0;
    var r = new VdfsReader(img);
    var a = r.Entries.First(e => e.Name == "a.txt");
    var b = r.Entries.First(e => e.Name == "b.txt");
    Assert.That(r.Extract(a), Is.EqualTo(big));
    Assert.That(r.Extract(b), Is.EqualTo("BBB"u8.ToArray()));
  }

  // ── Mutate-then-extract round-trip ────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MutateThenExtract_RoundTrip() {
    using var img = BuildImage(
      ("keep.txt", "KEEP"u8.ToArray()),
      ("drop.txt", "DROPME"u8.ToArray()));

    VdfsInPlaceModifier.AddFile(img, "added.bin", "ADDED_BYTES"u8.ToArray());
    VdfsInPlaceModifier.RemoveFile(img, "drop.txt");
    VdfsInPlaceModifier.AddFile(img, "tail.dat", new byte[] { 1, 2, 3, 4, 5 });

    img.Position = 0;
    var r = new VdfsReader(img);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "keep.txt", "added.bin", "tail.dat" }));

    Assert.That(r.Extract(r.Entries.First(e => e.Name == "keep.txt")),
      Is.EqualTo("KEEP"u8.ToArray()));
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "added.bin")),
      Is.EqualTo("ADDED_BYTES"u8.ToArray()));
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "tail.dat")),
      Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
  }

  // ── Descriptor wiring (routes through VdfsInPlaceModifier, not rebuild) ──

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Add_UsesInPlaceModifier_PreservesOriginalDataBytes() {
    var seed = "DESCRIPTOR_SEED_BYTES"u8.ToArray();
    using var img = BuildImage(("seed.dat", seed));
    var original = img.ToArray();
    // The seed file's bytes live at offset 36 + 80 = 116.
    var seedOffset = 36 + 80;
    var seedSlice = original.AsSpan(seedOffset, seed.Length).ToArray();

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via descriptor"u8.ToArray());
      var d = new VdfsFormatDescriptor();
      ((IArchiveModifiable)d).Add(img,
        [new ArchiveInputInfo(tmp, "added.txt", false)]);

      var snap = img.ToArray();
      Assert.That(snap.AsSpan(seedOffset, seed.Length).ToArray(),
        Is.EqualTo(seedSlice),
        "Add through descriptor must preserve original data bytes at original offset");

      img.Position = 0;
      var entries = d.List(img, null);
      Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name),
        Has.Member("added.txt"));
      Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name),
        Has.Member("seed.dat"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Remove_UsesInPlaceModifier_PreservesNeighbourData() {
    var keep = "DESC_KEEP_BYTES"u8.ToArray();
    var drop = "DESC_DROP_BYTES"u8.ToArray();
    using var img = BuildImage(("keep.txt", keep), ("drop.txt", drop));
    var original = img.ToArray();
    var keepOffset = 36 + 2 * 80;
    var keepSlice = original.AsSpan(keepOffset, keep.Length).ToArray();

    var d = new VdfsFormatDescriptor();
    ((IArchiveModifiable)d).Remove(img, ["drop.txt"]);

    var snap = img.ToArray();
    Assert.That(snap.AsSpan(keepOffset, keep.Length).ToArray(),
      Is.EqualTo(keepSlice),
      "Remove through descriptor must leave neighbour file data byte-identical");

    img.Position = 0;
    var names = d.List(img, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "keep.txt" }));
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static int IndexOf(byte[] haystack, byte[] needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++) {
        if (haystack[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return i;
    }
    return -1;
  }
}
