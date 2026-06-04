using System.Text;
using Compression.Registry;
using FileSystem.Jfs;

namespace Compression.Tests.Jfs;

/// <summary>
/// Exercises the extended-scope JFS mutation paths that go beyond the
/// original leaf-only mutator: arbitrary path depth, long names via
/// continuation slots, external dtree leaf insert/delete (router-promoted
/// dtroot), and recursive subdirectory removal.
/// <para>
/// Each test starts from a real <see cref="JfsWriter"/> output, mutates it
/// in place via <see cref="JfsFormatDescriptor"/>'s Add/Remove, then asserts
/// the resulting image round-trips through <see cref="JfsReader"/> with the
/// expected directory contents.
/// </para>
/// </summary>
[TestFixture]
public class JfsFullScopeMutationTests {

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

  private static HashSet<string> ReadDirs(MemoryStream image) {
    image.Position = 0;
    var r = new JfsReader(image);
    return r.Entries.Where(e => e.IsDirectory)
                    .Select(e => e.Name.Replace('\\', '/'))
                    .ToHashSet();
  }

  // ── arbitrary path depth ───────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_AtArbitraryDepth_RoundTrips() {
    using var img = BuildImage(("a/b/c/d.txt", "deep"u8.ToArray()));
    var d = new JfsFormatDescriptor();
    ((IArchiveModifiable)d).Add(img, [ArchiveInputInfo.InMemory("a/b/c/e.txt", "added"u8.ToArray())]);

