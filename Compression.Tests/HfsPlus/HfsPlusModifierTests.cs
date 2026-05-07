#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

[TestFixture]
public class HfsPlusModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    HfsPlusModifier.AddFile(ms, "hello.txt", "world-hfsplus"u8.ToArray());

    ms.Position = 0;
    using var r = new HfsPlusReader(ms, leaveOpen: true);
    var entry = r.Entries.Single(e => !e.IsDirectory && e.Name == "hello.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(entry)), Is.EqualTo("world-hfsplus"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingFiles() {
    var ms = BuildImageWith(("first.txt", "AAA"), ("second.txt", "BBB"));
    HfsPlusModifier.AddFile(ms, "third.txt", "CCC"u8.ToArray());

    ms.Position = 0;
    using var r = new HfsPlusReader(ms, leaveOpen: true);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(names, Is.SupersetOf(new[] { "first.txt", "second.txt", "third.txt" }));
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(r.Entries.Single(e => e.Name == "first.txt"))), Is.EqualTo("AAA"));
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(r.Entries.Single(e => e.Name == "second.txt"))), Is.EqualTo("BBB"));
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(r.Entries.Single(e => e.Name == "third.txt"))), Is.EqualTo("CCC"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultiBlockPayload() {
    var ms = BuildEmptyImage();
    var data = new byte[10_000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13) & 0xFF);

    HfsPlusModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    using var r = new HfsPlusReader(ms, leaveOpen: true);
    var entry = r.Entries.Single(e => !e.IsDirectory && e.Name == "big.bin");
    Assert.That(r.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_ReplacesByName() {
    var ms = BuildImageWith(("dup.txt", "OLD"));
    HfsPlusModifier.AddFile(ms, "dup.txt", "NEW"u8.ToArray());

    ms.Position = 0;
    using var r = new HfsPlusReader(ms, leaveOpen: true);
    var matches = r.Entries.Where(e => !e.IsDirectory && e.Name == "dup.txt").ToList();
    Assert.That(matches, Has.Count.EqualTo(1));
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(matches[0])), Is.EqualTo("NEW"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildImageWith(("keep.txt", "K"), ("drop.txt", "D"));
    Assert.That(HfsPlusModifier.RemoveFile(ms, "drop.txt"), Is.True);

    ms.Position = 0;
    using var r = new HfsPlusReader(ms, leaveOpen: true);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("keep.txt"));
    Assert.That(names, Does.Not.Contain("drop.txt"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(HfsPlusModifier.RemoveFile(ms, "ghost"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesPayloadBytes() {
    var ms = BuildEmptyImage();
    HfsPlusModifier.AddFile(ms, "secret.bin", "TOP-SECRET-MARKER-HFSPLUS"u8.ToArray());
    HfsPlusModifier.RemoveFile(ms, "secret.bin");

    var asAscii = System.Text.Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOP-SECRET-MARKER-HFSPLUS"));
  }

  [Test, Category("RoundTrip")]
  public void AddRemoveAdd_RoundTrips() {
    var ms = BuildEmptyImage();
    HfsPlusModifier.AddFile(ms, "a.txt", "first"u8.ToArray());
    HfsPlusModifier.RemoveFile(ms, "a.txt");
    HfsPlusModifier.AddFile(ms, "a.txt", "second"u8.ToArray());

    ms.Position = 0;
    using var r = new HfsPlusReader(ms, leaveOpen: true);
    var entry = r.Entries.Single(e => !e.IsDirectory && e.Name == "a.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(entry)), Is.EqualTo("second"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_Works() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "hello-via-iface"u8.ToArray());
      ((IArchiveModifiable)new HfsPlusFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via.txt", false)]);

      ms.Position = 0;
      using var r = new HfsPlusReader(ms, leaveOpen: true);
      Assert.That(r.Entries.Any(e => !e.IsDirectory && e.Name == "via.txt"), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_Works() {
    var ms = BuildImageWith(("removeme.txt", "X"));
    ((IArchiveModifiable)new HfsPlusFormatDescriptor()).Remove(ms, ["removeme.txt"]);

    ms.Position = 0;
    using var r = new HfsPlusReader(ms, leaveOpen: true);
    Assert.That(r.Entries.Any(e => !e.IsDirectory && e.Name == "removeme.txt"), Is.False);
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(new HfsPlusWriter().Build());
    return ms;
  }

  private static MemoryStream BuildImageWith(params (string Name, string Content)[] files) {
    var w = new HfsPlusWriter();
    foreach (var (n, c) in files)
      w.AddFile(n, System.Text.Encoding.ASCII.GetBytes(c));
    var ms = new MemoryStream();
    ms.Write(w.Build());
    return ms;
  }
}
