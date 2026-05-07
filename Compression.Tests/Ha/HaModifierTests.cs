#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Ha;

namespace Compression.Tests.Ha;

[TestFixture]
public class HaModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedHa();
    HaModifier.AddFile(ms, "added.txt", "hello-ha"u8.ToArray());
    ms.Position = 0;
    var reader = new HaReader(ms);
    var entry = reader.Entries.Single(e => e.FileName == "added.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("hello-ha"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingEntries() {
    var ms = BuildSeedHa();
    HaModifier.AddFile(ms, "added.txt", "added-data"u8.ToArray());
    ms.Position = 0;
    var reader = new HaReader(ms);
    var byName = reader.Entries.ToDictionary(e => e.FileName, e =>
      System.Text.Encoding.ASCII.GetString(reader.Extract(e)));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"));
    Assert.That(byName["added.txt"], Is.EqualTo("added-data"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_KeepsHaMagic() {
    var ms = BuildSeedHa();
    HaModifier.AddFile(ms, "added.txt", "x"u8.ToArray());
    ms.Position = 0;
    Assert.That(ms.ReadByte(), Is.EqualTo(0x48));
    Assert.That(ms.ReadByte(), Is.EqualTo(0x41));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleEntriesAppendInOrder() {
    var ms = BuildSeedHa();
    HaModifier.AddFile(ms, "a.txt", "AAA"u8.ToArray());
    HaModifier.AddFile(ms, "b.txt", "BB"u8.ToArray());
    HaModifier.AddFile(ms, "c.txt", "CCCC"u8.ToArray());

    ms.Position = 0;
    var reader = new HaReader(ms);
    var names = reader.Entries.Select(e => e.FileName).ToArray();
    Assert.That(names, Is.EqualTo(new[] { "seed.txt", "a.txt", "b.txt", "c.txt" }));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedHa();
    HaModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    HaModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(HaModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var reader = new HaReader(ms);
    Assert.That(reader.Entries.Any(e => e.FileName == "victim.txt"), Is.False);
    Assert.That(reader.Entries.Any(e => e.FileName == "keeper.txt"), Is.True);
    Assert.That(reader.Entries.Any(e => e.FileName == "seed.txt"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_KeepsHaMagic() {
    var ms = BuildSeedHa();
    HaModifier.AddFile(ms, "a.txt", "AAA"u8.ToArray());
    Assert.That(HaModifier.RemoveFile(ms, "seed.txt"), Is.True);

    ms.Position = 0;
    Assert.That(ms.ReadByte(), Is.EqualTo(0x48));
    Assert.That(ms.ReadByte(), Is.EqualTo(0x41));
    ms.Position = 0;
    var reader = new HaReader(ms);
    Assert.That(reader.Entries.Select(e => e.FileName), Is.EqualTo(new[] { "a.txt" }));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedHa();
    Assert.That(HaModifier.RemoveFile(ms, "ghost.txt"), Is.False);
    // Archive is unchanged.
    ms.Position = 0;
    var reader = new HaReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("seed.txt"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_LastEntry_TruncatesStream() {
    var ms = BuildSeedHa();
    HaModifier.AddFile(ms, "tail.txt", "tail-data"u8.ToArray());
    var lengthWithTail = ms.Length;
    Assert.That(HaModifier.RemoveFile(ms, "tail.txt"), Is.True);
    Assert.That(ms.Length, Is.LessThan(lengthWithTail));

    ms.Position = 0;
    var reader = new HaReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("seed.txt"));
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedHa();
    HaModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    HaModifier.RemoveFile(ms, "doc.txt");
    HaModifier.AddFile(ms, "doc.txt", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    var reader = new HaReader(ms);
    var matching = reader.Entries.Where(e => e.FileName == "doc.txt").ToList();
    Assert.That(matching, Has.Count.EqualTo(1));
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(matching[0])),
      Is.EqualTo("version-two-replacement"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedHa();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new HaFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var reader = new HaReader(ms);
      var entry = reader.Entries.Single(e => e.FileName == "via-if.txt");
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.Extract(entry)), Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_DropsEntry() {
    var ms = BuildSeedHa();
    HaModifier.AddFile(ms, "drop.txt", "bye"u8.ToArray());
    ((IArchiveModifiable)new HaFormatDescriptor()).Remove(ms, ["drop.txt"]);

    ms.Position = 0;
    var reader = new HaReader(ms);
    Assert.That(reader.Entries.Any(e => e.FileName == "drop.txt"), Is.False);
    Assert.That(reader.Entries.Any(e => e.FileName == "seed.txt"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AdvertisesCanModify() {
    var d = new HaFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  private static MemoryStream BuildSeedHa() {
    var ms = new MemoryStream();
    using (var w = new HaWriter(ms, leaveOpen: true))
      w.AddFile("seed.txt", "seed-content"u8.ToArray());
    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    return copy;
  }
}
