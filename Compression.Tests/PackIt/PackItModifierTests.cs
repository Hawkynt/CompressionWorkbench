#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.PackIt;

namespace Compression.Tests.PackIt;

[TestFixture]
public class PackItModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedPit();
    PackItModifier.AddFile(ms, "added.txt", "hello-pit"u8.ToArray());
    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries["added.txt"], Is.EqualTo("hello-pit"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_RoundTrips() {
    var ms = BuildSeedPit();
    var data = new byte[5000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    PackItModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    using var reader = new PackItReader(ms, leaveOpen: true);
    var big = reader.Entries.Single(e => e.Name == "big.bin");
    var got = reader.Extract(big);
    Assert.That(got, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedPit();
    PackItModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    PackItModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(PackItModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries.ContainsKey("victim.txt"), Is.False);
    Assert.That(entries["keeper.txt"], Is.EqualTo("keep-me"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedPit();
    Assert.That(PackItModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedPit();
    PackItModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    PackItModifier.RemoveFile(ms, "doc.txt");
    PackItModifier.AddFile(ms, "doc.txt", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries["doc.txt"], Is.EqualTo("version-two-replacement"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedPit();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new PackItFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var entries = ReadAll(ms);
      Assert.That(entries["via-if.txt"], Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  // ── Helpers ──────────────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedPit() {
    var ms = new MemoryStream();
    using (var w = new PackItWriter(ms, leaveOpen: true))
      w.AddFile("seed.txt", "seed-content"u8.ToArray());
    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    return copy;
  }

  private static Dictionary<string, string> ReadAll(Stream s) {
    using var reader = new PackItReader(s, leaveOpen: true);
    var result = new Dictionary<string, string>();
    foreach (var e in reader.Entries) {
      var data = reader.Extract(e);
      result[e.Name] = System.Text.Encoding.ASCII.GetString(data);
    }
    return result;
  }
}
