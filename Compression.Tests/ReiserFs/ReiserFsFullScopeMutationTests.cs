using System.Text;
using Compression.Registry;

namespace Compression.Tests.ReiserFs;

/// <summary>
/// Full-scope mutation tests for the ReiserFS modifier. Exercises the paths
/// that previously fell back to <see cref="NotSupportedException"/> on the old
/// leaf-only modifier — nested directory creation, INDIRECT-item-sized file
/// bodies, leaf split (many adds), leaf merge (many removes), root tree-height
/// growth (1000+ files), tail-packed small files alongside large ones. The
/// rebuild-based modifier accepts every case; reiserfsck-conformance for the
/// resulting images is checked in <c>ReiserFsPostMutationExternalTests</c>.
/// </summary>
[TestFixture]
public class ReiserFsFullScopeMutationTests {

  private static MemoryStream BuildSeed(params (string Name, byte[] Data)[] files) {
    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    return ms;
  }

  private static IArchiveModifiable Modifier() => new FileSystem.ReiserFs.ReiserFsFormatDescriptor();

  private static void Add(IArchiveModifiable mod, Stream image, string name, byte[] data) {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, data);
      mod.Add(image, [new ArchiveInputInfo(tmp, name, false)]);
    } finally {
      File.Delete(tmp);
    }
  }

  // ── Nested paths ────────────────────────────────────────────────────────

  /// <summary>
  /// Given a fresh ReiserFS image, when AddFile inserts an entry into a deeply
  /// nested path (docs/api/reference.txt), then the modifier creates every
  /// intermediate directory object and the file round-trips at its full path.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NestedPath_CreatesIntermediateDirectories() {
    using var ms = BuildSeed(("readme.txt", "root"u8.ToArray()));

    Add(Modifier(), ms, "docs/api/reference.txt", "deep content"u8.ToArray());

    ms.Position = 0;
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
    Assert.That(byPath, Does.ContainKey("readme.txt"));
    Assert.That(byPath, Does.ContainKey("docs/api/reference.txt"));
    Assert.That(byPath["docs/api/reference.txt"], Is.EqualTo("deep content"u8.ToArray()));

    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();
    Assert.That(dirs, Does.Contain("docs"));
    Assert.That(dirs, Does.Contain("docs/api"));
  }

  /// <summary>
  /// Adding multiple files into one nested directory shares the parent dir
  /// object (no duplicates) — verifies the modifier's BuildTree path reuses
  /// existing directory nodes for additional adds.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NestedPath_SharedParent() {
    using var ms = BuildSeed();
    var mod = Modifier();

    Add(mod, ms, "data/a.txt", "alpha"u8.ToArray());
    Add(mod, ms, "data/b.txt", "beta"u8.ToArray());
    Add(mod, ms, "data/c.txt", "gamma"u8.ToArray());

    ms.Position = 0;
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var dirs = r.Entries.Where(e => e.IsDirectory).Select(e => e.Name.Replace('\\', '/')).ToList();
    Assert.That(dirs.Count(d => d == "data"), Is.EqualTo(1), "single shared parent");

    var files = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name.Replace('\\', '/')).ToHashSet();
    Assert.That(files, Does.Contain("data/a.txt"));
    Assert.That(files, Does.Contain("data/b.txt"));
    Assert.That(files, Does.Contain("data/c.txt"));
  }

  // ── INDIRECT-item file bodies ──────────────────────────────────────────

  /// <summary>
  /// Given a fresh image, when AddFile inserts a body > 4 KB, then the writer
  /// emits one or more INDIRECT items pointing at dedicated data blocks past
  /// the tree, and the reader concatenates the data blocks back into the
  /// original payload truncated by sd_size.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IndirectItem_LargeBody_RoundTrips() {
    using var ms = BuildSeed();
    var payload = new byte[16 * 1024];
    new Random(1).NextBytes(payload);
    Add(Modifier(), ms, "big.bin", payload);

    ms.Position = 0;
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var entry = r.Entries.First(e => e.Name == "big.bin");
    Assert.That(r.Extract(entry), Is.EqualTo(payload));
  }

  /// <summary>
  /// Verifies that a body whose size is NOT a multiple of the block size still
  /// round-trips: the writer zero-pads the last block, the reader truncates by
  /// sd_size.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IndirectItem_NonAlignedTail_TruncatesCorrectly() {
    using var ms = BuildSeed();
    var payload = new byte[8 * 1024 + 137]; // partial last block
    new Random(2).NextBytes(payload);
    Add(Modifier(), ms, "tail.bin", payload);

    ms.Position = 0;
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var entry = r.Entries.First(e => e.Name == "tail.bin");
    Assert.That(r.Extract(entry), Is.EqualTo(payload),
      "INDIRECT body with partial last block must truncate to exactly the original size.");
  }

  // ── Tail-packed small files ─────────────────────────────────────────────

  /// <summary>
  /// Small files (≤ MaxDirectBody) go in DIRECT items packed inside leaves;
  /// large files (> MaxDirectBody) go in INDIRECT items with separate data
  /// blocks. Both can coexist in the same image and round-trip.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MixedDirectAndIndirect_BothRoundTrip() {
    using var ms = BuildSeed();
    var mod = Modifier();

    var tiny = "tiny"u8.ToArray();
    var huge = new byte[5000];
    new Random(3).NextBytes(huge);

    Add(mod, ms, "tiny.txt", tiny);
    Add(mod, ms, "huge.bin", huge);

    ms.Position = 0;
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var tinyEntry = r.Entries.First(e => e.Name == "tiny.txt");
    var hugeEntry = r.Entries.First(e => e.Name == "huge.bin");
    Assert.Multiple(() => {
      Assert.That(r.Extract(tinyEntry), Is.EqualTo(tiny), "DIRECT tail still works");
      Assert.That(r.Extract(hugeEntry), Is.EqualTo(huge), "INDIRECT body round-trips");
    });
  }

  // ── Leaf split (many adds) ──────────────────────────────────────────────

  /// <summary>
  /// Adding many files to a directory forces the writer to split items across
  /// multiple leaves and grow tree_height to 3 (root becomes an internal page
  /// above the leaves). All files must remain readable.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ManyAdds_TriggerLeafSplits() {
    using var ms = BuildSeed();
    var mod = Modifier();

    var expected = new Dictionary<string, byte[]>();
    for (var i = 0; i < 50; i++) {
      var name = $"file{i:D3}.txt";
      var data = Encoding.UTF8.GetBytes($"payload-{i}");
      Add(mod, ms, name, data);
      expected[name] = data;
    }

    ms.Position = 0;
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name, e => r.Extract(e));
    Assert.That(byPath, Has.Count.EqualTo(expected.Count));
    foreach (var (name, data) in expected)
      Assert.That(byPath[name], Is.EqualTo(data), $"content intact for {name}");
  }

  // ── Leaf merge (many removes) ───────────────────────────────────────────

  /// <summary>
  /// Removing many files from a multi-leaf image forces the writer to merge
  /// items back into fewer leaves; tree_height may collapse from 3 back to 2.
  /// Survivors must remain readable.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ManyRemoves_TriggerLeafMerges() {
    var files = new List<(string, byte[])>();
    for (var i = 0; i < 50; i++)
      files.Add(($"file{i:D3}.txt", Encoding.UTF8.GetBytes($"payload-{i}")));
    using var ms = BuildSeed([.. files]);

    var mod = Modifier();
    var toRemove = files.Take(40).Select(f => f.Item1).ToArray();
    mod.Remove(ms, toRemove);

    ms.Position = 0;
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var byPath = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(byPath, Has.Count.EqualTo(10), "10 survivors expected");
    for (var i = 40; i < 50; i++)
      Assert.That(byPath, Does.ContainKey($"file{i:D3}.txt"), $"survivor present: file{i:D3}.txt");
    for (var i = 0; i < 40; i++)
      Assert.That(byPath, Does.Not.ContainKey($"file{i:D3}.txt"), $"removed: file{i:D3}.txt");
  }

  // ── Root tree-height growth (~1000s of files) ────────────────────────

  /// <summary>
  /// Inserting 1000+ files into a single directory grows the tree past the
  /// single-internal-page configuration; even after that growth every file
  /// must round-trip. This exercises the deepest scope the writer supports
  /// today.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ManyFiles_GrowTreeHeight() {
    using var ms = BuildSeed();
    var mod = Modifier();

    const int fileCount = 1000;
    var expected = new Dictionary<string, byte[]>();
    for (var i = 0; i < fileCount; i++) {
      var name = $"f{i:D4}";
      var data = Encoding.UTF8.GetBytes($"c-{i:D4}");
      expected[name] = data;
    }
    // Single Add call so the rebuild loop happens once (perf — every Add is
    // O(N) on the existing entry list).
    var tmpFiles = new List<ArchiveInputInfo>(fileCount);
    var tmpHandles = new List<string>(fileCount);
    try {
      foreach (var (name, data) in expected) {
        var t = Path.GetTempFileName();
        File.WriteAllBytes(t, data);
        tmpHandles.Add(t);
        tmpFiles.Add(new ArchiveInputInfo(t, name, false));
      }
      mod.Add(ms, tmpFiles);
    } finally {
      foreach (var t in tmpHandles) try { File.Delete(t); } catch { }
    }

    ms.Position = 0;
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name, e => r.Extract(e));
    Assert.That(byPath, Has.Count.EqualTo(fileCount));
    // Spot check first / last / middle.
    foreach (var i in new[] { 0, 1, 250, 500, 999 }) {
      var name = $"f{i:D4}";
      Assert.That(byPath[name], Is.EqualTo(expected[name]), $"content intact for {name}");
    }
  }

  // ── Replace + remove + add interleave ──────────────────────────────────

  /// <summary>
  /// Add-with-same-name replaces the existing entry's bytes; remove deletes.
  /// Interleaved operations must produce the expected final state.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Interleaved_AddRemoveReplace() {
    using var ms = BuildSeed(("a.txt", "v1"u8.ToArray()), ("b.txt", "B"u8.ToArray()));
    var mod = Modifier();

    Add(mod, ms, "a.txt", "v2"u8.ToArray());            // replace
    Add(mod, ms, "c.txt", "C"u8.ToArray());             // add new
    mod.Remove(ms, ["b.txt"]);                          // remove

    ms.Position = 0;
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name, e => r.Extract(e));
    Assert.Multiple(() => {
      Assert.That(byPath, Does.ContainKey("a.txt"));
      Assert.That(byPath["a.txt"], Is.EqualTo("v2"u8.ToArray()), "replacement wins");
      Assert.That(byPath, Does.ContainKey("c.txt"));
      Assert.That(byPath, Does.Not.ContainKey("b.txt"), "removed");
    });
  }
}
