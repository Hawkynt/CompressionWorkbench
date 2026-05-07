#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.IffCdaf;

namespace Compression.Tests.IffCdaf;

[TestFixture]
public class IffCdafModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeed();
    IffCdafModifier.AddFile(ms, "added.txt", "hello-cdaf"u8.ToArray());

    ms.Position = 0;
    var r = new IffCdafReader(ms);
    var byName = r.Entries.ToDictionary(e => e.Name, e => r.Extract(e));
    Assert.That(byName["added.txt"], Is.EqualTo("hello-cdaf"u8.ToArray()));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_OddSize_RoundTrips() {
    var ms = BuildSeed();
    var data = "ABC"u8.ToArray(); // odd size triggers IFF padding
    IffCdafModifier.AddFile(ms, "odd.txt", data);

    ms.Position = 0;
    var r = new IffCdafReader(ms);
    var entry = r.Entries.First(e => e.Name == "odd.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_RoundTrips() {
    var ms = BuildSeed();
    var data = new byte[5000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    IffCdafModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    var r = new IffCdafReader(ms);
    var big = r.Entries.First(e => e.Name == "big.bin");
    Assert.That(r.Extract(big), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeed();
    IffCdafModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    IffCdafModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(IffCdafModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var r = new IffCdafReader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Not.Contain("victim.txt"));
    Assert.That(names, Contains.Item("keeper.txt"));
    Assert.That(names, Contains.Item("seed.txt"));

    var byName = r.Entries.ToDictionary(e => e.Name, e => r.Extract(e));
    Assert.That(byName["keeper.txt"], Is.EqualTo("keep-me"u8.ToArray()));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeed();
    Assert.That(IffCdafModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeed();
    IffCdafModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    IffCdafModifier.RemoveFile(ms, "doc.txt");
    IffCdafModifier.AddFile(ms, "doc.txt", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    var r = new IffCdafReader(ms);
    var doc = r.Entries.Single(e => e.Name == "doc.txt");
    Assert.That(r.Extract(doc), Is.EqualTo("version-two-replacement"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeed();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new IffCdafFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var r = new IffCdafReader(ms);
      var entry = r.Entries.Single(e => e.Name == "via-if.txt");
      Assert.That(r.Extract(entry), Is.EqualTo("via-if"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_DropsEntry() {
    var ms = BuildSeed();
    IffCdafModifier.AddFile(ms, "drop.txt", "x"u8.ToArray());
    ((IArchiveModifiable)new IffCdafFormatDescriptor()).Remove(ms, ["drop.txt"]);

    ms.Position = 0;
    var r = new IffCdafReader(ms);
    Assert.That(r.Entries.Select(e => e.Name), Does.Not.Contain("drop.txt"));
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static MemoryStream BuildSeed() {
    var ms = new MemoryStream();
    var w = new IffCdafWriter();
    w.AddFile("seed.txt", "seed-content"u8.ToArray());
    w.WriteTo(ms);
    ms.Position = 0;
    var copy = new MemoryStream();
    ms.CopyTo(copy);
    return copy;
  }
}
