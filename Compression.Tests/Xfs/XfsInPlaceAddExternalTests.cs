#pragma warning disable CS1591

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FileSystem.Xfs;

namespace Compression.Tests.Xfs;

/// <summary>
/// External-tool acceptance gate for the genuine in-place XFS v5 add
/// (<see cref="XfsInPlaceAdder"/>). Each scenario builds a writer image, mutates
/// it in place (no whole-image rebuild), then proves:
/// <list type="bullet">
///   <item><c>xfs_repair -n</c> accepts the result (exit 0, no "would …",
///         "corrupt", "bad", "error", "inconsistent" lines);</item>
///   <item><see cref="XfsReader"/> reads every expected file back byte-identical
///         — including files that pre-existed the mutation, proving their data
///         stayed put.</item>
/// </list>
/// Covers: short-form root add; short-form → block → leaf directory promotion;
/// inode-chunk growth (a fresh 64-inode chunk); fragmented free-space allocation
/// (middle/best-fit carve across a multi-record bnobt/cntbt); replace-by-name;
/// and nested sub-directory targets (existing and freshly created in place).
/// Skipped cleanly when <c>xfs_repair</c> is unavailable.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalConformance")]
public class XfsInPlaceAddExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_xfs_inplace_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  [Test]
  public void RootFileAdd_RepairCleanAndReadable() => RunScenario(
    seed: w => w.AddFile("readme.txt", Bytes("hello world")),
    mutate: (img, exp) => {
      Add(img, exp, "added.txt", Bytes("new content here"));
    },
    expectInPlace: true);

  [Test]
  public void ShortFormOverflow_PromotesToBlock_RepairCleanAndReadable() => RunScenario(
    seed: w => { for (var i = 0; i < 3; i++) w.AddFile($"file{i}.txt", Bytes($"c{i}")); },
    mutate: (img, exp) => {
      for (var i = 0; i < 12; i++) Add(img, exp, $"extra{i:D2}.dat", Bytes($"payload-{i}"));
    },
    expectInPlace: true,
    seedExpect: exp => { for (var i = 0; i < 3; i++) exp[$"file{i}.txt"] = Bytes($"c{i}"); });

  [Test]
  public void LeafFormRoot_RepairCleanAndReadable() => RunScenario(
    seed: w => w.AddFile("seed.txt", Bytes("s")),
    mutate: (img, exp) => {
      for (var i = 0; i < 450; i++) Add(img, exp, $"lf{i:D4}.dat", Bytes($"data-{i:D4}"));
    },
    expectInPlace: true,
    seedExpect: exp => exp["seed.txt"] = Bytes("s"));

  [Test]
  public void InodeChunkGrowth_RepairCleanAndReadable() => RunScenario(
    // Filling well past 64 inodes forces a new inode chunk to be carved + an
    // inobt record inserted; the dir simultaneously grows into leaf form.
    seed: w => w.AddFile("seed.txt", Bytes("s")),
    mutate: (img, exp) => {
      for (var i = 0; i < 80; i++) Add(img, exp, $"f{i:D3}", Bytes($"d{i}"));
    },
    expectInPlace: true,
    seedExpect: exp => exp["seed.txt"] = Bytes("s"));

  [Test]
  public void FragmentedFreeSpace_MiddleCarve_RepairCleanAndReadable() => RunScenario(
    seed: w => { for (var i = 0; i < 30; i++) w.AddFile($"x{i:D2}", new byte[8192]); },
    mutate: (img, exp) => {
      // Replace every other 2-block file with a tiny one, freeing interleaved
      // multi-block extents → a multi-record bnobt/cntbt. Then add a 5-block
      // file, forcing a best-fit carve out of a free fragment.
      var rnd = new Random(3);
      for (var i = 0; i < 30; i += 2) {
        var d = new byte[100]; rnd.NextBytes(d);
        Add(img, exp, $"x{i:D2}", d);
      }
      var big = new byte[5 * 4096]; rnd.NextBytes(big);
      Add(img, exp, "bigafter.bin", big);
    },
    expectInPlace: true,
    seedExpect: exp => { for (var i = 0; i < 30; i++) exp[$"x{i:D2}"] = new byte[8192]; });

  [Test]
  public void ReplaceByName_RepairCleanAndReadable() => RunScenario(
    seed: w => { w.AddFile("a.txt", Bytes("original-aaa")); w.AddFile("b.txt", Bytes("bbb")); },
    mutate: (img, exp) => {
      Add(img, exp, "a.txt", Bytes("REPLACED-content-which-is-longer"));
    },
    expectInPlace: true,
    seedExpect: exp => exp["b.txt"] = Bytes("bbb"));

  [Test]
  public void NestedExistingSubdir_RepairCleanAndReadable() => RunScenario(
    seed: w => { w.AddFile("a/keep.txt", Bytes("k")); w.AddFile("root.txt", Bytes("r")); },
    mutate: (img, exp) => {
      for (var i = 0; i < 250; i++) Add(img, exp, $"a/sub{i:D3}.txt", Bytes($"sd{i}"));
    },
    expectInPlace: true,
    seedExpect: exp => { exp["a/keep.txt"] = Bytes("k"); exp["root.txt"] = Bytes("r"); });

  [Test]
  public void CreateNewNestedDirsInPlace_RepairCleanAndReadable() => RunScenario(
    seed: w => w.AddFile("seed.txt", Bytes("s")),
    mutate: (img, exp) => {
      Add(img, exp, "newdir/deep/leaf/file.txt", Bytes("hello deep"));
      Add(img, exp, "newdir/another.txt", Bytes("another"));
    },
    expectInPlace: true,
    seedExpect: exp => exp["seed.txt"] = Bytes("s"));

  [Test]
  public void ExistingLargeFile_StaysByteIdentical_AfterManyAdds() {
    var big = new byte[180_000];
    new Random(11).NextBytes(big);
    RunScenario(
      seed: w => { w.AddFile("orig/large.bin", big); w.AddFile("orig/note.txt", Bytes("note")); },
      mutate: (img, exp) => {
        for (var i = 0; i < 50; i++) Add(img, exp, $"more{i:D2}.dat", Bytes($"m{i}"));
      },
      expectInPlace: true,
      seedExpect: exp => { exp["orig/large.bin"] = big; exp["orig/note.txt"] = Bytes("note"); });
  }

  // ── Harness ─────────────────────────────────────────────────────────────

  private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

  private static void Add(byte[] img, Dictionary<string, byte[]> exp, string name, byte[] data) {
    XfsInPlaceAdder.AddFile(img, name, data);
    exp[name] = data;
  }

  private void RunScenario(Action<FileSystem.Xfs.XfsWriter> seed,
      Action<byte[], Dictionary<string, byte[]>> mutate, bool expectInPlace,
      Action<Dictionary<string, byte[]>>? seedExpect = null) {
    if (!IsLinux) Assert.Ignore("xfs_repair is a Linux-only tool");
    if (!HasCommand("xfs_repair")) Assert.Ignore("xfs_repair (xfsprogs) not installed");

    var w = new FileSystem.Xfs.XfsWriter();
    seed(w);
    byte[] img;
    using (var ms = new MemoryStream()) { w.WriteTo(ms); img = ms.ToArray(); }

    var expect = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    seedExpect?.Invoke(expect);

    try {
      mutate(img, expect);
    } catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidDataException) {
      if (expectInPlace)
        Assert.Fail($"In-place add unexpectedly fell back to rebuild: {ex.Message}");
      return; // a documented fallback case — nothing further to verify in place
    }

    // ── xfs_repair must accept the in-place result ──
    var path = Path.Combine(this._tmpDir, $"img_{Guid.NewGuid():N}.xfs");
    File.WriteAllBytes(path, img);
    var result = RunTool("xfs_repair", $"-n \"{path}\"");
    var lower = (result.StdOut + result.StdErr).ToLowerInvariant();
    var dirty = lower.Contains("would have") || lower.Contains("corrupt")
      || lower.Contains("bad ") || lower.Contains(" error") || lower.Contains("inconsistent")
      || lower.Contains("not found") || lower.Contains("would clear") || lower.Contains("would move")
      || lower.Contains("would rebuild") || lower.Contains("would fix") || lower.Contains("would reset")
      || lower.Contains("disconnected inode ");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"xfs_repair -n must exit 0.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(dirty, Is.False,
      $"xfs_repair -n reported repair actions.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");

    // ── Reader must read every expected file back byte-identical ──
    using var read = new MemoryStream(img);
    var reader = new XfsReader(read);
    var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var e in reader.Entries)
      if (!e.IsDirectory)
        found[e.Name] = reader.Extract(e);

    foreach (var (name, data) in expect) {
      Assert.That(found.ContainsKey(name), Is.True, $"file '{name}' missing after in-place add");
      Assert.That(found[name], Is.EqualTo(data), $"file '{name}' content mismatch after in-place add");
    }
  }

  // ── Process / tool helpers (mirror XfsExternalConformanceTests) ──

  private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

  private static bool HasCommand(string name) {
    try {
      var psi = new ProcessStartInfo {
        FileName = "/bin/sh", Arguments = $"-c \"which {name}\"",
        RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true,
      };
      using var proc = Process.Start(psi)!;
      var outp = proc.StandardOutput.ReadToEnd();
      proc.WaitForExit(30_000);
      return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(outp);
    } catch {
      return false;
    }
  }

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static ToolResult RunTool(string tool, string args, int timeoutMs = 120_000) {
    var psi = new ProcessStartInfo {
      FileName = tool, Arguments = args,
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    psi.Environment["LANG"] = "C";
    psi.Environment["LC_ALL"] = "C";
    using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {tool}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }
}
