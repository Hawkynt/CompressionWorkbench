using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Removing cloned files from a pool with block cloning active. The OpenZFS kernel prepares
/// cwbmain (block cloning switched on: a file cloned twice in its dataset and once into a subdirectory);
/// our removals must leave the block reference table as the kernel's own removals do — the
/// kernel's <c>bcloneused</c>/<c>bclonesaved</c>, its feature state and the served files match —
/// and <c>zdb -bcc</c> stays clean, also after the kernel removes the remaining clones.
/// </summary>
[TestFixture, Category("Slow"), Category("ExternalTool")]
public sealed class ZfsBlockCloningTests {
  private static readonly ZfsFormatDescriptor Descriptor = new();

  private const string Prepare =
    "echo 1 > /sys/module/zfs/parameters/zfs_bclone_enabled; D=$M/c-off; " +
    "head -c 600000 /dev/urandom > $D/orig.bin; zpool sync cwbmain; " +
    "cp --reflink=always $D/orig.bin $D/clone1.bin && cp --reflink=always $D/orig.bin $D/clone2.bin && " +
    "mkdir -p $D/sub && cp --reflink=always $D/orig.bin $D/sub/clone3.bin && zpool sync cwbmain && " +
    "zpool get -Hp -o property,value bcloneused cwbmain && echo PREPARED";

  private const string Report =
    "zpool sync cwbmain; zpool get -Hp -o property,value bcloneused,bclonesaved,feature@block_cloning cwbmain | sed 's/^/SPACE /'; " +
    "for f in c-off/orig.bin c-off/clone1.bin c-off/clone2.bin c-off/sub/clone3.bin; do test -f $M/$f && echo \"FILE $f $(sha256sum < $M/$f | cut -d' ' -f1)\"; done; echo REPORTED";

  private static byte[]? _prepared;

  /// <summary>
  /// <c>zdb -d</c> as <see cref="ZfsOracle.AssertFeatureCounts"/>, except that zdb 2.2.2 does not
  /// count the block reference table's MOS objects as referenced: those, and only those, may be
  /// reported leaked (it does so for pools the kernel made, too).
  /// </summary>
  private static void AssertCounts(string dir, string wsl) {
    var brt = new HashSet<ulong>();
    using (var pool = new ZfsPool([new MemoryStream(File.ReadAllBytes(Path.Combine(dir, "cwbmain.img")), writable: false)], leaveOpen: false))
      foreach (var e in pool.Mos.Zap(1, "MOS object directory").Where(e => e.Name.StartsWith("com.fudosecurity:brt:vdev:", StringComparison.Ordinal))) {
        brt.Add(e.Value);
        brt.Add(BitConverter.ToUInt64(pool.Mos.RequireDnode(e.Value, "BRT vdev").Bonus, 0));
      }
    var d = FsInteropToolbox.RunWsl($"zdb -e -p {wsl} -d cwbmain 2>&1");
    var leaked = System.Text.RegularExpressions.Regex.Matches(d.StdOut, @"MOS object (\d+) \([^)]*\) leaked").Select(m => ulong.Parse(m.Groups[1].Value)).ToList();
    Assert.That(leaked, Is.SubsetOf(brt), d.StdOut);
    Assert.That(d.StdOut, Does.Not.Contain("mismatch").And.Not.Contain("error"), d.StdOut);
    if (leaked.Count == 0) Assert.That(d.ExitCode, Is.Zero, d.StdOut);
    var b = FsInteropToolbox.RunWsl($"zdb -e -p {wsl} -b cwbmain 2>&1");
    Assert.That(b.StdOut, Does.Not.Contain("space map refcount mismatch"), b.StdOut);
  }

  private static byte[] Prepared() {
    if (_prepared != null) return _prepared;
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var (dir, wsl) = ZfsOracle.Stage(ZfsReferencePools.ImageBytes("cwbmain"), "cwbmain");
    try {
      ZfsOracle.KernelManifest(dir, wsl, "cwbmain", out var log, after: Prepare, download: true);
      Assert.That(log, Does.Contain("PREPARED").And.Not.Contain("bcloneused\t0"), log);
      return _prepared = File.ReadAllBytes(Path.Combine(dir, "cwbmain.img"));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  private static List<string> Space(byte[] image, string script) {
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbmain");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      AssertCounts(dir, wsl);
      ZfsOracle.KernelManifest(dir, wsl, "cwbmain", out var log, after: $"{script} {Report}", download: true);
      Assert.That(log, Does.Contain("REPORTED"), log);
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      AssertCounts(dir, wsl);
      return log.Split('\n').Select(l => l.TrimEnd('\r'))
        .Where(l => l.StartsWith("SPACE ", StringComparison.Ordinal) || l.StartsWith("FILE ", StringComparison.Ordinal)).ToList();
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  /// <summary>After our removal, then the kernel removing the rest, the pool must be clean too.</summary>
  private static void AssertRestRemovable(byte[] image, string[] rest) {
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbmain");
    try {
      ZfsOracle.KernelManifest(dir, wsl, "cwbmain", out var log,
        after: string.Concat(rest.Select(r => $"rm -f $M/{r}; ")) + "zpool sync cwbmain && echo REMOVED", download: true);
      Assert.That(log, Does.Contain("REMOVED"), log);
      ZfsOracle.AssertNoLeaks(wsl, "cwbmain");
      AssertCounts(dir, wsl);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  private static byte[] Removed(byte[] image, string[] paths) {
    var stream = new MemoryStream();
    stream.Write(image);
    Descriptor.Remove(stream, paths);
    return stream.ToArray();
  }

  private static readonly string[] All = ["c-off/orig.bin", "c-off/clone1.bin", "c-off/clone2.bin", "c-off/sub/clone3.bin"];

  [Test]
  public void Given_ABlockClonedThreeTimes_When_OneCloneIsRemoved_Then_TheTableMatchesTheKernels() {
    var prepared = Prepared();
    var ours = Removed(prepared, ["c-off/clone1.bin"]);
    var kernel = Space(prepared, "rm $M/c-off/clone1.bin;");
    Assert.That(Space(ours, ""), Is.EqualTo(kernel));
    AssertRestRemovable(ours, All.Skip(1).ToArray());
  }

  [Test]
  public void Given_AClonedBlock_When_AllButOneReferenceGoes_Then_TheLastFileStillReads() {
    var prepared = Prepared();
    string[] three = ["c-off/orig.bin", "c-off/clone1.bin", "c-off/sub/clone3.bin"];
    var ours = Removed(prepared, three);
    var kernel = Space(prepared, string.Concat(three.Select(r => $"rm $M/{r}; ")));
    var space = Space(ours, "");
    Assert.That(space, Is.EqualTo(kernel));
    Assert.That(space, Has.Some.StartsWith("FILE c-off/clone2.bin"));
    AssertRestRemovable(ours, ["c-off/clone2.bin"]);
  }

  [Test]
  public void Given_EveryReference_When_Removed_Then_TheTableAndItsFeatureReferenceGo() {
    var prepared = Prepared();
    var ours = Removed(prepared, All);
    var kernel = Space(prepared, string.Concat(All.Select(r => $"rm $M/{r}; ")));
    var space = Space(ours, "");
    Assert.That(space, Is.EqualTo(kernel));
    Assert.That(space, Has.Some.EqualTo("SPACE feature@block_cloning\tenabled"));
    using var reader = new ZfsReader(new MemoryStream(ours, writable: false));
    Assert.That(reader.Warnings, Is.Empty);
  }
}
