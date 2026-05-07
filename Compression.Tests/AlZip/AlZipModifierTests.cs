#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.AlZip;

namespace Compression.Tests.AlZip;

[TestFixture]
public class AlZipModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedAlz();
    AlZipModifier.AddFile(ms, "added.txt", "hello-alz"u8.ToArray());

    ms.Position = 0;
    using var r = new AlZipReader(ms, leaveOpen: true);
    var byName = r.Entries.ToDictionary(e => e.FileName, e => r.Extract(e));
    Assert.That(byName["added.txt"], Is.EqualTo("hello-alz"u8.ToArray()));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_RoundTrips() {
    var ms = BuildSeedAlz();
    var data = new byte[5000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    AlZipModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    using var r = new AlZipReader(ms, leaveOpen: true);
    var big = r.Entries.First(e => e.FileName == "big.bin");
    Assert.That(r.Extract(big), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedAlz();
    AlZipModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    AlZipModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(AlZipModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    using var r = new AlZipReader(ms, leaveOpen: true);
    var names = r.Entries.Select(e => e.FileName).ToHashSet();
    Assert.That(names, Does.Not.Contain("victim.txt"));
    Assert.That(names, Contains.Item("keeper.txt"));
    Assert.That(names, Contains.Item("seed.txt"));

    var byName = r.Entries.ToDictionary(e => e.FileName, e => r.Extract(e));
    Assert.That(byName["keeper.txt"], Is.EqualTo("keep-me"u8.ToArray()));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedAlz();
    Assert.That(AlZipModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedAlz();
    AlZipModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    AlZipModifier.RemoveFile(ms, "doc.txt");
    AlZipModifier.AddFile(ms, "doc.txt", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    using var r = new AlZipReader(ms, leaveOpen: true);
    var doc = r.Entries.Single(e => e.FileName == "doc.txt");
    Assert.That(r.Extract(doc), Is.EqualTo("version-two-replacement"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedAlz();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new AlZipFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      using var r = new AlZipReader(ms, leaveOpen: true);
      var entry = r.Entries.Single(e => e.FileName == "via-if.txt");
      Assert.That(r.Extract(entry), Is.EqualTo("via-if"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_DropsEntry() {
    var ms = BuildSeedAlz();
    AlZipModifier.AddFile(ms, "drop.txt", "x"u8.ToArray());
    ((IArchiveModifiable)new AlZipFormatDescriptor()).Remove(ms, ["drop.txt"]);

    ms.Position = 0;
    using var r = new AlZipReader(ms, leaveOpen: true);
    Assert.That(r.Entries.Select(e => e.FileName), Does.Not.Contain("drop.txt"));
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedAlz() {
    var ms = new MemoryStream();
    using (var w = new AlZipWriter(ms, leaveOpen: true))
      w.AddFile("seed.txt", "seed-content"u8.ToArray());
    ms.Position = 0;
    var copy = new MemoryStream();
    ms.CopyTo(copy);
    return copy;
  }
}
