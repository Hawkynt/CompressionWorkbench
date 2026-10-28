using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Vdfs;

namespace Compression.Tests.Vdfs;

[TestFixture]
public class VdfsInPlaceModifyTests {

  /// <summary>An expandable seekable stream holding a volume of <paramref name="files" />.</summary>
  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) => BuildImage(false, files);

  private static MemoryStream BuildImage(bool zipped, params (string Name, byte[] Data)[] files) {
    var w = new VdfsWriter { Zipped = zipped };
    foreach (var (n, d) in files) w.AddFile(n, d);
    var bytes = w.Build();
    var ms = new MemoryStream();
    ms.Write(bytes);
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, VdfsEntry> Files(MemoryStream img, out VdfsReader reader) {
    img.Position = 0;
    reader = new VdfsReader(img, leaveOpen: true);
    return reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
  }

  private static Dictionary<string, (long Offset, long Stored)> Extents(MemoryStream img) {
    img.Position = 0;
    return new VdfsFormatDescriptor().EnumerateExtents(img)
      .Where(e => e.Kind == DefragBlockKind.Used)
      .ToDictionary(e => e.FileName!, e => (e.Offset, e.Length));
  }

  private static byte[] Filled(int length, byte value) {
    var data = new byte[length];
    Array.Fill(data, value);
    return data;
  }

  // ── Add ──────────────────────────────────────────────────────────────────

  [Test, Category("InPlace")]
  public void Add_PayloadsBeyondTheGrownCatalog_KeepOffsetAndBytes() {
    // Given A (100 bytes) right behind the catalog and B behind A
    using var img = BuildImage(("a.bin", Filled(100, 0xAA)), ("b.bin", Filled(300, 0xBB)));
    var before = Extents(img);
    var bBytes = img.ToArray().AsSpan((int)before["B.BIN"].Offset, 300).ToArray();

    // When a file is added, growing the catalog by one 80-byte entry into A's first bytes
    VdfsInPlaceModifier.AddFile(img, "c.bin", Filled(10, 0xCC));

    // Then B, which the catalog does not reach, keeps its offset and bytes
    var after = Extents(img);
    Assert.That(after["B.BIN"].Offset, Is.EqualTo(before["B.BIN"].Offset));
    Assert.That(img.ToArray().AsSpan((int)after["B.BIN"].Offset, 300).ToArray(), Is.EqualTo(bBytes));

    // And A has moved out of the catalog's way, to the end, intact
    Assert.That(after["A.BIN"].Offset, Is.GreaterThan(before["B.BIN"].Offset));
    var files = Files(img, out var r);
    using (r) Assert.That(r.Extract(files["A.BIN"]), Is.EqualTo(Filled(100, 0xAA)));
  }

  [Test, Category("InPlace")]
  public void Add_CatalogStaysAt296_HeaderCountsFollow() {
    using var img = BuildImage(("a.txt", "A"u8.ToArray()));
    VdfsInPlaceModifier.AddFile(img, "dir/b.txt", "BB"u8.ToArray());

    var bytes = img.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(288)), Is.EqualTo(296u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(272)), Is.EqualTo(3u), "DIR, A.TXT, B.TXT");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(276)), Is.EqualTo(2u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(284)), Is.EqualTo(3u));
  }

  [Test, Category("InPlace")]
  public void Add_RelocatedPayload_LeavesNoStaleCopy() {
    // Given a marker payload right behind the catalog, longer than one entry
    var marker = "VDFS_RELOCATION_MARKER_0123456789_VDFS_RELOCATION_MARKER_0123456789_VDFS_RELOCATION_MARKER"u8.ToArray();
    using var img = BuildImage(("a.bin", marker));

    // When an add pushes it to the end
    VdfsInPlaceModifier.AddFile(img, "b.bin", [1]);

    // Then exactly one copy of it remains
    Assert.That(Occurrences(img.ToArray(), marker), Is.EqualTo(1));
  }

  [Test, Category("InPlace")]
  public void Add_NewFileRoundTripsThroughReader() {
    using var img = BuildImage(("seed.txt", "SEED"u8.ToArray()));
    var payload = new byte[1024];
    new Random(123).NextBytes(payload);

    VdfsInPlaceModifier.AddFile(img, "added.bin", payload);

    var files = Files(img, out var r);
    using (r) {
      Assert.That(files.Keys, Is.EquivalentTo(new[] { "SEED.TXT", "ADDED.BIN" }));
      Assert.That(r.Extract(files["ADDED.BIN"]), Is.EqualTo(payload));
      Assert.That(r.Extract(files["SEED.TXT"]), Is.EqualTo("SEED"u8.ToArray()));
      Assert.That(r.IsPartial, Is.False);
    }
  }

  [Test, Category("InPlace")]
  public void Add_MultipleAdds_AllReadable() {
    using var img = BuildImage(("alpha.txt", "ALPHA"u8.ToArray()), ("beta.txt", "BETA"u8.ToArray()));
    VdfsInPlaceModifier.AddFile(img, "c.txt", "CCC"u8.ToArray());
    VdfsInPlaceModifier.AddFile(img, "sub/d.txt", "DDDD"u8.ToArray());
    VdfsInPlaceModifier.AddFile(img, "sub/deeper/e.txt", "EEEEE"u8.ToArray());

    var files = Files(img, out var r);
    using (r) {
      Assert.That(files.Keys, Is.EquivalentTo(new[] { "ALPHA.TXT", "BETA.TXT", "C.TXT", "SUB/D.TXT", "SUB/DEEPER/E.TXT" }));
      Assert.That(r.Extract(files["ALPHA.TXT"]), Is.EqualTo("ALPHA"u8.ToArray()));
      Assert.That(r.Extract(files["SUB/DEEPER/E.TXT"]), Is.EqualTo("EEEEE"u8.ToArray()));
    }
  }

  [Test, Category("InPlace")]
  public void Add_ToZippedVolume_ZipsNewPayloads_ButNotSound() {
    using var img = BuildImage(true, ("seed.bin", Filled(4000, 1)));
    VdfsInPlaceModifier.AddFile(img, "more.bin", Filled(9000, 2));
    VdfsInPlaceModifier.AddFile(img, "theme.wav", Filled(500, 3));

    var files = Files(img, out var r);
    using (r) {
      Assert.That(files["MORE.BIN"].IsZipped, Is.True);
      Assert.That(files["THEME.WAV"].IsZipped, Is.False);
      Assert.That(r.Extract(files["MORE.BIN"]), Is.EqualTo(Filled(9000, 2)));
      Assert.That(r.Extract(files["THEME.WAV"]), Is.EqualTo(Filled(500, 3)));
      Assert.That(r.Extract(files["SEED.BIN"]), Is.EqualTo(Filled(4000, 1)));
    }
  }

  // ── Remove ───────────────────────────────────────────────────────────────

  [Test, Category("InPlace")]
  public void Remove_SurvivorsKeepOffsetsAndBytes() {
    using var img = BuildImage(("a.txt", "KEEP_AAA"u8.ToArray()), ("b.txt", Filled(64, 7)), ("c.txt", "KEEP_CCC"u8.ToArray()));
    var before = Extents(img);
    var snapshot = img.ToArray();

    VdfsInPlaceModifier.RemoveFile(img, "b.txt");

    var after = Extents(img);
    var now = img.ToArray();
    foreach (var name in new[] { "A.TXT", "C.TXT" }) {
      Assert.That(after[name], Is.EqualTo(before[name]), $"{name} moved");
      var (offset, length) = before[name];
      Assert.That(now.AsSpan((int)offset, (int)length).ToArray(), Is.EqualTo(snapshot.AsSpan((int)offset, (int)length).ToArray()));
    }
  }

  [Test, Category("InPlace")]
  public void Remove_DisappearsFromListing_AndTheOldCatalogTailIsZeroed() {
    using var img = BuildImage(("a.txt", "A"u8.ToArray()), ("b.txt", "B"u8.ToArray()));
    Assert.That(VdfsInPlaceModifier.RemoveFile(img, "a.txt"), Is.True);

    var files = Files(img, out var r);
    using (r) Assert.That(files.Keys, Is.EquivalentTo(new[] { "B.TXT" }));

    // The second entry slot held the old catalog; nothing of it may remain to be read back
    var bytes = img.ToArray();
    Assert.That(bytes.AsSpan(296 + 80, 80).ToArray(), Is.All.EqualTo(0));
  }

  [Test, Category("InPlace")]
  public void Remove_Directory_RemovesEverythingUnderIt() {
    using var img = BuildImage(("dir/a.txt", "A"u8.ToArray()), ("dir/sub/b.txt", "B"u8.ToArray()), ("c.txt", "C"u8.ToArray()));
    Assert.That(VdfsInPlaceModifier.RemoveFile(img, "DIR"), Is.True);

    img.Position = 0;
    using var r = new VdfsReader(img, leaveOpen: true);
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "C.TXT" }));
  }

  [Test, Category("InPlace")]
  public void Remove_MissingEntry_ReturnsFalse_AndChangesNothing() {
    using var img = BuildImage(("a.txt", "A"u8.ToArray()));
    var before = img.ToArray();
    Assert.That(VdfsInPlaceModifier.RemoveFile(img, "missing.bin"), Is.False);
    Assert.That(img.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("Security")]
  public void Remove_WipesDataExtent() {
    var marker = "VDFS_INPLACE_REMOVE_MARKER_4242"u8.ToArray();
    using var img = BuildImage(("secret.dat", marker), ("other.dat", "OTHER"u8.ToArray()));
    VdfsInPlaceModifier.RemoveFile(img, "secret.dat", wipeData: true);
    Assert.That(Occurrences(img.ToArray(), marker), Is.EqualTo(0));
  }

  // ── Replace ──────────────────────────────────────────────────────────────

  [Test, Category("InPlace")]
  public void Replace_Fits_RewritesAtOriginalOffset_LeavesOthersAlone() {
    using var img = BuildImage(("a.txt", "ALPHA_ORIGINAL_"u8.ToArray()), ("b.txt", "BETA_KEEP"u8.ToArray()));
    var before = Extents(img);

    Assert.That(VdfsInPlaceModifier.ReplaceFile(img, "a.txt", "FITS"u8.ToArray()), Is.True);

    var after = Extents(img);
    Assert.That(after["A.TXT"].Offset, Is.EqualTo(before["A.TXT"].Offset));
    Assert.That(after["A.TXT"].Stored, Is.EqualTo(4));
    Assert.That(after["B.TXT"], Is.EqualTo(before["B.TXT"]));
    var files = Files(img, out var r);
    using (r) {
      Assert.That(r.Extract(files["A.TXT"]), Is.EqualTo("FITS"u8.ToArray()));
      Assert.That(r.Extract(files["B.TXT"]), Is.EqualTo("BETA_KEEP"u8.ToArray()));
    }
  }

  [TestCase(false)]
  [TestCase(true)]
  [Category("InPlace")]
  public void Replace_TooLarge_MovesToTheEnd_StillRoundTrips(bool zipped) {
    using var img = BuildImage(zipped, ("a.txt", "AAA"u8.ToArray()), ("b.txt", "BBB"u8.ToArray()));
    var big = new byte[50000];
    new Random(7).NextBytes(big);

    Assert.That(VdfsInPlaceModifier.ReplaceFile(img, "a.txt", big), Is.True);

    var files = Files(img, out var r);
    using (r) {
      Assert.That(r.Extract(files["A.TXT"]), Is.EqualTo(big));
      Assert.That(r.Extract(files["B.TXT"]), Is.EqualTo("BBB"u8.ToArray()));
    }
  }

  // ── Mixed ────────────────────────────────────────────────────────────────

  [Test, Category("InPlace")]
  public void MutateThenExtract_RoundTrip() {
    using var img = BuildImage(("keep.txt", "KEEP"u8.ToArray()), ("drop.txt", "DROPME"u8.ToArray()));

    VdfsInPlaceModifier.AddFile(img, "added.bin", "ADDED_BYTES"u8.ToArray());
    VdfsInPlaceModifier.RemoveFile(img, "drop.txt");
    VdfsInPlaceModifier.AddFile(img, "tail.dat", [1, 2, 3, 4, 5]);

    var files = Files(img, out var r);
    using (r) {
      Assert.That(files.Keys, Is.EquivalentTo(new[] { "KEEP.TXT", "ADDED.BIN", "TAIL.DAT" }));
      Assert.That(r.Extract(files["KEEP.TXT"]), Is.EqualTo("KEEP"u8.ToArray()));
      Assert.That(r.Extract(files["ADDED.BIN"]), Is.EqualTo("ADDED_BYTES"u8.ToArray()));
      Assert.That(r.Extract(files["TAIL.DAT"]), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
    }
  }

  [Test, Category("InPlace")]
  public void Descriptor_Remove_PreservesNeighbourData() {
    using var img = BuildImage(("keep.txt", "DESC_KEEP_BYTES"u8.ToArray()), ("drop.txt", "DESC_DROP_BYTES"u8.ToArray()));
    var before = Extents(img);
    var keep = img.ToArray().AsSpan((int)before["KEEP.TXT"].Offset, 15).ToArray();

    new VdfsFormatDescriptor().Remove(img, ["drop.txt"]);

    var after = Extents(img);
    Assert.That(after["KEEP.TXT"], Is.EqualTo(before["KEEP.TXT"]));
    Assert.That(img.ToArray().AsSpan((int)after["KEEP.TXT"].Offset, 15).ToArray(), Is.EqualTo(keep));
    img.Position = 0;
    Assert.That(new VdfsFormatDescriptor().List(img, null).Select(e => e.Name), Is.EquivalentTo(new[] { "KEEP.TXT" }));
  }

  private static int Occurrences(byte[] haystack, byte[] needle) {
    var count = 0;
    for (var i = 0; i + needle.Length <= haystack.Length; i++)
      if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) ++count;
    return count;
  }
}
