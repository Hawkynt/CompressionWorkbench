#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Arj;

namespace Compression.Tests.Arj;

[TestFixture]
public class ArjModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedArj();
    ArjModifier.AddFile(ms, "added.txt", "hello-arj"u8.ToArray());
    ms.Position = 0;
    var reader = new ArjReader(ms);
    var entry = reader.Entries.Single(e => e.FileName == "added.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(entry)), Is.EqualTo("hello-arj"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingEntries() {
    var ms = BuildSeedArj();
    ArjModifier.AddFile(ms, "added.txt", "added-data"u8.ToArray());
    ms.Position = 0;
    var reader = new ArjReader(ms);
    var byName = reader.Entries.ToDictionary(e => e.FileName, e =>
      System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(e)));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"));
    Assert.That(byName["added.txt"], Is.EqualTo("added-data"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_RoundTrips() {
    var ms = BuildSeedArj();
    var data = new byte[5000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    ArjModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    var reader = new ArjReader(ms);
    var entry = reader.Entries.Single(e => e.FileName == "big.bin");
    Assert.That(reader.ExtractEntry(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedArj();
    ArjModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    ArjModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(ArjModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var reader = new ArjReader(ms);
    Assert.That(reader.Entries.Any(e => e.FileName == "victim.txt"), Is.False);
    Assert.That(reader.Entries.Any(e => e.FileName == "keeper.txt"), Is.True);
    Assert.That(reader.Entries.Any(e => e.FileName == "seed.txt"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedArj();
    Assert.That(ArjModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedArj();
    ArjModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    ArjModifier.RemoveFile(ms, "doc.txt");
    ArjModifier.AddFile(ms, "doc.txt", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    var reader = new ArjReader(ms);
    var matching = reader.Entries.Where(e => e.FileName == "doc.txt").ToList();
    Assert.That(matching, Has.Count.EqualTo(1));
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(matching[0])),
      Is.EqualTo("version-two-replacement"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedArj();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new ArjFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var reader = new ArjReader(ms);
      var entry = reader.Entries.Single(e => e.FileName == "via-if.txt");
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(entry)), Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  private static MemoryStream BuildSeedArj() {
    var ms = new MemoryStream();
    var w = new ArjWriter(ArjConstants.MethodStore);
    w.AddFile("seed.txt", "seed-content"u8.ToArray());
    w.WriteTo(ms);
    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    return copy;
  }
}
