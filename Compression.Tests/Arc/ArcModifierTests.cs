#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Arc;

namespace Compression.Tests.Arc;

[TestFixture]
public class ArcModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedArc();
    ArcModifier.AddFile(ms, "added.txt", "hello-arc"u8.ToArray());
    ms.Position = 0;
    var entries = ReadAll(ms);
    var added = entries.Single(e => e.Name == "added.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(added.Data), Is.EqualTo("hello-arc"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingEntries() {
    var ms = BuildSeedArc();
    ArcModifier.AddFile(ms, "added.txt", "added-data"u8.ToArray());
    ms.Position = 0;
    var byName = ReadAll(ms).ToDictionary(e => e.Name, e => System.Text.Encoding.ASCII.GetString(e.Data));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"));
    Assert.That(byName["added.txt"], Is.EqualTo("added-data"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_RoundTrips() {
    var ms = BuildSeedArc();
    var data = new byte[5000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    ArcModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    var entries = ReadAll(ms);
    var big = entries.Single(e => e.Name == "big.bin");
    Assert.That(big.Data, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedArc();
    ArcModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    ArcModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(ArcModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var names = ReadAll(ms).Select(e => e.Name).ToList();
    Assert.That(names, Does.Not.Contain("victim.txt"));
    Assert.That(names, Does.Contain("keeper.txt"));
    Assert.That(names, Does.Contain("seed.txt"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedArc();
    Assert.That(ArcModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedArc();
    ArcModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    ArcModifier.RemoveFile(ms, "doc.txt");
    ArcModifier.AddFile(ms, "doc.txt", "version-two"u8.ToArray());

    ms.Position = 0;
    var matching = ReadAll(ms).Where(e => e.Name == "doc.txt").ToList();
    Assert.That(matching, Has.Count.EqualTo(1));
    Assert.That(System.Text.Encoding.ASCII.GetString(matching[0].Data), Is.EqualTo("version-two"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedArc();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new ArcFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var entry = ReadAll(ms).Single(e => e.Name == "via-if.txt");
      Assert.That(System.Text.Encoding.ASCII.GetString(entry.Data), Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  // ── Helpers ─────────────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedArc() {
    var ms = new MemoryStream();
    using (var w = new ArcWriter(ms, ArcCompressionMethod.Stored, leaveOpen: true))
      w.AddEntry("seed.txt", "seed-content"u8.ToArray());
    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    return copy;
  }

  private static List<(string Name, byte[] Data)> ReadAll(Stream s) {
    using var reader = new ArcReader(s, leaveOpen: true);
    var result = new List<(string, byte[])>();
    while (reader.GetNextEntry() is { } e)
      result.Add((e.FileName, reader.ReadEntryData()));
    return result;
  }
}
