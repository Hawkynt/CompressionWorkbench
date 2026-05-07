#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Spark;

namespace Compression.Tests.Spark;

[TestFixture]
public class SparkModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedSpark();
    SparkModifier.AddFile(ms, "added.txt", "hello-spark"u8.ToArray());
    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries["added.txt"], Is.EqualTo("hello-spark"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_RoundTrips() {
    var ms = BuildSeedSpark();
    var data = new byte[5000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    SparkModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    using var reader = new SparkReader(ms, leaveOpen: true);
    var big = reader.Entries.Single(e => e.FileName == "big.bin");
    var got = reader.Extract(big);
    Assert.That(got, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedSpark();
    SparkModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    SparkModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(SparkModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries.ContainsKey("victim.txt"), Is.False);
    Assert.That(entries["keeper.txt"], Is.EqualTo("keep-me"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedSpark();
    Assert.That(SparkModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedSpark();
    SparkModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    SparkModifier.RemoveFile(ms, "doc.txt");
    SparkModifier.AddFile(ms, "doc.txt", "version-two"u8.ToArray());

    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries["doc.txt"], Is.EqualTo("version-two"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedSpark();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new SparkFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var entries = ReadAll(ms);
      Assert.That(entries["via-if.txt"], Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  // ── Helpers ─────────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedSpark() {
    var ms = new MemoryStream();
    using (var w = new SparkWriter(ms, leaveOpen: true))
      w.AddFile("seed.txt", "seed-content"u8.ToArray());
    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    return copy;
  }

  private static Dictionary<string, string> ReadAll(Stream s) {
    using var reader = new SparkReader(s, leaveOpen: true);
    var result = new Dictionary<string, string>();
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      var data = reader.Extract(e);
      result[e.FileName] = System.Text.Encoding.ASCII.GetString(data);
    }
    return result;
  }
}
