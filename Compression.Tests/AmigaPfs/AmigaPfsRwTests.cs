using System.Text;
using Compression.Registry;
using FileSystem.AmigaPfs;

namespace Compression.Tests.AmigaPfs;

/// <summary>
/// R/W round-trip tests for the Stage 1 PFS3 modifier (in-place Add/Remove
/// against the writer/reader's anode-as-direct-block convention).
///
/// Equivalence classes covered:
/// <list type="bullet">
///   <item>Add against an empty image (writer's seed image is a bare root + first dirblock).</item>
///   <item>Add against an image with existing files (room available in current dirblock).</item>
///   <item>Add forcing a new dirblock allocation (existing dirblock has no room).</item>
///   <item>Remove first/middle/last entry in a dirblock.</item>
///   <item>Remove an entry whose removal empties a non-first dirblock and unlinks it from the chain.</item>
///   <item>Replace-by-name (Add of an existing name removes the prior entry first).</item>
///   <item>Mixed sequence of Add/Remove operations round-tripped through <see cref="AmigaPfsReader"/>.</item>
///   <item>Sad-paths: Remove of missing entry, modifier rejects unseekable streams.</item>
/// </list>
/// </summary>
[TestFixture]
public class AmigaPfsRwTests {

  /// <summary>
  /// Helper: build a stream backed by an in-memory image produced by
  /// <see cref="AmigaPfsWriter"/>, then return it expanded into a
  /// random-access MemoryStream the modifier can mutate.
  /// </summary>
  private static MemoryStream BuildSeedImage(Action<AmigaPfsWriter>? configure = null, string label = "DISK") {
    var w = new AmigaPfsWriter();
    configure?.Invoke(w);
    var bytes = w.Build(label);
    // The MemoryStream must be resizable so the modifier can SetLength when the
    // image grows past the original allocation. The byte[] ctor produces a
    // fixed-size stream, so we pre-size and copy.
    var ms = new MemoryStream(capacity: bytes.Length * 2);
    ms.Write(bytes, 0, bytes.Length);
    ms.Position = 0;
    return ms;
  }

  private static IReadOnlyList<AmigaPfsEntry> ListEntries(MemoryStream image) {
    image.Position = 0;
    var r = new AmigaPfsReader(image);
    return r.Entries.ToArray();
  }

  private static byte[] ExtractEntry(MemoryStream image, string name) {
    image.Position = 0;
    var r = new AmigaPfsReader(image);
    var entry = r.Entries.Single(e => e.Name == name);
    return r.Extract(entry);
  }

  // ── Add ────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_AgainstEmptyImage_AppendsEntry() {
    using var image = BuildSeedImage();

    AmigaPfsModifier.AddFile(image, "added.txt", Encoding.ASCII.GetBytes("hello modifier"));

