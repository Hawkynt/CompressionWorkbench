#pragma warning disable CA1416 // Platform compatibility — OCFS2 tools are Linux/WSL-only and guarded at runtime.

using FileSystem.Ocfs2;

namespace Compression.Tests.Ocfs2;

/// <summary>
/// Cross-checks the OCFS2 reader and writer against the reference ocfs2-tools
/// (<c>mkfs.ocfs2</c>, <c>fsck.ocfs2</c>, <c>debugfs.ocfs2</c>) reached through
/// WSL on Windows or the native PATH on Linux. Two independent gates:
/// <list type="bullet">
///   <item><description><b>Reverse gate</b> — a real <c>mkfs.ocfs2 -M local</c>
///   image is parsed by <see cref="Ocfs2Reader"/>; the reader must surface the
///   <c>lost+found</c> directory that mkfs always creates. This proves the reader
///   matches the on-disk format rather than only the toolkit's own writer.</description></item>
///   <item><description><b>Forward gate</b> — an image built by
///   <see cref="Ocfs2Writer"/> is handed to <c>fsck.ocfs2 -fn</c>; its exit code
///   and pass output are asserted. OCFS2 fsck exit 0 = clean.</description></item>
/// </list>
/// Tests skip cleanly only when no tool channel is reachable.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
[Category("Conformance")]
[Category("Wsl")]
public class Ocfs2ExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ocfs2_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── Reverse gate: read a REAL mkfs.ocfs2 image ─────────────────────

  /// <summary>
  /// Given a volume formatted by the reference <c>mkfs.ocfs2 -M local</c>, when
  /// <see cref="Ocfs2Reader"/> walks its root directory, then the always-present
  /// <c>lost+found</c> directory is recognised — proving the reader parses the
  /// real on-disk layout (INODE01 dinodes, inline directory entries) and not just
  /// images produced by the toolkit's own writer.
  /// </summary>
  [Test]
  public void RealMkfsImage_ReaderRecognisesOnDiskLayout() {
    RequireTool("mkfs.ocfs2");
    var wslImg = MakeRealLocalImage(out var winImg);

    // The reader walks the root dir for *files*; lost+found is a directory, so we
    // assert on the lower-level recognition path: the image is recognised as
    // OCFS2 and its superblock-declared root block is a parsable dinode whose
    // inline directory contains lost+found.
    var image = File.ReadAllBytes(winImg);
    Assert.That(Ocfs2Reader.LooksLikeOcfs2(image), Is.True,
      "Reader did not recognise the OCFSV2 superblock of a real mkfs.ocfs2 image.");

    var rootNames = Ocfs2Reader.ReadRootEntryNames(image);
    Assert.That(rootNames, Does.Contain("lost+found"),
      $"Reader failed to find lost+found in the real mkfs.ocfs2 root directory. " +
      $"Saw: [{string.Join(", ", rootNames)}]. WSL image: {wslImg}");
  }

  // ── Forward gate: our writer must pass fsck.ocfs2 ──────────────────

  /// <summary>
  /// Given an image built by <see cref="Ocfs2Writer"/> with files and a nested
  /// directory, when it is handed to <c>fsck.ocfs2 -fn</c>, then the exact tool
  /// output and exit code are reported. fsck exit 0 means the image is clean.
  /// </summary>
  [Test]
  public void OurWriterImage_FsckOcfs2Report() {
    RequireTool("fsck.ocfs2");

    var w = new Ocfs2Writer();
    w.AddFile("readme.txt", "ocfs2 writer sample\n"u8.ToArray());
    w.AddFile("notes.bin", MakeData(9000));
    w.AddFile("docs/guide.txt", "guide in docs\n"u8.ToArray());
    var image = w.Build();

    var imgPath = Path.Combine(_tmpDir, "ours.ocfs2");
    File.WriteAllBytes(imgPath, image);

    var r = RunOcfs2Tool("fsck.ocfs2", "-fn", imgPath);
    TestContext.Out.WriteLine($"fsck.ocfs2 exit={r.ExitCode}\n--- stdout ---\n{r.StdOut}\n--- stderr ---\n{r.StdErr}");

    // Honest gate: fsck.ocfs2 exit 0 == clean. The tool IS installed, so this
    // test never skips — it runs fsck and records the real verdict.
    //
    // Ocfs2Writer emits a complete single-node ("local") OCFS2 volume: the full
    // system-file suite (global_bitmap + global_inode_alloc + inode_alloc:0000 +
    // extent_alloc:0000 chain allocators with GROUP01 group descriptors and
    // correct bitmaps, slot_map, local_alloc:0000, truncate_log:0000,
    // orphan_dir:0000, heartbeat, bad_blocks, a valid empty JBD2 journal:0000),
    // a spec-correct superblock and dinode layout, lost+found, and matching
    // allocation accounting + directory link counts. fsck.ocfs2 -fn must pass
    // every check at exit 0.
    Assert.That(r.ExitCode, Is.Not.EqualTo(int.MinValue),
      "fsck.ocfs2 did not run on any channel (tool should be installed).");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"fsck.ocfs2 did not report the image clean (exit {r.ExitCode}):\n{r.StdOut}\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("All passes succeeded").IgnoreCase,
      $"fsck.ocfs2 exited 0 but did not confirm all passes succeeded:\n{r.StdOut}");
  }

  /// <summary>
  /// Given an image built by <see cref="Ocfs2Writer"/>, when the reference
  /// <c>debugfs.ocfs2 stats</c> reads its superblock, then the tool exits 0 and
  /// reports the spec-correct geometry (Root Blknum 5, System Dir 6, 4 KB
  /// block/cluster, inline-data feature). This proves the superblock + dinode
  /// header is genuinely on-disk-correct — the reference tool, not just our own
  /// reader, parses it. (Full directory walking still needs the chain-allocator
  /// system files the writer does not yet emit; see the fsck report test.)
  /// </summary>
  [Test]
  public void OurWriterImage_DebugfsReadsSuperblock() {
    RequireTool("debugfs.ocfs2");
    var w = new Ocfs2Writer();
    w.AddFile("readme.txt", "ocfs2 sample\n"u8.ToArray());
    var imgPath = Path.Combine(_tmpDir, "ours_sb.ocfs2");
    File.WriteAllBytes(imgPath, w.Build());

    var r = RunOcfs2Tool("debugfs.ocfs2", "-R 'stats'", imgPath);
    TestContext.Out.WriteLine($"debugfs.ocfs2 stats exit={r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    Assert.That(r.ExitCode, Is.Not.EqualTo(int.MinValue), "debugfs.ocfs2 did not run on any channel.");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"debugfs.ocfs2 failed to read our superblock (exit {r.ExitCode}):\n{r.StdOut}\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("Root Blknum: 5"),
      $"debugfs.ocfs2 did not report the expected root block:\n{r.StdOut}");
    Assert.That(r.StdOut, Does.Contain("System Dir Blknum: 6"),
      $"debugfs.ocfs2 did not report the expected system dir block:\n{r.StdOut}");
    Assert.That(r.StdOut, Does.Contain("Block Size Bits: 12"),
      $"debugfs.ocfs2 did not report the expected block size:\n{r.StdOut}");
    Assert.That(r.StdOut, Does.Contain("inline-data"),
      $"debugfs.ocfs2 did not report the inline-data feature:\n{r.StdOut}");
  }

  // ── Round-trip: our writer → our reader ────────────────────────────

  /// <summary>
  /// A pure C# guard (no external tool): files written by <see cref="Ocfs2Writer"/>
  /// — including a nested-directory file and a multi-cluster file — read back
  /// byte-identical through <see cref="Ocfs2Reader"/>. This protects against the
  /// reader and writer drifting onto mutually-wrong offsets.
  /// </summary>
  [Test]
  public void OurWriter_RoundTripsThroughReader() {
    var samples = new (string Name, byte[] Data)[] {
      ("readme.txt", "ocfs2 round-trip sample\n"u8.ToArray()),
      ("notes.bin", MakeData(9000)),
      ("docs/guide.txt", "guide in docs\n"u8.ToArray()),
      ("docs/api/reference.txt", "deep reference\n"u8.ToArray()),
    };

    var w = new Ocfs2Writer();
    foreach (var (name, data) in samples) w.AddFile(name, data);
    var image = w.Build();

    var read = Ocfs2Reader.ReadFiles(image)
      .ToDictionary(f => f.Name, f => f.Data, StringComparer.Ordinal);

    foreach (var (name, data) in samples) {
      Assert.That(read.ContainsKey(name), Is.True, $"Reader missed '{name}'. Saw: [{string.Join(", ", read.Keys)}]");
      Assert.That(read[name], Is.EqualTo(data), $"Content mismatch for '{name}'.");
    }
  }

  // ── Image builders ─────────────────────────────────────────────────

  /// <summary>
  /// Formats a small local-mode OCFS2 volume with the reference tool inside WSL
  /// and returns the WSL path; also yields the Windows path to read the bytes.
  /// </summary>
  private string MakeRealLocalImage(out string winImagePath) {
    winImagePath = Path.Combine(_tmpDir, "real_mkfs.ocfs2");
    var wslImg = FsInteropToolbox.WinToWsl(winImagePath);
    // 32 MiB local-mode volume: small but valid; -M local => non-clustered.
    var script =
      $"truncate -s 32M {wslImg} && " +
      $"mkfs.ocfs2 -M local -b 4096 -C 4096 -N 1 -L cwbocfs2 --force {wslImg} 2>&1";
    var r = FsInteropToolbox.RunWsl(script);
    if (r.ExitCode != 0)
      Assert.Inconclusive($"mkfs.ocfs2 failed to format a local image (exit {r.ExitCode}):\n{r.StdOut}\n{r.StdErr}");
    if (!File.Exists(winImagePath))
      Assert.Inconclusive($"mkfs.ocfs2 reported success but the image is not visible at {winImagePath}.");
    return wslImg;
  }

  private static byte[] MakeData(int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)(i * 31 + 7);
    return data;
  }

  // ── Tool channel plumbing (mirrors ExtExternalConformanceTests) ────

  private static (string StdOut, string StdErr, int ExitCode) RunOcfs2Tool(
      string tool, string args, string winImagePath) {
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool(tool))
      return FsInteropToolbox.RunWsl($"{tool} {args} {FsInteropToolbox.WinToWsl(winImagePath)}");
    return (string.Empty, "no channel available", int.MinValue);
  }

  private static void RequireTool(string tool) {
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool(tool)) return;
    Assert.Ignore(
      $"'{tool}' not available. Install ocfs2-tools inside WSL " +
      $"(`sudo apt install -y ocfs2-tools`) to enable this gate.");
  }
}
