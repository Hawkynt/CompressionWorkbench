#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Lbr;

namespace Compression.Tests.Lbr;

[TestFixture]
public class LbrModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeed();
    LbrModifier.AddFile(ms, "ADDED.TXT", "hello-lbr"u8.ToArray());

    ms.Position = 0;
    var r = new LbrReader(ms);
    var byName = r.Entries.ToDictionary(e => e.FileName);
    Assert.That(byName.ContainsKey("ADDED.TXT"));
    Assert.That(byName.ContainsKey("SEED.TXT"));
    Assert.That(r.Extract(byName["ADDED.TXT"]).AsSpan(0, 9).ToArray(),
      Is.EqualTo("hello-lbr"u8.ToArray()));
    Assert.That(r.Extract(byName["SEED.TXT"]).AsSpan(0, 12).ToArray(),
      Is.EqualTo("seed-content"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LowercaseName_StoredUppercase() {
    var ms = BuildSeed();
    LbrModifier.AddFile(ms, "lower.txt", "x"u8.ToArray());

    ms.Position = 0;
    var r = new LbrReader(ms);
    Assert.That(r.Entries.Select(e => e.FileName), Contains.Item("LOWER.TXT"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_RoundTrips() {
    var ms = BuildSeed();
    var data = new byte[2000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    LbrModifier.AddFile(ms, "BIG.BIN", data);

    ms.Position = 0;
    var r = new LbrReader(ms);
    var big = r.Entries.First(e => e.FileName == "BIG.BIN");
    Assert.That(r.Extract(big).AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeed();
    LbrModifier.AddFile(ms, "VICTIM.TXT", "delete-me"u8.ToArray());
    LbrModifier.AddFile(ms, "KEEPER.TXT", "keep-me"u8.ToArray());
    Assert.That(LbrModifier.RemoveFile(ms, "VICTIM.TXT"), Is.True);

    ms.Position = 0;
    var r = new LbrReader(ms);
    var names = r.Entries.Select(e => e.FileName).ToHashSet();
    Assert.That(names, Does.Not.Contain("VICTIM.TXT"));
    Assert.That(names, Contains.Item("KEEPER.TXT"));
    Assert.That(names, Contains.Item("SEED.TXT"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeed();
    Assert.That(LbrModifier.RemoveFile(ms, "GHOST.TXT"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeed();
    LbrModifier.AddFile(ms, "DOC.TXT", "version-one"u8.ToArray());
    LbrModifier.RemoveFile(ms, "DOC.TXT");
    LbrModifier.AddFile(ms, "DOC.TXT", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    var r = new LbrReader(ms);
    var doc = r.Entries.Single(e => e.FileName == "DOC.TXT");
    Assert.That(r.Extract(doc).AsSpan(0, "version-two-replacement"u8.Length).ToArray(),
      Is.EqualTo("version-two-replacement"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_MarksSlotDeleted_AndReusable() {
    var ms = BuildSeed();
    LbrModifier.AddFile(ms, "FIRST.TXT", "a"u8.ToArray());
    LbrModifier.RemoveFile(ms, "FIRST.TXT");
    // After removal there must be a free slot we can reuse.
    LbrModifier.AddFile(ms, "REUSED.TXT", "b"u8.ToArray());

    ms.Position = 0;
    var r = new LbrReader(ms);
    Assert.That(r.Entries.Select(e => e.FileName), Contains.Item("REUSED.TXT"));
    Assert.That(r.Entries.Select(e => e.FileName), Does.Not.Contain("FIRST.TXT"));
  }

  [Test, Category("ErrorHandling")]
  public void AddFile_DirectoryFull_Throws() {
    // The default writer reserves dirSectors = ceil((1+1)*32/128) = 1 sector
    // = 4 directory slots when seeded with one file. After the seed (slot 1
    // taken, slots 2 and 3 free), we can add 2 more files; the 3rd should
    // throw because no free slot remains.
    var ms = BuildSeed();
    LbrModifier.AddFile(ms, "F1.TXT", "x"u8.ToArray());
    LbrModifier.AddFile(ms, "F2.TXT", "y"u8.ToArray());
    Assert.Throws<InvalidOperationException>(() =>
      LbrModifier.AddFile(ms, "F3.TXT", "z"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeed();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new LbrFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIAIF.TXT", false)]);

      ms.Position = 0;
      var r = new LbrReader(ms);
      var entry = r.Entries.Single(e => e.FileName == "VIAIF.TXT");
      Assert.That(r.Extract(entry).AsSpan(0, 6).ToArray(), Is.EqualTo("via-if"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_DropsEntry() {
    var ms = BuildSeed();
    LbrModifier.AddFile(ms, "DROP.TXT", "x"u8.ToArray());
    ((IArchiveModifiable)new LbrFormatDescriptor()).Remove(ms, ["DROP.TXT"]);

    ms.Position = 0;
    var r = new LbrReader(ms);
    Assert.That(r.Entries.Select(e => e.FileName), Does.Not.Contain("DROP.TXT"));
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static MemoryStream BuildSeed() {
    var ms = new MemoryStream();
    using (var w = new LbrWriter(ms, leaveOpen: true))
      w.AddFile("SEED.TXT", "seed-content"u8.ToArray());
    ms.Position = 0;
    var copy = new MemoryStream();
    ms.CopyTo(copy);
    return copy;
  }
}
