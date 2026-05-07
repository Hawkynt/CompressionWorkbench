#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Zoo;

namespace Compression.Tests.Zoo;

[TestFixture]
public class ZooModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedZoo();
    ZooModifier.AddFile(ms, "added.txt", "hello-zoo"u8.ToArray());
    ms.Position = 0;
    var reader = new ZooReader(ms);
    var entry = reader.Entries.Single(e => e.EffectiveName == "added.txt");
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(entry)), Is.EqualTo("hello-zoo"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingEntries() {
    var ms = BuildSeedZoo();
    ZooModifier.AddFile(ms, "added.txt", "added-data"u8.ToArray());
    ms.Position = 0;
    var reader = new ZooReader(ms);
    var byName = reader.Entries.ToDictionary(e => e.EffectiveName, e =>
      System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(e)));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"));
    Assert.That(byName["added.txt"], Is.EqualTo("added-data"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_RoundTrips() {
    var ms = BuildSeedZoo();
    var data = new byte[5000];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 1) & 0xFF);
    ZooModifier.AddFile(ms, "big.bin", data);

    ms.Position = 0;
    var reader = new ZooReader(ms);
    var entry = reader.Entries.Single(e => e.EffectiveName == "big.bin");
    Assert.That(reader.ExtractEntry(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_ToEmptyArchive_PatchesFirstEntryOffset() {
    var ms = BuildEmptyZoo();
    ZooModifier.AddFile(ms, "first.txt", "primum"u8.ToArray());
    ms.Position = 0;
    var reader = new ZooReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(reader.Entries[0])),
      Is.EqualTo("primum"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleAppends_ChainStaysIntact() {
    var ms = BuildSeedZoo();
    ZooModifier.AddFile(ms, "a.txt", "alpha"u8.ToArray());
    ZooModifier.AddFile(ms, "b.txt", "bravo"u8.ToArray());
    ZooModifier.AddFile(ms, "c.txt", "charlie"u8.ToArray());

    ms.Position = 0;
    var reader = new ZooReader(ms);
    var names = reader.Entries.Select(e => e.EffectiveName).ToArray();
    Assert.That(names, Is.EqualTo(new[] { "seed.txt", "a.txt", "b.txt", "c.txt" }));
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(reader.Entries[3])),
      Is.EqualTo("charlie"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedZoo();
    ZooModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    ZooModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(ZooModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var reader = new ZooReader(ms);
    Assert.That(reader.Entries.Any(e => e.EffectiveName == "victim.txt"), Is.False);
    Assert.That(reader.Entries.Any(e => e.EffectiveName == "keeper.txt"), Is.True);
    Assert.That(reader.Entries.Any(e => e.EffectiveName == "seed.txt"), Is.True);
    // Ensure remaining entries still extract.
    foreach (var e in reader.Entries)
      Assert.That(reader.ExtractEntry(e), Is.Not.Null);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_HeadEntry_RewritesFirstEntryOffset() {
    var ms = BuildSeedZoo();
    ZooModifier.AddFile(ms, "second.txt", "beta"u8.ToArray());
    Assert.That(ZooModifier.RemoveFile(ms, "seed.txt"), Is.True);

    ms.Position = 0;
    var reader = new ZooReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].EffectiveName, Is.EqualTo("second.txt"));
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(reader.Entries[0])),
      Is.EqualTo("beta"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_TailEntry_LeavesEarlierIntact() {
    var ms = BuildSeedZoo();
    ZooModifier.AddFile(ms, "tail.txt", "ultimo"u8.ToArray());
    Assert.That(ZooModifier.RemoveFile(ms, "tail.txt"), Is.True);

    ms.Position = 0;
    var reader = new ZooReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].EffectiveName, Is.EqualTo("seed.txt"));
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(reader.Entries[0])),
      Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_MiddleEntry_RewritesAllSubsequentLinks() {
    var ms = BuildSeedZoo();
    ZooModifier.AddFile(ms, "middle.txt", "centrum"u8.ToArray());
    ZooModifier.AddFile(ms, "tail.txt", "finis"u8.ToArray());

    Assert.That(ZooModifier.RemoveFile(ms, "middle.txt"), Is.True);

    ms.Position = 0;
    var reader = new ZooReader(ms);
    Assert.That(reader.Entries.Select(e => e.EffectiveName), Is.EqualTo(new[] { "seed.txt", "tail.txt" }));
    var byName = reader.Entries.ToDictionary(e => e.EffectiveName, e =>
      System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(e)));
    Assert.That(byName["seed.txt"], Is.EqualTo("seed-content"));
    Assert.That(byName["tail.txt"], Is.EqualTo("finis"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedZoo();
    Assert.That(ZooModifier.RemoveFile(ms, "ghost.txt"), Is.False);
    // Archive must still be intact.
    ms.Position = 0;
    var reader = new ZooReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedZoo();
    ZooModifier.AddFile(ms, "doc.txt", "version-one"u8.ToArray());
    ZooModifier.RemoveFile(ms, "doc.txt");
    ZooModifier.AddFile(ms, "doc.txt", "version-two-replacement"u8.ToArray());

    ms.Position = 0;
    var reader = new ZooReader(ms);
    var matching = reader.Entries.Where(e => e.EffectiveName == "doc.txt").ToList();
    Assert.That(matching, Has.Count.EqualTo(1));
    Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(matching[0])),
      Is.EqualTo("version-two-replacement"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedZoo();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new ZooFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via-if.txt", false)]);

      ms.Position = 0;
      var reader = new ZooReader(ms);
      var entry = reader.Entries.Single(e => e.EffectiveName == "via-if.txt");
      Assert.That(System.Text.Encoding.ASCII.GetString(reader.ExtractEntry(entry)), Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_DropsEntries() {
    var ms = BuildSeedZoo();
    ZooModifier.AddFile(ms, "drop.txt", "dropped"u8.ToArray());
    ((IArchiveModifiable)new ZooFormatDescriptor()).Remove(ms, ["drop.txt"]);

    ms.Position = 0;
    var reader = new ZooReader(ms);
    Assert.That(reader.Entries.Any(e => e.EffectiveName == "drop.txt"), Is.False);
    Assert.That(reader.Entries.Any(e => e.EffectiveName == "seed.txt"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_HasModifyCapability() {
    var d = new ZooFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedZoo() {
    var ms = new MemoryStream();
    using (var writer = new ZooWriter(ms, leaveOpen: true, defaultMethod: ZooCompressionMethod.Store))
      writer.AddEntry("seed.txt", "seed-content"u8.ToArray());

    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    return copy;
  }

  private static MemoryStream BuildEmptyZoo() {
    var ms = new MemoryStream();
    using (var _ = new ZooWriter(ms, leaveOpen: true)) {
      // No entries; Finish() runs on dispose, patches firstEntryOffset = 0.
    }
    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    return copy;
  }
}
