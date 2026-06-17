#pragma warning disable CS1591

using System.Diagnostics;
using System.Text;
using FileSystem.Erofs;

namespace Compression.Tests.Erofs;

/// <summary>
/// External-tool acceptance gate for the EROFS reader and writer, validated against the
/// reference <c>erofs-utils</c> trio (<c>mkfs.erofs</c>, <c>fsck.erofs</c>,
/// <c>dump.erofs</c>). Two directions are exercised:
/// <list type="bullet">
///   <item><b>Reverse gate</b> — <c>mkfs.erofs</c> builds an uncompressed image from a
///   small directory tree; <see cref="ErofsReader"/> must list the exact file set and
///   return byte-identical contents. This proves the reader handles the real on-disk
///   layout the reference tool emits (extended inodes, FLAT_INLINE tails, 32-byte-granule
///   node ids).</item>
///   <item><b>Forward gate</b> — <see cref="ErofsWriter"/> builds an image; the reference
///   <c>fsck.erofs</c> must accept it (exit 0, no problem phrases) and <c>fsck.erofs
///   --extract</c> must reproduce every file byte-for-byte. This is the real proof the
///   writer is spec-compliant, not merely self-consistent with its own reader.</item>
/// </list>
///
/// <para>Validators run through WSL when available (the typical Windows-dev / CI setup
/// this project ships under) and fall back to the host PATH so a native Linux CI host
/// with erofs-utils installed still runs the gate. Both channels skip cleanly via
/// <see cref="Assert.Ignore"/> when neither is reachable.</para>
///
/// <para>A negative guard corrupts a known superblock field of a writer image and asserts
/// <c>fsck.erofs</c> rejects it, proving the gate can fail on bad input rather than
/// rubber-stamping anything.</para>
/// </summary>
[TestFixture]
[Category("ExternalConformance")]
public class ErofsExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_erofs_conf_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── Tool routing: WSL preferred (Windows dev/CI), host PATH fallback ──

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static bool TryFromPath(string tool, out string fullPath) {
    var pathEnv = Environment.GetEnvironmentVariable("PATH");
    fullPath = string.Empty;
    if (string.IsNullOrEmpty(pathEnv)) return false;
    var exeName = OperatingSystem.IsWindows() && !tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
      ? tool + ".exe"
      : tool;
    foreach (var dir in pathEnv.Split(Path.PathSeparator)) {
      if (string.IsNullOrWhiteSpace(dir)) continue;
      string candidate;
      try { candidate = Path.Combine(dir.Trim(), exeName); } catch { continue; }
      if (File.Exists(candidate)) { fullPath = candidate; return true; }
    }
    return false;
  }

  private static readonly Lazy<bool> _wslAvailable = new(() => {
    if (!OperatingSystem.IsWindows()) return false;
    if (!TryFromPath("wsl", out var wsl)) return false;
    var r = RunExact(wsl, "--status");
    return r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut);
  });

  private static bool WslAvailable => _wslAvailable.Value;

  private static readonly Dictionary<string, bool> _wslToolCache = new(StringComparer.Ordinal);

  private static bool WslHasTool(string tool) {
    if (!WslAvailable) return false;
    if (_wslToolCache.TryGetValue(tool, out var cached)) return cached;
    // erofs-utils install to /sbin; a non-login shell may miss it, so probe both PATH
    // and the canonical sbin locations.
    var r = RunWsl($"command -v {tool} || command -v /sbin/{tool} || command -v /usr/sbin/{tool}");
    var found = r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut);
    _wslToolCache[tool] = found;
    return found;
  }

  private static ToolResult RunWsl(string linuxCommand) {
    if (!TryFromPath("wsl", out var wsl)) return new ToolResult(string.Empty, "wsl not on PATH", -1);
    // Run through a login shell so /sbin (where erofs-utils lives) is on PATH.
    var dqEscaped = linuxCommand.Replace("\\", "\\\\").Replace("\"", "\\\"");
    return RunExact(wsl, $"-e bash -lc \"{dqEscaped}\"");
  }

  private static string WinToWsl(string winPath) {
    var full = Path.GetFullPath(winPath);
    if (full.Length < 2 || full[1] != ':') return full.Replace('\\', '/');
    var drive = char.ToLowerInvariant(full[0]);
    var tail = full[2..].Replace('\\', '/');
    return $"'/mnt/{drive}{tail}'";
  }

  private static ToolResult RunExact(string tool, string args, int timeoutMs = 120_000) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    try {
      using var proc = Process.Start(psi)!;
      var stdout = proc.StandardOutput.ReadToEnd();
      var stderr = proc.StandardError.ReadToEnd();
      if (!proc.WaitForExit(timeoutMs)) try { proc.Kill(); } catch { /* best effort */ }
      return new ToolResult(stdout, stderr, proc.ExitCode);
    } catch (Exception ex) {
      return new ToolResult(string.Empty, ex.Message, -1);
    }
  }

  private static void RequireTool(string tool) {
    if (WslAvailable && WslHasTool(tool)) return;
    if (TryFromPath(tool, out _)) return;
    Assert.Ignore(
      $"{tool} not reachable. Install erofs-utils via WSL (`sudo apt install -y erofs-utils`) " +
      $"or add it to host PATH. The external-tool gate skips cleanly when no validator is available.");
  }

  // ── fsck.erofs result interpretation ─────────────────────────────────
  //
  // fsck.erofs returns non-zero on structural failure; a clean image exits 0. We also
  // scan the combined output for problem phrases the tool prints when it flags a defect,
  // so a tool that happens to exit 0 while warning is still caught.
  private static readonly string[] FsckProblemPhrases = [
    "Unexpected", "invalid", "corrupt", "mismatch", "failed", "I/O error",
    "Bad", "unsupported", "out of", "cannot", "error",
  ];

  private (bool Clean, string Report) RunFsck(string imagePath) {
    var r = RunErofsTool("fsck.erofs", "", imagePath)
      ?? throw new InvalidOperationException("fsck.erofs unreachable — caller must RequireTool first");
    var combined = r.StdOut + "\n" + r.StdErr;
    var scan = combined
      .Replace(imagePath, "<image>", StringComparison.Ordinal)
      .Replace(WinToWsl(imagePath).Trim('\''), "<image>", StringComparison.Ordinal);
    var flagged = FsckProblemPhrases
      .Where(p => scan.Contains(p, StringComparison.OrdinalIgnoreCase))
      .ToList();
    var clean = r.ExitCode == 0 && flagged.Count == 0;
    var report = $"exit={r.ExitCode}; flagged=[{string.Join(", ", flagged)}]\n{combined}";
    return (clean, report);
  }

  private static ToolResult? RunErofsTool(string tool, string flags, string imagePath) {
    if (WslAvailable && WslHasTool(tool)) {
      var prefix = string.IsNullOrEmpty(flags) ? "" : flags + " ";
      return RunWsl($"{tool} {prefix}{WinToWsl(imagePath)}");
    }
    if (TryFromPath(tool, out var hostTool)) {
      var prefix = string.IsNullOrEmpty(flags) ? "" : flags + " ";
      return RunExact(hostTool, $"{prefix}\"{imagePath}\"");
    }
    return null;
  }

  // ── Deterministic test payloads ──────────────────────────────────────

  private static byte[] Bytes(int n, int seed) {
    var r = new Random(seed);
    var b = new byte[n];
    r.NextBytes(b);
    return b;
  }

  private sealed record Probe(string Path, byte[] Content);

  /// <summary>A representative tree: root files, a nested subtree, an empty file, and a
  /// multi-block file (>1 block) so both the inline-tail and whole-block code paths run.</summary>
  private static Probe[] BuildProbeSet() => [
    new("hello.txt", Encoding.ASCII.GetBytes("hello world from erofs")),
    new("readme.md", Encoding.ASCII.GetBytes("second file content here, a bit longer to span")),
    new("sub/nested.txt", Encoding.ASCII.GetBytes("nested file data")),
    new("sub/deep/leaf.bin", Bytes(777, 7)),
    new("data/big.bin", Bytes(10_000, 42)),     // > 4096 → spans full blocks + inline tail
    new("data/aligned.bin", Bytes(8192, 99)),   // exactly 2 blocks, zero inline tail
    new("empty.dat", []),
  ];

  // ── Reverse gate: mkfs.erofs → ErofsReader ───────────────────────────

  [Test]
  public void ReverseGate_ReadsMkfsErofsImage_NamesAndContentsMatch() {
    RequireTool("mkfs.erofs");

    var probes = BuildProbeSet();
    var srcDir = Path.Combine(_tmpDir, "src");
    foreach (var p in probes) {
      var full = Path.Combine(srcDir, p.Path.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      File.WriteAllBytes(full, p.Content);
    }

    var imagePath = Path.Combine(_tmpDir, "mkfs_ref.img");
    // mkfs.erofs takes "FILE SOURCE" positionally; -b 4096 forces the block size our
    // reader/writer use; no -z => uncompressed plain data.
    var mk = WslAvailable && WslHasTool("mkfs.erofs")
      ? RunWsl($"mkfs.erofs -b 4096 {WinToWsl(imagePath)} {WinToWsl(srcDir)}")
      : (TryFromPath("mkfs.erofs", out var host)
          ? RunExact(host, $"-b 4096 \"{imagePath}\" \"{srcDir}\"")
          : throw new InvalidOperationException("mkfs.erofs unreachable"));
    Assert.That(File.Exists(imagePath), Is.True,
      $"mkfs.erofs did not produce an image.\nstdout:\n{mk.StdOut}\nstderr:\n{mk.StdErr}");

    var reader = new ErofsReader(File.ReadAllBytes(imagePath));
    var read = reader.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => e.Path.Replace('\\', '/'), e => reader.ExtractFile(e));

    foreach (var p in probes) {
      Assert.That(read, Does.ContainKey(p.Path),
        $"Reader missed '{p.Path}'. Found: {string.Join(", ", read.Keys)}");
      Assert.That(read[p.Path], Is.EqualTo(p.Content),
        $"Content mismatch for '{p.Path}'.");
    }
    Assert.That(read, Has.Count.EqualTo(probes.Length),
      $"Reader found extra entries: {string.Join(", ", read.Keys)}");
  }

  // ── Forward gate: ErofsWriter → fsck.erofs / dump.erofs ──────────────

  [Test]
  public void ForwardGate_WriterImage_PassesFsckErofs() {
    RequireTool("fsck.erofs");

    var imagePath = WriteProbeImage();

    var (clean, report) = RunFsck(imagePath);
    Assert.That(clean, Is.True, $"fsck.erofs rejected the writer image.\n{report}");
    TestContext.Out.WriteLine($"fsck.erofs accepted the image:\n{report}");
  }

  [Test]
  public void ForwardGate_WriterImage_DumpErofsReadsSuperblock() {
    RequireTool("dump.erofs");

    var imagePath = WriteProbeImage();

    var r = RunErofsTool("dump.erofs", "-s", imagePath)
      ?? throw new InvalidOperationException("dump.erofs unreachable — caller must RequireTool first");
    var combined = r.StdOut + "\n" + r.StdErr;
    Assert.That(r.ExitCode, Is.EqualTo(0), $"dump.erofs -s failed.\n{combined}");
    Assert.That(combined, Does.Contain("0xE0F5E1E2"),
      $"dump.erofs did not recognise the superblock magic.\n{combined}");
    TestContext.Out.WriteLine($"dump.erofs -s:\n{combined}");
  }

  [Test]
  public void ForwardGate_WriterImage_FsckExtractsByteIdenticalContents() {
    RequireTool("fsck.erofs");

    var probes = BuildProbeSet();
    var imagePath = WriteProbeImage(probes);

    var extractDir = Path.Combine(_tmpDir, "extract");
    Directory.CreateDirectory(extractDir);

    ToolResult ex;
    if (WslAvailable && WslHasTool("fsck.erofs"))
      ex = RunWsl($"fsck.erofs --extract={WinToWsl(extractDir)} {WinToWsl(imagePath)}");
    else if (TryFromPath("fsck.erofs", out var host))
      ex = RunExact(host, $"--extract=\"{extractDir}\" \"{imagePath}\"");
    else { Assert.Ignore("fsck.erofs unreachable"); return; }

    Assert.That(ex.ExitCode, Is.EqualTo(0),
      $"fsck.erofs --extract failed.\nstdout:\n{ex.StdOut}\nstderr:\n{ex.StdErr}");

    foreach (var p in probes) {
      var outPath = Path.Combine(extractDir, p.Path.Replace('/', Path.DirectorySeparatorChar));
      Assert.That(File.Exists(outPath), Is.True,
        $"fsck.erofs did not extract '{p.Path}'.");
      Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(p.Content),
        $"Extracted content mismatch for '{p.Path}'.");
    }
  }

  // ── Negative guard: a corrupted writer image must be rejected ─────────

  [Test]
  public void NegativeGuard_CorruptedSuperblock_FsckRejects() {
    RequireTool("fsck.erofs");

    // Sanity: a clean writer image passes first.
    var cleanPath = WriteProbeImage();
    var (clean, cleanReport) = RunFsck(cleanPath);
    Assert.That(clean, Is.True, $"Pre-condition: clean image must pass fsck.\n{cleanReport}");

    // Corrupt the root_nid field (@1024+14) to point past the meta region. fsck must
    // notice the resulting inode is unreadable / out of range.
    var image = File.ReadAllBytes(cleanPath);
    image[1024 + 14] = 0xFF;
    image[1024 + 15] = 0xFF;
    var badPath = Path.Combine(_tmpDir, "corrupt.img");
    File.WriteAllBytes(badPath, image);

    var (badClean, badReport) = RunFsck(badPath);
    Assert.That(badClean, Is.False,
      $"fsck.erofs accepted a corrupted image — the gate cannot detect failure.\n{badReport}");
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  private string WriteProbeImage() => WriteProbeImage(BuildProbeSet());

  private string WriteProbeImage(Probe[] probes) {
    var writer = new ErofsWriter();
    foreach (var p in probes)
      writer.AddFile(p.Path, p.Content);
    var imagePath = Path.Combine(_tmpDir, "writer.img");
    File.WriteAllBytes(imagePath, writer.Build());
    return imagePath;
  }
}
