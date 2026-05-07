#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Hfs;

namespace Compression.Tests.Hfs;

[TestFixture]
public class HfsModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    HfsModifier.AddFile(ms, "HELLO.TXT", "world-hfs"u8.ToArray());

    ms.Position = 0;
    var r = new HfsReader(ms);
    var entry = r.Entries.Single(e => e.Name == "HELLO.TXT");
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(entry)), Is.EqualTo("world-hfs"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingFiles() {
    var ms = BuildImageWith(("FIRST.TXT", "AAA"), ("SECOND.TXT", "BBB"));
    HfsModifier.AddFile(ms, "THIRD.TXT", "CCC"u8.ToArray());

    ms.Position = 0;
    var r = new HfsReader(ms);
    var byName = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(byName.Keys, Is.SupersetOf(new[] { "FIRST.TXT", "SECOND.TXT", "THIRD.TXT" }));
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(byName["FIRST.TXT"])), Is.EqualTo("AAA"));
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(byName["SECOND.TXT"])), Is.EqualTo("BBB"));
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(byName["THIRD.TXT"])), Is.EqualTo("CCC"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultiBlockPayload() {
    var ms = BuildEmptyImage();
    var data = new byte[5_000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 11) & 0xFF);

    HfsModifier.AddFile(ms, "BIG.BIN", data);

    ms.Position = 0;
    var r = new HfsReader(ms);
    var entry = r.Entries.Single(e => e.Name == "BIG.BIN");
    Assert.That(r.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_ReplacesByName() {
    var ms = BuildImageWith(("DUP.TXT", "OLD"));
    HfsModifier.AddFile(ms, "DUP.TXT", "NEW"u8.ToArray());

    ms.Position = 0;
    var r = new HfsReader(ms);
    var matches = r.Entries.Where(e => !e.IsDirectory && e.Name == "DUP.TXT").ToList();
    Assert.That(matches, Has.Count.EqualTo(1));
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(matches[0])), Is.EqualTo("NEW"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildImageWith(("KEEP.TXT", "K"), ("DROP.TXT", "D"));
    Assert.That(HfsModifier.RemoveFile(ms, "DROP.TXT"), Is.True);

    ms.Position = 0;
    var r = new HfsReader(ms);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("KEEP.TXT"));
    Assert.That(names, Does.Not.Contain("DROP.TXT"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(HfsModifier.RemoveFile(ms, "GHOST.TXT"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesPayloadBytes() {
    var ms = BuildEmptyImage();
    HfsModifier.AddFile(ms, "SECRET.BIN", "TOPSECRET-MARKER-HFS"u8.ToArray());
    HfsModifier.RemoveFile(ms, "SECRET.BIN");

    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-HFS"));
  }

  [Test, Category("RoundTrip")]
  public void AddRemoveAdd_RoundTrips() {
    var ms = BuildEmptyImage();
    HfsModifier.AddFile(ms, "A.TXT", "first"u8.ToArray());
    HfsModifier.RemoveFile(ms, "A.TXT");
    HfsModifier.AddFile(ms, "A.TXT", "second"u8.ToArray());

    ms.Position = 0;
    var r = new HfsReader(ms);
    var entry = r.Entries.Single(e => e.Name == "A.TXT");
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(entry)), Is.EqualTo("second"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_Works() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "hello-via-iface"u8.ToArray());
      ((IArchiveModifiable)new HfsFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIA.TXT", false)]);

      ms.Position = 0;
      var r = new HfsReader(ms);
      Assert.That(r.Entries.Any(e => !e.IsDirectory && e.Name == "VIA.TXT"), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_Works() {
    var ms = BuildImageWith(("RM.TXT", "X"));
    ((IArchiveModifiable)new HfsFormatDescriptor()).Remove(ms, ["RM.TXT"]);

    ms.Position = 0;
    var r = new HfsReader(ms);
    Assert.That(r.Entries.Any(e => !e.IsDirectory && e.Name == "RM.TXT"), Is.False);
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new HfsWriter().Build());
    return ms;
  }

  private static MemoryStream BuildImageWith(params (string Name, string Content)[] files) {
    var w = new HfsWriter();
    foreach (var (n, c) in files)
      w.AddFile(n, System.Text.Encoding.ASCII.GetBytes(c));
    var ms = new MemoryStream();
    ms.Write(w.Build());
    return ms;
  }
}
