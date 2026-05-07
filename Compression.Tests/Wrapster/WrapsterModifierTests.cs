#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Wrapster;

namespace Compression.Tests.Wrapster;

[TestFixture]
public class WrapsterModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeed();
    WrapsterModifier.AddFile(ms, "added.txt", "hello-wrap"u8.ToArray());

    ms.Position = 0;
    var r = new WrapsterReader(ms);
    var byName = r.Entries.ToDictionary(e => e.Name, e => r.Extract(e));
    Assert.That(byName["added.txt"], Is.EqualTo("hello-wrap"u8.ToArray()));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_ReplacesExistingByName() {
    var ms = BuildSeed();
    WrapsterModifier.AddFile(ms, "seed.txt", "replaced"u8.ToArray());

    ms.Position = 0;
    var r = new WrapsterReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("replaced"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeed();
    WrapsterModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    WrapsterModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(WrapsterModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var r = new WrapsterReader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Not.Contain("victim.txt"));
    Assert.That(names, Contains.Item("keeper.txt"));
    Assert.That(names, Contains.Item("seed.txt"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeed();
    Assert.That(WrapsterModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeed();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new WrapsterFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var r = new WrapsterReader(ms);
      var entry = r.Entries.Single(e => e.Name == "via-if.txt");
      Assert.That(r.Extract(entry), Is.EqualTo("via-if"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_DropsEntry() {
    var ms = BuildSeed();
    WrapsterModifier.AddFile(ms, "drop.txt", "x"u8.ToArray());
    ((IArchiveModifiable)new WrapsterFormatDescriptor()).Remove(ms, ["drop.txt"]);

    ms.Position = 0;
    var r = new WrapsterReader(ms);
    Assert.That(r.Entries.Select(e => e.Name), Does.Not.Contain("drop.txt"));
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static MemoryStream BuildSeed() {
    var ms = new MemoryStream();
    var w = new WrapsterWriter();
    w.AddFile("seed.txt", "seed-content"u8.ToArray());
    w.WriteTo(ms);
    ms.Position = 0;
    var copy = new MemoryStream();
    ms.CopyTo(copy);
    return copy;
  }
}