    var files = ReadAll(img);
    Assert.That(files.ContainsKey("a/b/c/d.txt"), Is.True);
    Assert.That(files.ContainsKey("a/b/c/e.txt"), Is.True, "added entry at depth 4 round-trips");
    Assert.That(files["a/b/c/e.txt"], Is.EqualTo("added"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Remove_AtArbitraryDepth_RoundTrips() {
    using var img = BuildImage(
      ("a/b/keep.txt", "kept"u8.ToArray()),
      ("a/b/drop.txt", "dropped"u8.ToArray())
    );
    var d = new JfsFormatDescriptor();
    ((IArchiveModifiable)d).Remove(img, ["a/b/drop.txt"]);

    var files = ReadAll(img);
    Assert.That(files.ContainsKey("a/b/drop.txt"), Is.False);
    Assert.That(files.ContainsKey("a/b/keep.txt"), Is.True);
    Assert.That(files["a/b/keep.txt"], Is.EqualTo("kept"u8.ToArray()));
  }

  // ── long names via continuation slots ──────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_LongName_25Chars_RoundTrips() {
    using var img = BuildImage(("a.txt", "1"u8.ToArray()));
    var d = new JfsFormatDescriptor();

    // 25 chars > 11 head → 1 head + 1 continuation slot (15 cap).
    var longName = "this-is-a-rather-long-name";
    ((IArchiveModifiable)d).Add(img, [ArchiveInputInfo.InMemory(longName, "ok"u8.ToArray())]);

    var files = ReadAll(img);
    Assert.That(files.ContainsKey(longName), Is.True);
    Assert.That(files[longName], Is.EqualTo("ok"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Remove_LongName_FreesContinuationSlots() {
    var longName = "another-long-filename-here";
    using var img = BuildImage(
      ("a.txt", "1"u8.ToArray()),
      (longName, "long"u8.ToArray())
    );
    var d = new JfsFormatDescriptor();
    ((IArchiveModifiable)d).Remove(img, [longName]);

    var files = ReadAll(img);
    Assert.That(files.ContainsKey(longName), Is.False);
    Assert.That(files.ContainsKey("a.txt"), Is.True);
  }

  // ── external dtree (router-promoted dtroot) ───────────────────────────

  // Builds an image whose root dtroot is already promoted to a router (>8
  // entries) and inserts a new entry — must end up in the right leaf page.
  [Test, Category("RoundTrip")]
  public void Add_IntoExternalDtreeLeaf_RoundTrips() {
    var inputs = new List<(string, byte[])>();
    for (var i = 0; i < 20; i++)
      inputs.Add(($"d{i:D3}.txt", Encoding.UTF8.GetBytes($"v{i}")));
    using var img = BuildImage(inputs.ToArray());

    var d = new JfsFormatDescriptor();
    ((IArchiveModifiable)d).Add(img, [ArchiveInputInfo.InMemory("inserted.dat", "fresh"u8.ToArray())]);

    var files = ReadAll(img);
    Assert.That(files.Count, Is.EqualTo(21), "all 20 original + 1 added entries are present");
    Assert.That(files.ContainsKey("inserted.dat"), Is.True);
    Assert.That(files["inserted.dat"], Is.EqualTo("fresh"u8.ToArray()));
    // Spot-check originals are intact.
    Assert.That(files["d000.txt"], Is.EqualTo("v0"u8.ToArray()));
    Assert.That(files["d019.txt"], Is.EqualTo("v19"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Remove_FromExternalDtreeLeaf_RoundTrips() {
    var inputs = new List<(string, byte[])>();
    for (var i = 0; i < 20; i++)
      inputs.Add(($"d{i:D3}.txt", Encoding.UTF8.GetBytes($"v{i}")));
    using var img = BuildImage(inputs.ToArray());

    var d = new JfsFormatDescriptor();
    ((IArchiveModifiable)d).Remove(img, ["d010.txt"]);

    var files = ReadAll(img);
    Assert.That(files.ContainsKey("d010.txt"), Is.False, "removed entry is gone");
    Assert.That(files.Count, Is.EqualTo(19), "19 entries remain");
  }

  // ── recursive subdirectory removal ─────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Remove_Subdirectory_Recursive_FreesEverything() {
    using var img = BuildImage(
      ("keep.txt", "kept"u8.ToArray()),
      ("doomed/f1.txt", "a"u8.ToArray()),
      ("doomed/f2.txt", "b"u8.ToArray()),
      ("doomed/nested/inner.txt", "c"u8.ToArray())
    );
    var d = new JfsFormatDescriptor();
    ((IArchiveModifiable)d).Remove(img, ["doomed"]);

    var files = ReadAll(img);
    var dirs = ReadDirs(img);
    Assert.That(files.ContainsKey("keep.txt"), Is.True, "sibling root file unchanged");
    Assert.That(files["keep.txt"], Is.EqualTo("kept"u8.ToArray()));
    Assert.That(dirs.Contains("doomed"), Is.False, "doomed subdir gone");
    Assert.That(files.ContainsKey("doomed/f1.txt"), Is.False, "doomed file gone");
    Assert.That(files.ContainsKey("doomed/f2.txt"), Is.False, "doomed file gone");
    Assert.That(files.ContainsKey("doomed/nested/inner.txt"), Is.False, "doomed deep file gone");
  }

  // ── multi-dmap allocation ──────────────────────────────────────────────

  // The writer's MinUsableBlocks=4096 fits in one dmap; this exercises that
  // single-dmap allocation still works after the multi-dmap walk extension.
  [Test, Category("RoundTrip")]
  public void Add_LargeFile_SpansMultipleBlocksWithinDmap_RoundTrips() {
    using var img = BuildImage(("seed.txt", "1"u8.ToArray()));
    var d = new JfsFormatDescriptor();

    // 32 KB file = 8 blocks @ 4 KB.
    var bigData = new byte[32 * 1024];
    for (var i = 0; i < bigData.Length; i++) bigData[i] = (byte)(i & 0xFF);
    ((IArchiveModifiable)d).Add(img, [ArchiveInputInfo.InMemory("big.bin", bigData)]);

    var files = ReadAll(img);
    Assert.That(files["big.bin"], Is.EqualTo(bigData), "large multi-block file round-trips intact");
  }

  // ── honest fallbacks for genuine multi-week scope ─────────────────────

  [Test, Category("ErrorHandling")]
  public void Add_InlineDtrootSplit_StillThrowsClean() {
    // Fill inline dtroot to capacity (8 short-name entries) — adding a 9th must throw.
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
      "Inline dtroot at capacity must throw NotSupportedException with 'split' in the message.");
  }
}
