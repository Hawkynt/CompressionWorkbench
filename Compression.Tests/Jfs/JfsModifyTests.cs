using Compression.Registry;
using FileSystem.Jfs;

namespace Compression.Tests.Jfs;

/// <summary>
/// JFS in-place leaf mutation tests (<see cref="IArchiveModifiable"/>).
/// <para>
/// The mutator patches the dtree leaf root, allocates blocks via the dmap,
/// writes a new file dinode with an inline xtree extent, and reruns the
/// <c>ujfs_adjtree</c> tree maintenance — all without going through the
/// writer's rebuild path. Splits, name-continuation overflow, FSIT extent
/// growth and xtree root promotion fall back with
/// <see cref="NotSupportedException"/>.
/// </para>
/// </summary>
[TestFixture]
public class JfsModifyTests {

  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new JfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> ReadAll(MemoryStream image) {
    image.Position = 0;
    var r = new JfsReader(image);
    return r.Entries.Where(e => !e.IsDirectory)
                    .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
  }

  // ── Add (leaf-only) ────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_SmallFile_LeafOnly_RoundTrips() {
    using var img = BuildImage(("a.txt", "alpha"u8.ToArray()));
    var d = new JfsFormatDescriptor();

    ((IArchiveModifiable)d).Add(img, [ArchiveInputInfo.InMemory("b.txt", "bravo"u8.ToArray())]);

