#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Xar;

namespace Compression.Tests.Xar;

[TestFixture]
public class XarModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedXar();
    XarModifier.AddFile(ms, "greet.txt", "hello-xar"u8.ToArray());

    ms.Position = 0;
    var reader = new XarReader(ms);
    var entry = reader.Entries.Single(e => e.FileName == "greet.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("hello-xar"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingEntries() {
    var ms = BuildSeedXar();
    XarModifier.AddFile(ms, "added.txt", "added-data"u8.ToArray());

    ms.Position = 0;
    var reader = new XarReader(ms);
    var byName = reader.Entries.ToDictionary(e => e.FileName, e =>
      System.Text.Encoding.ASCII.GetString(reader.Extract(e)));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"));
    Assert.That(byName["added.txt"], Is.EqualTo("added-data"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_TwoInARow_BothRoundTrip() {
    var ms = BuildSeedXar();
    XarModifier.AddFile(ms, "first.txt", "FIRST"u8.ToArray());
    XarModifier.AddFile(ms, "second.txt", "SECOND-LONGER"u8.ToArray());

    ms.Position = 0;
    var reader = new XarReader(ms);
    var byName = reader.Entries.ToDictionary(e => e.FileName, e =>
      System.Text.Encoding.ASCII.GetString(reader.Extract(e)));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"));
    Assert.That(byName["first.txt"], Is.EqualTo("FIRST"));
    Assert.That(byName["second.txt"], Is.EqualTo("SECOND-LONGER"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_RoundTrips() {
    var ms = BuildSeedXar();
    var data = new byte[6000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 17 + 3) & 0xFF);
    XarModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    var reader = new XarReader(ms);
    var entry = reader.Entries.Single(e => e.FileName == "big.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(data));
    // Existing seed must still be intact (heap was shifted around it).
    var seed = reader.Entries.Single(e => e.FileName == "seed.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(seed)), Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedXar();
    XarModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    XarModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());

    Assert.That(XarModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var reader = new XarReader(ms);
    Assert.That(reader.Entries.Any(e => e.FileName == "victim.txt"), Is.False);
    var keeper = reader.Entries.Single(e => e.FileName == "keeper.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(keeper)), Is.EqualTo("keep-me"));
    var seed = reader.Entries.Single(e => e.FileName == "seed.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(seed)), Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedXar();
    Assert.That(XarModifier.RemoveFile(ms, "ghost.txt"), Is.False);

    // Archive still readable, seed intact.
    ms.Position = 0;
    var reader = new XarReader(ms);
    var seed = reader.Entries.Single(e => e.FileName == "seed.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(seed)), Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedXar();
    XarModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    XarModifier.RemoveFile(ms, "doc.txt");
    XarModifier.AddFile(ms, "doc.txt", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    var reader = new XarReader(ms);
    var matching = reader.Entries.Where(e => e.FileName == "doc.txt").ToList();
    Assert.That(matching, Has.Count.EqualTo(1));
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(matching[0])),
      Is.EqualTo("version-two-replacement"));
  }

  [Test, Category("RoundTrip")]
  public void AddRemove_OnFreshArchive_LeavesOnlySeed() {
    var ms = BuildSeedXar();
    XarModifier.AddFile(ms, "tmp.txt", "TEMPORARY"u8.ToArray());
    Assert.That(XarModifier.RemoveFile(ms, "tmp.txt"), Is.True);

    ms.Position = 0;
    var reader = new XarReader(ms);
    Assert.That(reader.Entries.Select(e => e.FileName), Is.EquivalentTo(new[] { "seed.txt" }));
    var seed = reader.Entries.Single(e => e.FileName == "seed.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(seed)), Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedXar();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new XarFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var reader = new XarReader(ms);
      var entry = reader.Entries.Single(e => e.FileName == "via-if.txt");
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_DropsEntry() {
    var ms = BuildSeedXar();
    XarModifier.AddFile(ms, "drop.txt", "drop-me"u8.ToArray());
    XarModifier.AddFile(ms, "stay.txt", "stay-here"u8.ToArray());

    ((IArchiveModifiable)new XarFormatDescriptor()).Remove(ms, ["drop.txt"]);

    ms.Position = 0;
    var reader = new XarReader(ms);
    Assert.That(reader.Entries.Any(e => e.FileName == "drop.txt"), Is.False);
    var stay = reader.Entries.Single(e => e.FileName == "stay.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(stay)), Is.EqualTo("stay-here"));
  }

  [Test, Category("RoundTrip")]
  public void HeaderRemainsValidXarMagic() {
    var ms = BuildSeedXar();
    XarModifier.AddFile(ms, "x.txt", "x"u8.ToArray());
    XarModifier.RemoveFile(ms, "seed.txt");

    ms.Position = 0;
    Assert.That(ms.ReadByte(), Is.EqualTo(0x78));
    Assert.That(ms.ReadByte(), Is.EqualTo(0x61));
    Assert.That(ms.ReadByte(), Is.EqualTo(0x72));
    Assert.That(ms.ReadByte(), Is.EqualTo(0x21));
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedXar() {
    var ms = new MemoryStream();
    using (var w = new XarWriter(ms, leaveOpen: true))
      w.AddFile("seed.txt", "seed-content"u8.ToArray());
    return ms;
  }
}
