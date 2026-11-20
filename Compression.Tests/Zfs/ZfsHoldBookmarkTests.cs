using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Snapshots with user holds and bookmarks, destroyed and created by the editor, and file edits
/// under a bookmark that counts what the head frees. The pool is prepared by the OpenZFS kernel
/// (snapshots, bookmarks — one of them left behind by a destroyed snapshot — and a hold); the
/// kernel then does the same operation itself on a copy, and the space it reports for every
/// bookmark and snapshot must match what it reports after our edit. <c>zdb -bcc</c> and
/// <c>zdb -d</c> check both.
/// </summary>
[TestFixture, Category("Slow"), Category("ExternalTool")]
public sealed class ZfsHoldBookmarkTests {
  private static readonly ZfsFormatDescriptor Descriptor = new();

  /// <summary>
  /// c-lz4: @before (exists), @y, @z with files changing between them; bookmarks #bb, #by, #bz of
  /// those three; @y destroyed (so #by lies between @before and @z); a user hold "keep" on @z; and
  /// after @z one more change.
  /// </summary>
  private const string Prepare =
    "D=$M/c-lz4; head -c 300000 /dev/urandom > $D/x1; zpool sync cwbmain; zfs snapshot cwbmain/c-lz4@y; " +
    "head -c 200000 /dev/urandom > $D/x2; rm $D/x1; zpool sync cwbmain; zfs snapshot cwbmain/c-lz4@z; " +
    "head -c 100000 /dev/urandom > $D/x3; rm $D/x2; " +
    "zfs bookmark cwbmain/c-lz4@before cwbmain/c-lz4#bb && zfs bookmark cwbmain/c-lz4@y cwbmain/c-lz4#by && " +
    "zfs bookmark cwbmain/c-lz4@z cwbmain/c-lz4#bz && zfs destroy cwbmain/c-lz4@y && zfs hold keep cwbmain/c-lz4@z && " +
    "zpool sync cwbmain && echo PREPARED";

  /// <summary>Space the kernel reports for the head, its snapshots and its bookmarks, one "SPACE name property value" line each.</summary>
  private const string Report =
    "for p in written@before written#bb written#by written#bz usedbysnapshots usedbydataset referenced; do " +
    "zfs get -Hp -o property,value $p cwbmain/c-lz4 | sed 's/^/SPACE head /'; done; " +
    "zfs list -Hp -t snapshot -o name,used,referenced -r cwbmain/c-lz4 | sed 's/^/SPACE snap /'; " +
    "zfs holds -H -r cwbmain/c-lz4 | sed 's/^/HOLD /'; echo REPORTED";

  private static byte[]? _prepared;