    var files = ReadAll(img);
    Assert.That(files.ContainsKey("a.txt"), Is.True, "existing root file kept");
    Assert.That(files["a.txt"], Is.EqualTo("alpha"u8.ToArray()), "existing content intact");
    Assert.That(files.ContainsKey("b.txt"), Is.True, "new file added");
    Assert.That(files["b.txt"], Is.EqualTo("bravo"u8.ToArray()), "new file content intact");
  }

  [Test, Category("RoundTrip")]
  public void Add_TwoFiles_PreservesSortedOrder() {
    using var img = BuildImage(("m.txt", "mike"u8.ToArray()));
    var d = new JfsFormatDescriptor();

    // Insert one alphabetically before and one after "m.txt".
    ((IArchiveModifiable)d).Add(img, [
      ArchiveInputInfo.InMemory("a.txt", "alpha"u8.ToArray()),
      ArchiveInputInfo.InMemory("z.txt", "zulu"u8.ToArray()),
    ]);

    img.Position = 0;
    var r = new JfsReader(img);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Has.Count.EqualTo(3));
    // fsck.jfs requires strictly ascending UCS-2 ordinal stbl keys.
    var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
    Assert.That(names, Is.EqualTo(sorted), "dtree stbl must list entries in ascending UCS-2 order");
  }

  [Test, Category("RoundTrip")]
  public void Add_MultipleSmallFiles_AllRoundTrip() {
    using var img = BuildImage(("a.txt", "a"u8.ToArray()));
    var d = new JfsFormatDescriptor();
    ((IArchiveModifiable)d).Add(img, [
      ArchiveInputInfo.InMemory("b.txt", "bee"u8.ToArray()),
      ArchiveInputInfo.InMemory("c.txt", "cee"u8.ToArray()),
      ArchiveInputInfo.InMemory("d.txt", "dee"u8.ToArray()),
    ]);

    var files = ReadAll(img);
    Assert.That(files["a.txt"], Is.EqualTo("a"u8.ToArray()));
    Assert.That(files["b.txt"], Is.EqualTo("bee"u8.ToArray()));
    Assert.That(files["c.txt"], Is.EqualTo("cee"u8.ToArray()));
    Assert.That(files["d.txt"], Is.EqualTo("dee"u8.ToArray()));
  }

  // ── Remove ─────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Remove_SingleFile_RoundTrips() {
    using var img = BuildImage(
      ("keep.txt", "kept"u8.ToArray()),
      ("drop.txt", "dropped"u8.ToArray())
    );
    var d = new JfsFormatDescriptor();

    ((IArchiveModifiable)d).Remove(img, ["drop.txt"]);

    var files = ReadAll(img);
    Assert.That(files.ContainsKey("drop.txt"), Is.False, "removed file is gone");
    Assert.That(files.ContainsKey("keep.txt"), Is.True, "other file kept");
    Assert.That(files["keep.txt"], Is.EqualTo("kept"u8.ToArray()), "kept content intact");
  }

  [Test, Category("RoundTrip")]
  public void Remove_ThenAdd_RoundTrips() {
    using var img = BuildImage(("a.txt", "alpha"u8.ToArray()), ("b.txt", "bravo"u8.ToArray()));
    var d = new JfsFormatDescriptor();

    ((IArchiveModifiable)d).Remove(img, ["a.txt"]);
    ((IArchiveModifiable)d).Add(img, [ArchiveInputInfo.InMemory("c.txt", "charlie"u8.ToArray())]);

    var files = ReadAll(img);
    Assert.That(files.ContainsKey("a.txt"), Is.False);
    Assert.That(files["b.txt"], Is.EqualTo("bravo"u8.ToArray()));
    Assert.That(files["c.txt"], Is.EqualTo("charlie"u8.ToArray()));
  }

  // ── Honest fallbacks ───────────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Add_RequiresSplit_ThrowsClean() {
    // Fill the inline dtroot (8 slots) — adding a 9th must throw NotSupportedException.
    using var img = BuildImage(
      ("a.txt", "1"u8.ToArray()),
      ("b.txt", "2"u8.ToArray()),
      ("c.txt", "3"u8.ToArray()),
      ("d.txt", "4"u8.ToArray()),
      ("e.txt", "5"u8.ToArray()),
      ("f.txt", "6"u8.ToArray()),
      ("g.txt", "7"u8.ToArray()),
      ("h.txt", "8"u8.ToArray())
    );
    var d = new JfsFormatDescriptor();

    Assert.That(() => ((IArchiveModifiable)d).Add(img,
      [ArchiveInputInfo.InMemory("i.txt", "9"u8.ToArray())]),
      Throws.InstanceOf<NotSupportedException>().With.Message.Contains("split"),
      "JFS must fall back honestly when dtree split would be required.");
  }

  // Nested paths are now supported when the intermediate directory exists;
  // if it doesn't, the mutator throws DirectoryNotFoundException — that's
  // the honest behaviour.
  [Test, Category("ErrorHandling")]
  public void Add_NestedPath_MissingIntermediate_ThrowsClean() {
    using var img = BuildImage(("a.txt", "1"u8.ToArray()));
    var d = new JfsFormatDescriptor();

    Assert.That(() => ((IArchiveModifiable)d).Add(img,
      [ArchiveInputInfo.InMemory("sub/inner.txt", "nested"u8.ToArray())]),
      Throws.InstanceOf<DirectoryNotFoundException>(),
      "JFS mutator should reject paths whose intermediate directories don't exist.");
  }

  // Long names are now supported via continuation-slot chains; the only
  // remaining limit is the per-name-slot capacity of the parent dtree page.
  [Test, Category("RoundTrip")]
  public void Add_LongName_NowSupported_RoundTrips() {
    using var img = BuildImage(("a.txt", "1"u8.ToArray()));
    var d = new JfsFormatDescriptor();

    ((IArchiveModifiable)d).Add(img,
      [ArchiveInputInfo.InMemory("this_name_is_long.txt", "long-content"u8.ToArray())]);

    var files = ReadAll(img);
    Assert.That(files.ContainsKey("this_name_is_long.txt"), Is.True,
      "Long names via continuation slots are now supported.");
    Assert.That(files["this_name_is_long.txt"], Is.EqualTo("long-content"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Remove_Nonexistent_Throws() {
    using var img = BuildImage(("a.txt", "1"u8.ToArray()));
    var d = new JfsFormatDescriptor();

    Assert.That(() => ((IArchiveModifiable)d).Remove(img, ["missing.txt"]),
      Throws.InstanceOf<FileNotFoundException>());
  }
}
