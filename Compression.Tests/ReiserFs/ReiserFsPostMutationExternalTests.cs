using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.ReiserFs;

/// <summary>
/// External-tool acceptance gate: every mutated image must still pass
/// <c>reiserfsck --check</c> on a real Linux filesystem stack. This is the
/// honest correctness signal — self-round-trip can pass even when the writer
/// and reader are mutually wrong at the same offsets (the XFS trap), but
/// reiserfsck rejects every structural defect: bad item type, bad dirent hash,
/// bad SD block-count, bad bitmap, bad tree path. We exercise every previously
/// out-of-scope mutation path here.
///
/// Skipped cleanly on non-Windows or when WSL / reiserfsprogs is not installed.
/// Install on the host with: <c>wsl sudo apt-get install -y reiserfsprogs</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class ReiserFsPostMutationExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_rfs_external_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── Acceptance gates ───────────────────────────────────────────────────

  /// <summary>
  /// Given a ReiserFS image, when AddFile inserts a small file into the root
  /// directory, then <c>reiserfsck --check</c> reports no corruptions on the
  /// resulting image.
  /// </summary>
  [Test]
  public void PostAdd_PassesReiserfsck() {
    var imgPath = WriteSeedImage("post_add.reiserfs", ("seed.txt", "seed"u8.ToArray()));
    using (var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
      var tmp = Path.GetTempFileName();
      try {
        File.WriteAllBytes(tmp, "added content"u8.ToArray());
        ((IArchiveModifiable)d).Add(fs, [new ArchiveInputInfo(tmp, "added.txt", false)]);
      } finally {
        File.Delete(tmp);
      }
    }
    AssertReiserfsckClean(imgPath);
  }

  /// <summary>
  /// Given an image with two files, when RemoveFile drops one, then
  /// reiserfsck reports the result clean.
  /// </summary>
  [Test]
  public void PostRemove_PassesReiserfsck() {
    var imgPath = WriteSeedImage("post_remove.reiserfs",
      ("keep.txt", "keep"u8.ToArray()),
      ("drop.txt", "drop"u8.ToArray()));
    using (var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
      ((IArchiveModifiable)d).Remove(fs, ["drop.txt"]);
    }
    AssertReiserfsckClean(imgPath);
  }

  /// <summary>
  /// Adding many files forces leaf splits (multi-leaf S+tree with an internal
  /// page above). reiserfsck must accept the resulting tree shape, key
  /// ordering, bitmap, and per-object sd_blocks accounting.
  /// </summary>
  [Test]
  public void PostLeafSplit_PassesReiserfsck() {
    var imgPath = WriteSeedImage("post_split.reiserfs", ("seed.txt", "seed"u8.ToArray()));
    using (var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
      var tmpFiles = new List<ArchiveInputInfo>();
      var tmpHandles = new List<string>();
      try {
        for (var i = 0; i < 50; i++) {
          var t = Path.GetTempFileName();
          File.WriteAllBytes(t, Encoding.UTF8.GetBytes($"payload-{i}"));
          tmpHandles.Add(t);
          tmpFiles.Add(new ArchiveInputInfo(t, $"file{i:D3}.txt", false));
        }
        ((IArchiveModifiable)d).Add(fs, tmpFiles);
      } finally {
        foreach (var t in tmpHandles) try { File.Delete(t); } catch { }
      }
    }
    AssertReiserfsckClean(imgPath);
  }

  /// <summary>
  /// Removing most files from a multi-leaf image forces leaf merges and
  /// possibly tree-height collapse. reiserfsck must still report clean.
  /// </summary>
  [Test]
  public void PostLeafMerge_PassesReiserfsck() {
    var seedFiles = new List<(string, byte[])>();
    for (var i = 0; i < 50; i++)
      seedFiles.Add(($"file{i:D3}.txt", Encoding.UTF8.GetBytes($"payload-{i}")));
    var imgPath = WriteSeedImage("post_merge.reiserfs", [.. seedFiles]);

    using (var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
      ((IArchiveModifiable)d).Remove(fs, [.. seedFiles.Take(40).Select(s => s.Item1)]);
    }
    AssertReiserfsckClean(imgPath);
  }

  /// <summary>
  /// Nested-path adds create intermediate directories. reiserfsck must accept
  /// the directory entries' R5-hashed deh_offsets and the parent / child key
  /// relationships.
  /// </summary>
  [Test]
  public void PostNested_PassesReiserfsck() {
    var imgPath = WriteSeedImage("post_nested.reiserfs", ("readme.txt", "root"u8.ToArray()));
    using (var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
      var tmp = Path.GetTempFileName();
      try {
        File.WriteAllBytes(tmp, "deep file content"u8.ToArray());
        ((IArchiveModifiable)d).Add(fs, [new ArchiveInputInfo(tmp, "docs/api/reference.txt", false)]);
      } finally {
        File.Delete(tmp);
      }
    }
    AssertReiserfsckClean(imgPath);
  }

  /// <summary>
  /// Adding a body larger than one DIRECT item (forces an INDIRECT item with
  /// dedicated data blocks past the tree) must still produce a reiserfsck-
  /// clean image. This is the previously NotSupportedException scope.
  /// </summary>
  [Test]
  public void PostLargeFile_PassesReiserfsck() {
    var imgPath = WriteSeedImage("post_large.reiserfs", ("seed.txt", "seed"u8.ToArray()));
    using (var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
      var tmp = Path.GetTempFileName();
      try {
        var big = new byte[12 * 1024];
        new Random(7).NextBytes(big);
        File.WriteAllBytes(tmp, big);
        ((IArchiveModifiable)d).Add(fs, [new ArchiveInputInfo(tmp, "big.bin", false)]);
      } finally {
        File.Delete(tmp);
      }
    }
    AssertReiserfsckClean(imgPath);
  }

  // ── Genuine in-place proofs (data must NOT be relocated) ─────────────────

  /// <summary>
  /// Given a seed image whose single existing file is large enough to live in a
  /// dedicated INDIRECT data block, when a small file is added to the root, then
  /// the existing file's data block stays byte-identical at the SAME absolute
  /// offset — proving the add is a genuine in-place S+tree splice, not a rebuild.
  /// </summary>
  [Test]
  public void PostAdd_IndirectFileDataBlock_NotRelocated() {
    var bigSeed = new byte[8 * 1024];
    for (var i = 0; i < bigSeed.Length; i++) bigSeed[i] = (byte)(i * 7 + 3);
    var imgPath = WriteSeedImage("inplace_proof.reiserfs", ("bigseed.bin", bigSeed));

    var before = File.ReadAllBytes(imgPath);
    var sig = bigSeed.AsSpan(0, 256).ToArray();
    var offBefore = IndexOf(before, sig);
    Assert.That(offBefore, Is.GreaterThanOrEqualTo(0), "seed data block locatable before add");

    using (var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
      var tmp = Path.GetTempFileName();
      try {
        File.WriteAllBytes(tmp, "a small added file"u8.ToArray());
        ((IArchiveModifiable)d).Add(fs, [new ArchiveInputInfo(tmp, "added.txt", false)]);
      } finally { File.Delete(tmp); }
    }

    var after = File.ReadAllBytes(imgPath);
    var offAfter = IndexOf(after, sig);
    Assert.Multiple(() => {
      Assert.That(offAfter, Is.EqualTo(offBefore),
        "existing INDIRECT file's data block must stay at the same offset (in-place, not rebuilt)");
      Assert.That(after.AsSpan(offAfter, bigSeed.Length).SequenceEqual(bigSeed), Is.True,
        "existing file's data block must be byte-identical after the add");
    });
    AssertReiserfsckClean(imgPath);
  }

  /// <summary>
  /// A small add that fits in the existing root leaf must NOT grow the image —
  /// the in-place path reuses the leaf's free space instead of re-emitting the
  /// whole volume.
  /// </summary>
  [Test]
  public void PostAdd_SmallFile_DoesNotGrowImage() {
    var bigSeed = new byte[8 * 1024];
    new Random(11).NextBytes(bigSeed);
    var imgPath = WriteSeedImage("inplace_nogrow.reiserfs", ("bigseed.bin", bigSeed));
    var sizeBefore = new FileInfo(imgPath).Length;

    using (var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
      var tmp = Path.GetTempFileName();
      try {
        File.WriteAllBytes(tmp, "tiny"u8.ToArray());
        ((IArchiveModifiable)d).Add(fs, [new ArchiveInputInfo(tmp, "added.txt", false)]);
      } finally { File.Delete(tmp); }
    }

    Assert.That(new FileInfo(imgPath).Length, Is.EqualTo(sizeBefore),
      "small in-place add reuses leaf free space; image size must be unchanged");
    AssertReiserfsckClean(imgPath);
  }

  /// <summary>
  /// In-place remove of an INDIRECT file frees its data block (the bytes are
  /// wiped) while the surviving file round-trips and the image is not rebuilt
  /// (size unchanged). reiserfsck must report clean — including the bitmap
  /// comparison, which proves the freed block is correctly accounted.
  /// </summary>
  [Test]
  public void PostRemove_IndirectFile_FreesBlockInPlace() {
    var keep = "KEEP-THIS-FILE-CONTENT"u8.ToArray();
    var bigDrop = new byte[8 * 1024];
    for (var i = 0; i < bigDrop.Length; i++) bigDrop[i] = (byte)(i * 5 + 1);
    var imgPath = WriteSeedImage("inplace_rm.reiserfs", ("keep.txt", keep), ("dropbig.bin", bigDrop));
    var sizeBefore = new FileInfo(imgPath).Length;

    using (var fs = File.Open(imgPath, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
      ((IArchiveModifiable)d).Remove(fs, ["dropbig.bin"]);
    }

    var after = File.ReadAllBytes(imgPath);
    var sig = bigDrop.AsSpan(0, 256).ToArray();
    Assert.Multiple(() => {
      Assert.That(new FileInfo(imgPath).Length, Is.EqualTo(sizeBefore),
        "in-place remove must not rebuild/shrink the image");
      Assert.That(IndexOf(after, sig), Is.LessThan(0),
        "removed INDIRECT file's data block must be wiped");
      Assert.That(IndexOf(after, keep), Is.GreaterThanOrEqualTo(0),
        "surviving file's body must remain present");
    });
    AssertReiserfsckClean(imgPath);
  }

  private static int IndexOf(byte[] haystack, byte[] needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++)
        if (haystack[i + j] != needle[j]) { match = false; break; }
      if (match) return i;
    }
    return -1;
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private string WriteSeedImage(string name, params (string Name, byte[] Data)[] files) {
    var path = Path.Combine(_tmpDir, name);
    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var fs = File.Create(path);
    w.WriteTo(fs);
    return path;
  }

  private static void AssertReiserfsckClean(string imgPath) {
    RequireWslAndReiserfsck();
    // reiserfsck --check is read-only but prompts for "Yes" before proceeding.
    var result = RunWsl($"echo Yes | reiserfsck --check '{WinToWsl(imgPath)}' 2>&1; echo EXIT=$?");

    Assert.That(result.StdOut, Does.Contain("No corruptions found"),
      $"reiserfsck did not report the filesystem clean.\nfull stdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdOut, Does.Not.Contain("Bad"),
      $"reiserfsck reported a Bad-* defect.\n{result.StdOut}");
    Assert.That(result.StdOut, Does.Not.Contain("corrupted"),
      $"reiserfsck reported corruption.\n{result.StdOut}");
    Assert.That(result.StdOut, Does.Not.Contain("vpf-"),
      $"reiserfsck reported a structural (vpf-) defect.\n{result.StdOut}");
    Assert.That(result.StdOut, Does.Not.Contain("bad_path"),
      $"reiserfsck reported an S+tree path defect.\n{result.StdOut}");
  }

  private static void RequireWslAndReiserfsck() {
    if (RunWsl("command -v reiserfsck").ExitCode != 0)
      Assert.Ignore("reiserfsck not installed. Run: `sudo apt-get install -y reiserfsprogs` (or via WSL on Windows).");
  }

  private static string WinToWsl(string p) {
    var full = Path.GetFullPath(p);
    if (full.Length < 2 || full[1] != ':') return full.Replace('\\', '/');
    var drive = char.ToLowerInvariant(full[0]);
    var tail = full[2..].Replace('\\', '/');
    return $"/mnt/{drive}{tail}";
  }

  private static (string StdOut, string StdErr, int ExitCode) RunWsl(string cmd) {
    var dqEscaped = cmd.Replace("\"", "\\\"");
    var onWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    var psi = new ProcessStartInfo {
      FileName = onWindows ? "wsl" : "/bin/bash",
      Arguments = onWindows ? $"-e bash -c \"{dqEscaped}\"" : $"-c \"{dqEscaped}\"",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    try {
      using var p = Process.Start(psi)!;
      var so = p.StandardOutput.ReadToEnd();
      var se = p.StandardError.ReadToEnd();
      p.WaitForExit(120_000);
      return (so, se, p.ExitCode);
    } catch (Exception ex) {
      return (string.Empty, ex.Message, -1);
    }
  }
}
