using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Snapshots created and destroyed by the editor (<c>&lt;dataset&gt;/.zfs/snapshot/&lt;name&gt;</c> added as a
/// directory, or removed). The reader checks the head and every snapshot; OpenZFS checks the pools —
/// <c>zdb -bcc</c>, <c>zdb -d</c>, the kernel listing, comparing and destroying the snapshots — and
/// <c>zdb -bcc</c> again after the kernel's own changes.
/// </summary>
[TestFixture]
public sealed class ZfsSnapshotTests {
  private static readonly ZfsFormatDescriptor Descriptor = new();

  private static ArchiveInputInfo Directory(string path) => new(path, path, IsDirectory: true);

  private static Dictionary<string, string> Files(byte[] image) {
    using var reader = new ZfsReader(new MemoryStream(image, writable: false));
    Assert.That(reader.Warnings, Is.Empty);
    return reader.Entries.Where(e => !e.IsDirectory && e.LinkTarget == null)
      .ToDictionary(e => e.Name, e => ZfsOracle.Sha(reader.Extract(e)), StringComparer.Ordinal);
  }

  /// <summary>The files under <paramref name="prefix"/>, keyed by the path below it.</summary>
  private static Dictionary<string, string> Under(Dictionary<string, string> files, string prefix) =>
    files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
      .ToDictionary(f => f.Key[prefix.Length..], f => f.Value, StringComparer.Ordinal);

  /// <summary>The head's own files of a dataset (no snapshot, no child dataset given in <paramref name="children"/>).</summary>
  private static Dictionary<string, string> Head(Dictionary<string, string> files, string dataset) =>
    files.Where(f => f.Key.StartsWith(dataset + "/", StringComparison.Ordinal) && !f.Key.Contains("/.zfs/", StringComparison.Ordinal))
      .ToDictionary(f => f.Key[(dataset.Length + 1)..], f => f.Value, StringComparer.Ordinal);

  private static readonly byte[] Later = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 4000).Select(i => $"later {i % 9}\n")));

  /// <summary>
  /// cwbmain: snapshot c-lz4@made taken (c-lz4@before already exists), then a file removed and one
  /// added (the removed one goes to the deadlist under @made).
  /// </summary>
  private static byte[] CreateThenEdit(out Dictionary<string, string> before) {
    var image = ZfsReferencePools.Copy("cwbmain");
    before = Files(image.ToArray());
    Descriptor.Add(image, [Directory("c-lz4/.zfs/snapshot/made")]);
    Descriptor.Remove(image, ["c-lz4/rand.bin"]);
    Descriptor.Add(image, [ArchiveInputInfo.InMemory("c-lz4/later.txt", Later)]);
    return image.ToArray();
  }

  // ── reader ───────────────────────────────────────────────────────────────────────────

  [Test]
  public void Given_ADataset_When_ASnapshotIsCreated_Then_ItHoldsTheHeadAsItWas() {
    var image = ZfsReferencePools.Copy("cwbmain");
    var before = Files(image.ToArray());
    Descriptor.Add(image, [Directory("c-lz4/.zfs/snapshot/made"), Directory("c-off/.zfs/snapshot/first")]);
    var after = Files(image.ToArray());
    Assert.That(Under(after, "c-lz4/.zfs/snapshot/made/"), Is.EquivalentTo(Head(before, "c-lz4")));
    Assert.That(Under(after, "c-off/.zfs/snapshot/first/"), Is.EquivalentTo(Head(before, "c-off")));
    Assert.That(after.Where(f => !f.Key.Contains("/made/", StringComparison.Ordinal) && !f.Key.Contains("/first/", StringComparison.Ordinal)),
      Is.EquivalentTo(before));
  }

  [Test]
  public void Given_ANewSnapshot_When_TheHeadChangesAfterwards_Then_TheSnapshotKeepsTheOldFiles() {
    var image = CreateThenEdit(out var before);
    var after = Files(image);
    Assert.That(Under(after, "c-lz4/.zfs/snapshot/made/"), Is.EquivalentTo(Head(before, "c-lz4")));
    Assert.That(Head(after, "c-lz4"), Does.Not.ContainKey("rand.bin").And.ContainKey("later.txt"));
    ZfsAccountingProbe.AssertMovedTogether(ZfsAccountingProbe.Probe(ZfsReferencePools.ImageBytes("cwbmain")), ZfsAccountingProbe.Probe(image));
  }

  [Test]
  public void Given_ASnapshot_When_Destroyed_Then_ItIsGoneAndTheHeadIsUntouched() {
    var image = ZfsReferencePools.Copy("cwbmain");
    var before = Files(image.ToArray());
    Descriptor.Remove(image, ["c-lz4/.zfs/snapshot/before"]);
    var after = Files(image.ToArray());
    Assert.That(after, Is.EquivalentTo(before.Where(f => !f.Key.StartsWith("c-lz4/.zfs/snapshot/before/", StringComparison.Ordinal))));
  }

  [Test]
  public void Given_ThreeGenerations_When_TheMiddleAndThenTheNewestSnapshotIsDestroyed_Then_TheOthersStayIntact() {
    var image = new MemoryStream();
    image.Write(CreateThenEdit(out _));
    var snapshotted = Files(image.ToArray());
    // before ← made ← head: destroying "before" makes "made" the oldest
    Descriptor.Remove(image, ["c-lz4/.zfs/snapshot/before"]);
    var afterMiddle = Files(image.ToArray());
    Assert.That(afterMiddle, Is.EquivalentTo(snapshotted.Where(f => !f.Key.StartsWith("c-lz4/.zfs/snapshot/before/", StringComparison.Ordinal))));
    Descriptor.Remove(image, ["c-lz4/.zfs/snapshot/made"]);
    var afterNewest = Files(image.ToArray());
    Assert.That(afterNewest, Is.EquivalentTo(afterMiddle.Where(f => !f.Key.StartsWith("c-lz4/.zfs/snapshot/made/", StringComparison.Ordinal))));
  }

  [Test]
  public void Given_AVersion28Pool_When_ASnapshotIsCreatedAndDestroyed_Then_TheFilesStay() {
    var image = ZfsReferencePools.Copy("cwbv28");
    var before = Files(image.ToArray());
    Descriptor.Add(image, [Directory(".zfs/snapshot/s1")]);
    var withSnapshot = Files(image.ToArray());
    Assert.That(Under(withSnapshot, ".zfs/snapshot/s1/"), Is.EquivalentTo(before.Where(f => !f.Key.StartsWith("zpl3/", StringComparison.Ordinal))));
    Descriptor.Remove(image, ["dir/sub/deep.txt"]);
    Descriptor.Remove(image, [".zfs/snapshot/s1"]);
    var after = Files(image.ToArray());
    Assert.That(after, Is.EquivalentTo(before.Where(f => f.Key != "dir/sub/deep.txt")));
  }

  [Test]
  public void Given_AnExistingOrAbsentName_When_CreatedOrDestroyed_Then_NothingChanges() {
    var image = ZfsReferencePools.Copy("cwbmain");
    var original = image.ToArray();
    Descriptor.Add(image, [Directory("c-lz4/.zfs/snapshot/before"), Directory("c-lz4/.zfs"), Directory("c-lz4/.zfs/snapshot")]);
    Descriptor.Remove(image, ["c-lz4/.zfs/snapshot/never"]);
    Assert.That(image.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Given_ASnapshotAndAFileChangeInOneDataset_When_Edited_Then_TheEditIsRefusedUntouched() {
    var image = ZfsReferencePools.Copy("cwbmain");
    var original = image.ToArray();
    Assert.Throws<NotSupportedException>(() => Descriptor.Add(image, [Directory("c-lz4/.zfs/snapshot/made"), ArchiveInputInfo.InMemory("c-lz4/x.txt", Later)]));
    Assert.Throws<NotSupportedException>(() => Descriptor.Add(image, [ArchiveInputInfo.InMemory("c-lz4/.zfs/snapshot/before/x.txt", Later)]));
    Assert.Throws<ArgumentException>(() => Descriptor.Add(image, [Directory("c-lz4/.zfs/snapshot/bad@name")]));
    Assert.Throws<NotSupportedException>(() => Descriptor.Add(image, [ArchiveInputInfo.InMemory("c-lz4/.zfs/snapshot/file", Later)]));
    Assert.That(image.ToArray(), Is.EqualTo(original));
  }

  // ── OpenZFS ──────────────────────────────────────────────────────────────────────────

  private const string ListSnapshots = "zfs list -H -t snapshot -o name | sed 's/^/SNAP /'";

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_CreatedSnapshots_When_OpenZfsChecksAndDestroysThem_Then_NothingLeaks() {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var image = CreateThenEdit(out _);
    var stream = new MemoryStream();
    stream.Write(image);
    Descriptor.Add(stream, [Directory("c-off/.zfs/snapshot/first")]);
    image = stream.ToArray();
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbmain");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbmain");
      var expected = Files(image).Where(f => !f.Key.Contains("/.zfs/", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
      var made = Under(Files(image), "c-lz4/.zfs/snapshot/made/");
      var served = ZfsOracle.KernelManifest(dir, wsl, "cwbmain", out var log, after:
        $"{ListSnapshots}; " +
        "( cd $M/c-lz4/.zfs/snapshot/made && find . -type f | sort | while read -r p; do echo \"MADE $(sha256sum < \"$p\" | cut -d' ' -f1) ${p#./}\"; done ); " +
        "zfs destroy cwbmain/c-lz4@made && zfs destroy cwbmain/c-lz4@before && zfs destroy cwbmain/c-off@first && " +
        "zfs get -Hp -o value usedbysnapshots cwbmain/c-lz4 cwbmain/c-off | sed 's/^/SNAPUSED /'; " +
        "zpool sync cwbmain && echo DESTROYED", download: true);
      Assert.That(served, Is.EquivalentTo(expected));
      Assert.That(log, Does.Contain("SNAP cwbmain/c-lz4@before").And.Contain("SNAP cwbmain/c-lz4@made").And.Contain("SNAP cwbmain/c-off@first"), log);
      var kernelMade = log.Split('\n').Where(l => l.StartsWith("MADE ", StringComparison.Ordinal))
        .ToDictionary(l => l[70..].TrimEnd('\r'), l => l[5..69], StringComparer.Ordinal);
      Assert.That(kernelMade, Is.EquivalentTo(made));
      Assert.That(log, Does.Contain("DESTROYED"), log);
      Assert.That(log.Split('\n').Where(l => l.StartsWith("SNAPUSED ", StringComparison.Ordinal)).Select(l => l.Trim()), Is.EqualTo(new[] { "SNAPUSED 0", "SNAPUSED 0" }), log);
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbmain");
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_DestroyedSnapshots_When_OpenZfsChecksThePool_Then_NothingLeaksAndItSnapshotsAgain() {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    // the middle one first (its next is a snapshot), then the oldest (its next is the head)
    var stream = new MemoryStream();
    stream.Write(CreateThenEdit(out _));
    Descriptor.Remove(stream, ["c-lz4/.zfs/snapshot/before"]);
    var middle = stream.ToArray();
    Descriptor.Remove(stream, ["c-lz4/.zfs/snapshot/made"]);
    var last = stream.ToArray();
    foreach (var (image, left) in new[] { (middle, "SNAP cwbmain/c-lz4@made"), (last, "") }) {
      var (dir, wsl) = ZfsOracle.Stage(image, "cwbmain");
      try {
        ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
        ZfsOracle.AssertFeatureCounts(wsl, "cwbmain");
        var expected = Files(image).Where(f => !f.Key.Contains("/.zfs/", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var served = ZfsOracle.KernelManifest(dir, wsl, "cwbmain", out var log, after:
          $"{ListSnapshots}; zfs destroy -r cwbmain/c-lz4@made 2>/dev/null; " +
          "zfs get -Hp -o value usedbysnapshots cwbmain/c-lz4 | sed 's/^/SNAPUSED /'; " +
          "zfs snapshot cwbmain/c-lz4@kernel && rm $M/c-lz4/later.txt && zpool sync cwbmain && echo RESNAPPED", download: true);
        Assert.That(served, Is.EquivalentTo(expected));
        Assert.That(log, Does.Not.Contain("SNAP cwbmain/c-lz4@before"), log);
        if (left.Length > 0) Assert.That(log, Does.Contain(left), log);
        Assert.That(log, Does.Contain("SNAPUSED 0").And.Contain("RESNAPPED"), log);
        ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
        ZfsOracle.AssertFeatureCounts(wsl, "cwbmain");
      } finally {
        ZfsOracle.Cleanup(dir);
      }
    }
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_AVersion28PoolSnapshot_When_OpenZfsChecksIt_Then_ItIsCleanAndDestroysCleanly() {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var stream = ZfsReferencePools.Copy("cwbv28");
    Descriptor.Add(stream, [Directory(".zfs/snapshot/s1"), Directory("zpl3/.zfs/snapshot/s1")]);
    Descriptor.Remove(stream, ["dir/sub/deep.txt"]);
    var image = stream.ToArray();
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbv28");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbv28");
      var expected = Files(image).Where(f => !f.Key.Contains(".zfs/", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
      var served = ZfsOracle.KernelManifest(dir, wsl, "cwbv28", out var log, after:
        $"{ListSnapshots}; test -f $M/.zfs/snapshot/s1/dir/sub/deep.txt && echo HELD; " +
        "zfs destroy cwbv28@s1 && zfs destroy cwbv28/zpl3@s1 && " +
        "zfs get -Hp -o value usedbysnapshots cwbv28 cwbv28/zpl3 | sed 's/^/SNAPUSED /'; zpool sync cwbv28 && echo DESTROYED", download: true);
      Assert.That(served, Is.EquivalentTo(expected));
      Assert.That(log, Does.Contain("SNAP cwbv28@s1").And.Contain("SNAP cwbv28/zpl3@s1").And.Contain("HELD").And.Contain("DESTROYED"), log);
      Assert.That(log.Split('\n').Where(l => l.StartsWith("SNAPUSED ", StringComparison.Ordinal)).Select(l => l.Trim()), Is.EqualTo(new[] { "SNAPUSED 0", "SNAPUSED 0" }), log);
      ZfsOracle.AssertNoLeaks(wsl, "cwbv28");
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}
