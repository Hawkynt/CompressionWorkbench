using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Freeing what a snapshot still holds (onto the deadlist) and gang blocks: removals in
/// <c>cwbmain</c>'s <c>c-lz4</c> dataset, whose snapshot <c>before</c> keeps the old files, and in
/// <c>cwbgang</c>, whose data blocks are gang blocks. The reader checks the head and the snapshot;
/// OpenZFS checks the pools — <c>zdb -bcc</c>, <c>zdb -d</c>, the kernel — and, after the kernel
/// destroys the snapshot (which frees exactly the deadlist), <c>zdb -bcc</c> again.
/// </summary>
[TestFixture]
public sealed class ZfsHeldFreeTests {
  private static readonly ZfsFormatDescriptor Descriptor = new();

  private static readonly (string Path, byte[] Data)[] Adds = [
    ("c-lz4/after/new.txt", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"after {i % 7}\n")))),
  ];
  private static readonly string[] Removes = ["c-lz4/rand.bin", "c-lz4/sparse.bin", "c-lz4/dir", "c-lz4/hardlink.txt"];

  private static Dictionary<string, string> Manifest(string pool) =>
    ZfsReferencePools.Manifest(pool).Where(m => m.Kind == 'F').ToDictionary(m => m.Path, m => m.Sha256!, StringComparer.Ordinal);

  private static (byte[] Image, Dictionary<string, string> Head, Dictionary<string, string> Snapshot) EditMain() {
    var image = ZfsReferencePools.Copy("cwbmain");
    Descriptor.Remove(image, Removes);
    Descriptor.Add(image, Adds.Select(a => ArchiveInputInfo.InMemory(a.Path, a.Data)).ToList());
    var all = Manifest("cwbmain");
    var snapshot = all.Where(e => e.Key.Contains(".zfs/", StringComparison.Ordinal)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    var head = all.Where(e => !e.Key.Contains(".zfs/", StringComparison.Ordinal)
      && !Removes.Any(r => e.Key == r || e.Key.StartsWith(r + "/", StringComparison.Ordinal))).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    foreach (var (path, data) in Adds) head[path] = ZfsOracle.Sha(data);
    return (image.ToArray(), head, snapshot);
  }

  private static (byte[] Image, Dictionary<string, string> Expected) EditGang() {
    var image = ZfsReferencePools.Copy("cwbgang");
    Descriptor.Remove(image, ["big.txt", "text.txt", "rand.bin"]);
    var added = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"gang {i % 5}\n")));
    Descriptor.Add(image, [ArchiveInputInfo.InMemory("after.txt", added)]);
    var expected = Manifest("cwbgang");
    foreach (var gone in new[] { "big.txt", "text.txt", "rand.bin" }) expected.Remove(gone);
    expected["after.txt"] = ZfsOracle.Sha(added);
    return (image.ToArray(), expected);
  }

  /// <summary><c>dedup/copy.txt</c> shares its one block with <c>dedup/text.txt</c>; the other two files are unique.</summary>
  private static readonly string[] OneCopy = ["dedup/copy.txt"];
  private static readonly string[] EveryDedupFile = ["dedup/copy.txt", "dedup/text.txt", "dedup/rand.bin", "dedup/tiny.txt"];

  private static (byte[] Image, Dictionary<string, string> Expected) EditDedup(string[] removes) {
    var image = ZfsReferencePools.Copy("cwbmain");
    Descriptor.Remove(image, removes);
    var expected = Manifest("cwbmain").Where(e => !e.Key.Contains(".zfs/", StringComparison.Ordinal) && !removes.Contains(e.Key))
      .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    return (image.ToArray(), expected);
  }

  private static Dictionary<string, string> Read(byte[] image, Func<string, bool> which) {
    using var reader = new ZfsReader(new MemoryStream(image, writable: false));
    Assert.That(reader.Warnings, Is.Empty);
    return reader.Entries.Where(e => !e.IsDirectory && e.LinkTarget == null && which(e.Name))
      .ToDictionary(e => e.Name, e => ZfsOracle.Sha(reader.Extract(e)), StringComparer.Ordinal);
  }

  [Test]
  public void Given_FilesASnapshotHolds_When_Removed_Then_TheHeadLosesThemAndTheSnapshotKeepsThem() {
    var (image, head, snapshot) = EditMain();
    Assert.That(Read(image, n => !n.Contains(".zfs/", StringComparison.Ordinal)), Is.EquivalentTo(head));
    Assert.That(Read(image, n => n.Contains(".zfs/", StringComparison.Ordinal)), Is.EquivalentTo(snapshot));
    ZfsAccountingProbe.AssertMovedTogether(ZfsAccountingProbe.Probe(ZfsReferencePools.ImageBytes("cwbmain")), ZfsAccountingProbe.Probe(image));
  }

  [Test]
  public void Given_GangBlocks_When_TheirFilesAreRemoved_Then_TheReaderSeesTheEdit() {
    var (image, expected) = EditGang();
    Assert.That(Read(image, _ => true), Is.EquivalentTo(expected));
    ZfsAccountingProbe.AssertMovedTogether(ZfsAccountingProbe.Probe(ZfsReferencePools.ImageBytes("cwbgang")), ZfsAccountingProbe.Probe(image));
  }

  [Test]
  public void Given_ADeduplicatedCopy_When_Removed_Then_TheOtherCopyStillReads() {
    var (image, expected) = EditDedup(OneCopy);
    Assert.That(Read(image, n => !n.Contains(".zfs/", StringComparison.Ordinal)), Is.EquivalentTo(expected));
    ZfsAccountingProbe.AssertMovedTogether(ZfsAccountingProbe.Probe(ZfsReferencePools.ImageBytes("cwbmain")), ZfsAccountingProbe.Probe(image));
  }

  [Test]
  public void Given_EveryDeduplicatedFile_When_Removed_Then_TheRestStillReads() {
    var (image, expected) = EditDedup(EveryDedupFile);
    Assert.That(Read(image, n => !n.Contains(".zfs/", StringComparison.Ordinal)), Is.EquivalentTo(expected));
    ZfsAccountingProbe.AssertMovedTogether(ZfsAccountingProbe.Probe(ZfsReferencePools.ImageBytes("cwbmain")), ZfsAccountingProbe.Probe(image));
  }

  // ── OpenZFS ───────────────────────────────────────────────────────────────────────────

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_OneOfTwoDedupReferencesFreed_When_OpenZfsChecksThePool_Then_TheEntryIsUniqueAndNothingLeaks() {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var (image, expected) = EditDedup(OneCopy);
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbmain");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbmain");
      var tables = ZfsOracle.DedupTables(wsl, "cwbmain");
      Assert.That(tables, Does.Contain("DDT-sha256-zap-unique: 3 entries").And.Not.Contain("refcnt 2"), tables);
      // the kernel deduplicates against our table: a new copy takes the entry back to two references
      var served = ZfsOracle.KernelManifest(dir, wsl, "cwbmain", out var log,
        after: "cp $M/dedup/text.txt $M/dedup/again.txt && rm $M/dedup/rand.bin && zpool sync cwbmain && echo DEDUPED", download: true);
      Assert.That(served.Where(e => !e.Key.Contains(".zfs/", StringComparison.Ordinal)), Is.EquivalentTo(expected));
      Assert.That(log, Does.Contain("DEDUPED"), log);
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      tables = ZfsOracle.DedupTables(wsl, "cwbmain");
      Assert.That(tables, Does.Contain("DDT-sha256-zap-duplicate: 1 entries").And.Contain("DDT-sha256-zap-unique: 1 entries"), tables);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_EveryDedupReferenceFreed_When_OpenZfsChecksThePool_Then_TheTablesAreGoneAndNothingLeaks() {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var (image, expected) = EditDedup(EveryDedupFile);
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbmain");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbmain");
      var tables = ZfsOracle.DedupTables(wsl, "cwbmain");
      Assert.That(tables, Does.Contain("All DDTs are empty"), tables);
      // and the kernel builds new tables from nothing
      var served = ZfsOracle.KernelManifest(dir, wsl, "cwbmain", out var log,
        after: "seq 1 20000 > $M/dedup/a.txt && cp $M/dedup/a.txt $M/dedup/b.txt && zpool sync cwbmain && echo DEDUPED", download: true);
      Assert.That(served.Where(e => !e.Key.Contains(".zfs/", StringComparison.Ordinal)), Is.EquivalentTo(expected));
      Assert.That(log, Does.Contain("DEDUPED"), log);
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      tables = ZfsOracle.DedupTables(wsl, "cwbmain");
      Assert.That(tables, Does.Contain("DDT-sha256-zap-duplicate"), tables);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_HeldFrees_When_OpenZfsChecksThePoolAndDestroysTheSnapshot_Then_NothingLeaks() {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var (image, head, _) = EditMain();
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbmain");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbmain");
      // the kernel frees the snapshot through its deadlist: anything missing there would leak
      var served = ZfsOracle.KernelManifest(dir, wsl, "cwbmain", out var log,
        after: "zfs destroy cwbmain/c-lz4@before && echo DESTROYED", download: true);
      Assert.That(served, Is.EquivalentTo(head));
      Assert.That(log, Does.Contain("DESTROYED"), log);
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbmain");
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_GangFrees_When_OpenZfsChecksThePool_Then_ItIsClean() {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var (image, expected) = EditGang();
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbgang");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbgang");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbgang");
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbgang"), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}