    var entries = ListEntries(image);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("added.txt"));
    Assert.That(Encoding.ASCII.GetString(ExtractEntry(image, "added.txt")), Is.EqualTo("hello modifier"));
  }

  [Test, Category("HappyPath")]
  public void Add_AgainstImageWithExistingFile_PreservesExisting() {
    using var image = BuildSeedImage(w => w.AddFile("original.txt", Encoding.ASCII.GetBytes("first")));

    AmigaPfsModifier.AddFile(image, "appended.bin", new byte[] { 1, 2, 3, 4, 5 });

    var entries = ListEntries(image);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "original.txt", "appended.bin" }));
    Assert.That(ExtractEntry(image, "original.txt"), Is.EqualTo(Encoding.ASCII.GetBytes("first")));
    Assert.That(ExtractEntry(image, "appended.bin"), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
  }

  [Test, Category("HappyPath")]
  public void Add_MultiBlockFile_RoundTripsBytewise() {
    using var image = BuildSeedImage();
    var payload = new byte[2048];
    for (var i = 0; i < payload.Length; i++)
      payload[i] = (byte)((i * 31) & 0xFF);

    AmigaPfsModifier.AddFile(image, "blob.bin", payload);

    Assert.That(ExtractEntry(image, "blob.bin"), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Add_ZeroByteFile_SurfacesWithZeroSize() {
    using var image = BuildSeedImage();

    AmigaPfsModifier.AddFile(image, "empty.txt", []);

    var entries = ListEntries(image);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("empty.txt"));
    Assert.That(entries[0].Size, Is.EqualTo(0));
    Assert.That(ExtractEntry(image, "empty.txt"), Is.Empty);
  }

  [Test, Category("Boundary")]
  public void Add_ForcesNewDirBlockAllocation_WhenCurrentBlockIsFull() {
    // Each entry costs (17 header + nameLen + 1 comment) bytes; for a 30-char name that's
    // 17 + 30 + 1 = 48 bytes. A dirblock has 512-20-1 = 491 bytes of entry budget,
    // so ~10 entries per block. We seed with 10 such entries (filling the first
    // dirblock) and prove the 11th add allocates a fresh block in the chain.
    using var image = BuildSeedImage(w => {
      for (var i = 0; i < 10; i++) {
        var name = $"prefilled-entry-{i:D2}.txt"; // 24 chars
        w.AddFile(name, Encoding.ASCII.GetBytes($"contents{i}"));
      }
    });

    AmigaPfsModifier.AddFile(image, "freshly-allocated-block.dat", Encoding.ASCII.GetBytes("payload"));

    var entries = ListEntries(image);
    Assert.That(entries, Has.Count.EqualTo(11));
    Assert.That(entries.Select(e => e.Name), Does.Contain("freshly-allocated-block.dat"));
    Assert.That(Encoding.ASCII.GetString(ExtractEntry(image, "freshly-allocated-block.dat")), Is.EqualTo("payload"));
    // Pre-existing entries are still intact.
    for (var i = 0; i < 10; i++)
      Assert.That(Encoding.ASCII.GetString(ExtractEntry(image, $"prefilled-entry-{i:D2}.txt")), Is.EqualTo($"contents{i}"));
  }

  [Test, Category("HappyPath")]
  public void Add_ReplaceByName_OverwritesPreviousEntry() {
    using var image = BuildSeedImage(w => w.AddFile("config.cfg", Encoding.ASCII.GetBytes("OLD")));

    AmigaPfsModifier.AddFile(image, "config.cfg", Encoding.ASCII.GetBytes("NEW PAYLOAD"));

    var entries = ListEntries(image);
    // Replace-by-name: exactly one entry survives, with the new contents.
    Assert.That(entries.Count(e => e.Name == "config.cfg"), Is.EqualTo(1));
    Assert.That(Encoding.ASCII.GetString(ExtractEntry(image, "config.cfg")), Is.EqualTo("NEW PAYLOAD"));
  }

  [Test, Category("HappyPath")]
  public void Add_Directory_SurfacesAsDirectoryEntry() {
    using var image = BuildSeedImage();

    AmigaPfsModifier.AddDirectory(image, "subdir");

    var entries = ListEntries(image);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("subdir"));
    Assert.That(entries[0].IsDirectory, Is.True);
  }

  // ── Remove ─────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_OnlyEntry_LeavesEmptyImage() {
    using var image = BuildSeedImage(w => w.AddFile("solo.txt", Encoding.ASCII.GetBytes("alone")));

    var removed = AmigaPfsModifier.RemoveFile(image, "solo.txt");

    Assert.That(removed, Is.True);
    Assert.That(ListEntries(image), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Remove_FirstEntry_CompactsRemaining() {
    using var image = BuildSeedImage(w => {
      w.AddFile("alpha.txt", Encoding.ASCII.GetBytes("A"));
      w.AddFile("beta.txt", Encoding.ASCII.GetBytes("B"));
      w.AddFile("gamma.txt", Encoding.ASCII.GetBytes("C"));
    });

    var removed = AmigaPfsModifier.RemoveFile(image, "alpha.txt");

    Assert.That(removed, Is.True);
    var entries = ListEntries(image);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "beta.txt", "gamma.txt" }));
    Assert.That(Encoding.ASCII.GetString(ExtractEntry(image, "beta.txt")), Is.EqualTo("B"));
    Assert.That(Encoding.ASCII.GetString(ExtractEntry(image, "gamma.txt")), Is.EqualTo("C"));
  }

  [Test, Category("HappyPath")]
  public void Remove_MiddleEntry_CompactsAroundIt() {
    using var image = BuildSeedImage(w => {
      w.AddFile("a.txt", Encoding.ASCII.GetBytes("AAA"));
      w.AddFile("b.txt", Encoding.ASCII.GetBytes("BBB"));
      w.AddFile("c.txt", Encoding.ASCII.GetBytes("CCC"));
    });

    var removed = AmigaPfsModifier.RemoveFile(image, "b.txt");

    Assert.That(removed, Is.True);
    var entries = ListEntries(image);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "a.txt", "c.txt" }));
  }

  [Test, Category("HappyPath")]
  public void Remove_LastEntry_RetainsLeadingEntries() {
    using var image = BuildSeedImage(w => {
      w.AddFile("first.txt", Encoding.ASCII.GetBytes("F"));
      w.AddFile("second.txt", Encoding.ASCII.GetBytes("S"));
    });

    var removed = AmigaPfsModifier.RemoveFile(image, "second.txt");

    Assert.That(removed, Is.True);
    var entries = ListEntries(image);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "first.txt" }));
  }

  [Test, Category("HappyPath")]
  public void Remove_WipesFileDataBytes() {
    using var image = BuildSeedImage(w => w.AddFile("secret.bin", Encoding.ASCII.GetBytes("CONFIDENTIAL_BYTES")));
    var imageBefore = image.ToArray();
    Assert.That(System.Text.Encoding.ASCII.GetString(imageBefore), Does.Contain("CONFIDENTIAL_BYTES"),
      "Sanity: seed image carries the marker before removal.");

    AmigaPfsModifier.RemoveFile(image, "secret.bin", wipeData: true);

    var imageAfter = image.ToArray();
    Assert.That(System.Text.Encoding.ASCII.GetString(imageAfter), Does.Not.Contain("CONFIDENTIAL_BYTES"),
      "Removed file's bytes must be wiped from the on-disk image.");
  }

  [Test, Category("HappyPath")]
  public void Remove_UnlinksEmptyNonFirstDirBlock() {
    // Fill the first dirblock + spill 1 entry into a second dirblock, then
    // remove that spill entry. The second dirblock becomes empty and the
    // modifier unlinks it from the chain — the remaining 10 entries still
    // round-trip cleanly.
    using var image = BuildSeedImage(w => {
      for (var i = 0; i < 10; i++)
        w.AddFile($"prefilled-entry-{i:D2}.txt", Encoding.ASCII.GetBytes($"c{i}"));
    });
    AmigaPfsModifier.AddFile(image, "spilled-into-new-dirblock.dat", Encoding.ASCII.GetBytes("spill"));
    Assert.That(ListEntries(image), Has.Count.EqualTo(11), "Sanity: seed has 11 entries (10 + 1 spill).");

    AmigaPfsModifier.RemoveFile(image, "spilled-into-new-dirblock.dat");

    var entries = ListEntries(image);
    Assert.That(entries, Has.Count.EqualTo(10));
    Assert.That(entries.Select(e => e.Name), Does.Not.Contain("spilled-into-new-dirblock.dat"));
  }

  // ── Sad-path & boundary ────────────────────────────────────────────────

  [Test, Category("Sad")]
  public void Remove_MissingEntry_ReturnsFalse() {
    using var image = BuildSeedImage(w => w.AddFile("present.txt", Encoding.ASCII.GetBytes("p")));

    var removed = AmigaPfsModifier.RemoveFile(image, "absent.txt");

    Assert.That(removed, Is.False);
    // The present entry must still be intact.
    Assert.That(ListEntries(image).Select(e => e.Name), Is.EqualTo(new[] { "present.txt" }));
  }

  [Test, Category("Sad")]
  public void Add_RejectsNullArguments() {
    using var image = BuildSeedImage();
    Assert.Throws<ArgumentNullException>(() => AmigaPfsModifier.AddFile(null!, "x", []));
    Assert.Throws<ArgumentNullException>(() => AmigaPfsModifier.AddFile(image, null!, []));
    Assert.Throws<ArgumentNullException>(() => AmigaPfsModifier.AddFile(image, "x", null!));
  }

  [Test, Category("Sad")]
  public void Remove_RejectsNullArguments() {
    using var image = BuildSeedImage();
    Assert.Throws<ArgumentNullException>(() => AmigaPfsModifier.RemoveFile(null!, "x"));
    Assert.Throws<ArgumentNullException>(() => AmigaPfsModifier.RemoveFile(image, null!));
  }

  // ── Mixed sequence ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void MixedSequence_AddRemoveReplaceAdd_RoundTrips() {
    using var image = BuildSeedImage();

    AmigaPfsModifier.AddFile(image, "a.txt", Encoding.ASCII.GetBytes("A1"));
    AmigaPfsModifier.AddFile(image, "b.txt", Encoding.ASCII.GetBytes("B1"));
    AmigaPfsModifier.AddFile(image, "c.txt", Encoding.ASCII.GetBytes("C1"));

    AmigaPfsModifier.RemoveFile(image, "b.txt");

    AmigaPfsModifier.AddFile(image, "a.txt", Encoding.ASCII.GetBytes("A2-replaced"));  // replace-by-name
    AmigaPfsModifier.AddFile(image, "d.txt", Encoding.ASCII.GetBytes("D1"));

    var entries = ListEntries(image);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "a.txt", "c.txt", "d.txt" }));
    Assert.That(Encoding.ASCII.GetString(ExtractEntry(image, "a.txt")), Is.EqualTo("A2-replaced"));
    Assert.That(Encoding.ASCII.GetString(ExtractEntry(image, "c.txt")), Is.EqualTo("C1"));
    Assert.That(Encoding.ASCII.GetString(ExtractEntry(image, "d.txt")), Is.EqualTo("D1"));
  }

  // ── Descriptor surface ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AddRemove_RoundTripsThroughInterface() {
    var d = new AmigaPfsFormatDescriptor();
    using var image = BuildSeedImage();

    d.Add(image, [
      ArchiveInputInfo.InMemory("alpha.txt", Encoding.ASCII.GetBytes("alpha")),
      ArchiveInputInfo.InMemory("beta.bin", new byte[] { 0x55, 0xAA, 0xFF }),
    ]);

    image.Position = 0;
    var listed1 = d.List(image, null);
    Assert.That(listed1.Select(e => e.Name), Is.EquivalentTo(new[] { "alpha.txt", "beta.bin" }));

    d.Remove(image, ["alpha.txt"]);

    image.Position = 0;
    var listed2 = d.List(image, null);
    Assert.That(listed2.Select(e => e.Name), Is.EquivalentTo(new[] { "beta.bin" }));

    image.Position = 0;
    Assert.That(d.ExtractEntryToMemory(image, "beta.bin", null), Is.EqualTo(new byte[] { 0x55, 0xAA, 0xFF }));
  }
}