  private static byte[] Prepared() {
    if (_prepared != null) return _prepared;
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var (dir, wsl) = ZfsOracle.Stage(ZfsReferencePools.ImageBytes("cwbmain"), "cwbmain");
    try {
      ZfsOracle.KernelManifest(dir, wsl, "cwbmain", out var log, after: Prepare, download: true);
      Assert.That(log, Does.Contain("PREPARED"), log);
      return _prepared = File.ReadAllBytes(Path.Combine(dir, "cwbmain.img"));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  /// <summary>Runs <paramref name="script"/> then <see cref="Report"/> in the kernel on <paramref name="image"/>; zdb checks the pool before and after.</summary>
  private static List<string> Space(byte[] image, string script) {
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbmain");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbmain");
      ZfsOracle.KernelManifest(dir, wsl, "cwbmain", out var log, after: $"{script} {Report}", download: true);
      Assert.That(log, Does.Contain("REPORTED"), log);
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbmain");
      return log.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("SPACE ", StringComparison.Ordinal) || l.StartsWith("HOLD ", StringComparison.Ordinal))
        .Select(l => l.StartsWith("HOLD ", StringComparison.Ordinal) ? string.Join(' ', l.Split('\t').Take(2)) : l).ToList();
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  private static byte[] Edit(byte[] image, Action<MemoryStream> edit) {
    var stream = new MemoryStream();
    stream.Write(image);
    edit(stream);
    return stream.ToArray();
  }

  [Test]
  public void Given_AHeldBookmarkedSnapshot_When_WeDestroyIt_Then_TheKernelReportsWhatItsOwnDestroyLeaves() {
    var prepared = Prepared();
    var ours = Space(Edit(prepared, s => Descriptor.Remove(s, ["c-lz4/.zfs/snapshot/z"])), "");
    var kernel = Space(prepared, "zfs release keep cwbmain/c-lz4@z && zfs destroy cwbmain/c-lz4@z;");
    Assert.That(ours, Has.Some.StartsWith("SPACE head written#by"));
    Assert.That(ours, Has.None.StartsWith("SPACE snap cwbmain/c-lz4@z").And.None.StartsWith("HOLD"));
    Assert.That(ours, Is.EqualTo(kernel));
  }

  [Test]
  public void Given_ABookmarkAfterTheNewestSnapshot_When_WeSnapshot_Then_TheKernelReportsWhatItsOwnSnapshotLeaves() {
    var prepared = Prepared();
    var withoutZ = Edit(prepared, s => Descriptor.Remove(s, ["c-lz4/.zfs/snapshot/z"]));
    var ours = Space(Edit(withoutZ, s => Descriptor.Add(s, [new ArchiveInputInfo("c-lz4/.zfs/snapshot/w", "c-lz4/.zfs/snapshot/w", IsDirectory: true)])), "");
    var kernel = Space(withoutZ, "zfs snapshot cwbmain/c-lz4@w;");
    Assert.That(ours, Has.Some.StartsWith("SPACE snap cwbmain/c-lz4@w"));
    // The kernel writes some metadata of the head when it mounts it: before its own @w, after
    // ours — so our @w uniquely holds those few blocks. Everything else must match, and the
    // difference must be exactly that.
    static bool Shifted(string l) => l.StartsWith("SPACE head usedbysnapshots", StringComparison.Ordinal) || l.StartsWith("SPACE snap cwbmain/c-lz4@w", StringComparison.Ordinal);
    Assert.That(ours.Where(l => !Shifted(l)), Is.EqualTo(kernel.Where(l => !Shifted(l))));
    long Number(List<string> lines, string prefix, int field) => long.Parse(lines.Single(l => l.StartsWith(prefix, StringComparison.Ordinal)).Split('\t')[field]);
    var snapshotsDelta = Number(ours, "SPACE head usedbysnapshots", 1) - Number(kernel, "SPACE head usedbysnapshots", 1);
    var wDelta = Number(ours, "SPACE snap cwbmain/c-lz4@w", 1) - Number(kernel, "SPACE snap cwbmain/c-lz4@w", 1);
    Assert.That(snapshotsDelta, Is.EqualTo(wDelta), string.Join('\n', ours));
    Assert.That(Number(ours, "SPACE snap cwbmain/c-lz4@w", 2), Is.EqualTo(Number(kernel, "SPACE snap cwbmain/c-lz4@w", 2)));
  }

  [Test]
  public void Given_ABookmarkAtTheNewestSnapshot_When_WeRemoveFiles_Then_ItCountsWhatTheHeadFreed() {
    var prepared = Prepared();
    var released = Edit(prepared, s => Descriptor.Remove(s, ["c-lz4/rand.bin", "c-lz4/x3"]));
    // a bookmark at the newest snapshot sees exactly what that snapshot sees (written@z comes from
    // @z's deadlist, written#bz from the bookmark's own count of what the head freed)
    var space = Space(released, "zfs get -Hp -o property,value written@z cwbmain/c-lz4 | sed 's/^/SPACE head /';");
    string Value(string property) => space.Single(l => l.StartsWith($"SPACE head {property}\t", StringComparison.Ordinal)).Split('\t')[1];
    Assert.That(Value("written#bz"), Is.EqualTo(Value("written@z")), string.Join('\n', space));
  }
}
