#pragma warning disable CS1591

using System.Diagnostics;
using System.Text;
using FileSystem.FatPlus;

namespace Compression.Tests.FatPlus;

/// <summary>
/// External-tool acceptance gate for the FAT+ writer and the genuine in-place
/// <see cref="FatPlusInPlaceAdder"/>. FAT+ is a backward-compatible FAT32
/// extension whose on-disk layout is plain FAT32 except the directory-entry
/// size encoding, so a fresh FAT+ image — and an image after an in-place add of
/// files whose declared size equals their on-disk byte count — must validate
/// clean under <c>fsck.fat -n</c> (dosfstools) and list correctly under
/// <c>mdir</c> (mtools), exactly like a standard FAT32 volume.
///
/// <para><b>Documented limitation.</b> Files whose declared FAT+ extended size
/// exceeds 4 GiB (encoded in the <c>DIR_NTRes</c> high-6-bits) are deliberately
/// <em>not</em> validated here: a plain, non-FAT+-aware <c>fsck.fat</c> reads
/// only the 32-bit <c>DIR_FileSize</c> and reports a size/chain mismatch. That
/// is the inherent FAT+ incompatibility with legacy tools, verified instead by
/// the reader-round-trip tests in <c>FatPlusRwTests</c>.</para>
///
/// <para>Validators are invoked through WSL when available (Windows dev/CI) and
/// fall back to the host PATH (native Linux CI). Both channels
/// <see cref="Assert.Ignore"/> cleanly when no validator is reachable.</para>
/// </summary>
[TestFixture]
[Category("ExternalConformance")]
public class FatPlusExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_fatplus_conf_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── Tool routing (WSL preferred, host PATH fallback) ──────────────────────

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static bool TryFromPath(string tool, out string fullPath) {
    var pathEnv = Environment.GetEnvironmentVariable("PATH");
    fullPath = string.Empty;
    if (string.IsNullOrEmpty(pathEnv)) return false;
    var exeName = OperatingSystem.IsWindows() && !tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
      ? tool + ".exe" : tool;
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
    var r = RunWsl($"command -v {tool}");
    var found = r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut);
    _wslToolCache[tool] = found;
    return found;
  }

  private static ToolResult RunWsl(string linuxCommand) {
    if (!TryFromPath("wsl", out var wsl)) return new ToolResult(string.Empty, "wsl not on PATH", -1);
    var dqEscaped = linuxCommand.Replace("\"", "\\\"");
    return RunExact(wsl, $"-e bash -c \"{dqEscaped}\"");
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
      FileName = tool, Arguments = args,
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
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

  private static ToolResult? RunValidator(string tool, string flags, string imagePath) {
    if (WslAvailable && WslHasTool(tool))
      return RunWsl($"{tool} {flags} {WinToWsl(imagePath)}");
    if (TryFromPath(tool, out var hostTool))
      return RunExact(hostTool, $"{flags} \"{imagePath}\"");
    return null;
  }

  private static ToolResult? RunMdir(string imagePath) {
    if (WslAvailable && WslHasTool("mdir"))
      return RunWsl($"mdir -b -/ -i {WinToWsl(imagePath)} ::/");
    if (TryFromPath("mdir", out var hostTool))
      return RunExact(hostTool, $"-b -/ -i \"{imagePath}\" ::/");
    return null;
  }

  private static void RequireValidator(string tool, string aptHint) {
    if (WslAvailable && WslHasTool(tool)) return;
    if (TryFromPath(tool, out _)) return;
    Assert.Ignore(
      $"{tool} not reachable. Install via WSL (`sudo apt install -y {aptHint}`) or add it to host PATH.");
  }

  // The boot-sector/backup OEM "differences" phrase is included: a missing
  // backup-OEM patch makes fsck.fat report it, which the writer fix must avoid.
  private static readonly string[] FsckFatProblemPhrases = [
    "differ", "differences", "wrong", "Invalid", "Fixing", "Auto-correcting",
    "Corrupt", "corrupted", "orphan", "truncated", "mismatch", "Dropping",
    "Reclaiming", "Removing", "Unaligned", "bad cluster", "Cluster start",
    "contains a bad",
  ];

  private static (bool Clean, string Report) RunFsckFat(string imagePath) {
    var r = RunValidator("fsck.fat", "-n -V", imagePath)
      ?? throw new InvalidOperationException("fsck.fat unreachable — caller must RequireValidator first");
    var combined = r.StdOut + "\n" + r.StdErr;
    var scan = combined
      .Replace(imagePath, "<image>", StringComparison.Ordinal)
      .Replace(WinToWsl(imagePath).Trim('\''), "<image>", StringComparison.Ordinal);
    var flagged = FsckFatProblemPhrases
      .Where(p => scan.Contains(p, StringComparison.OrdinalIgnoreCase))
      .ToList();
    var clean = r.ExitCode == 0 && flagged.Count == 0;
    return (clean, $"exit={r.ExitCode}; flagged=[{string.Join(", ", flagged)}]\n{combined}");
  }

  private static byte[] Bytes(int n, int seed) {
    var r = new Random(seed);
    var b = new byte[n];
    r.NextBytes(b);
    return b;
  }

  private string WriteImage(string name, byte[] image) {
    var path = Path.Combine(_tmpDir, name);
    File.WriteAllBytes(path, image);
    return path;
  }

  // ═══════════════════════════════════════════════════════════════════
  // A freshly built FAT+ image is clean under fsck.fat (incl. the backup
  // boot-sector OEM signature being identical to the primary).
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  [CancelAfter(180_000)]
  public void FreshFatPlusImage_IsCleanUnderFsckFat() {
    RequireValidator("fsck.fat", "dosfstools");
    var w = new FatPlusWriter();
    w.AddFile("README.TXT", Encoding.ASCII.GetBytes("FAT+ conformance probe"));
    w.AddFile("BIG.BIN", Bytes(300_000, 21));
    var path = WriteImage("fatplus_fresh.img", w.Build());
    var (clean, report) = RunFsckFat(path);
    Assert.That(clean, Is.True, $"fsck.fat flagged a freshly built FAT+ image:\n{report}");
  }

  // ═══════════════════════════════════════════════════════════════════
  // After a genuine in-place add of real-data files (small + multi-cluster
  // + long name), the image is still clean under fsck.fat and mdir lists
  // every file. This is the FAT+ R/W proof on a real validator.
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  [CancelAfter(180_000)]
  public void InPlaceAdds_AreCleanUnderFsckFat_AndListedByMdir() {
    RequireValidator("fsck.fat", "dosfstools");

    var w = new FatPlusWriter();
    w.AddFile("SEED.TXT", "seed"u8.ToArray());
    var image = w.Build();

    FatPlusInPlaceAdder.AddFile(image, "SMALL.TXT", "small in-place fat+ add"u8.ToArray());
    FatPlusInPlaceAdder.AddFile(image, "BIG.BIN", Bytes(250 * 1024, 5)); // multi-cluster
    FatPlusInPlaceAdder.AddFile(image, "A Long FatPlus Name.txt", "long name probe"u8.ToArray());
    var path = WriteImage("fatplus_inplace.img", image);

    var (clean, report) = RunFsckFat(path);
    Assert.That(clean, Is.True, $"fsck.fat flagged a FAT+ image after in-place adds:\n{report}");

    RequireValidator("mdir", "mtools");
    var r = RunMdir(path)
      ?? throw new InvalidOperationException("mdir unreachable — RequireValidator should have skipped");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"mdir failed:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
    var listing = r.StdOut.ToUpperInvariant().Replace('\\', '/');
    Assert.Multiple(() => {
      Assert.That(listing, Does.Contain("::/SEED.TXT"), "existing SEED.TXT must survive the in-place add");
      Assert.That(listing, Does.Contain("::/SMALL.TXT"), "SMALL.TXT must appear");
      Assert.That(listing, Does.Contain("::/BIG.BIN"), "multi-cluster BIG.BIN must appear");
      Assert.That(listing, Does.Contain("::/A LONG FATPLUS NAME.TXT"), "long name must appear via VFAT slots");
    });
  }

  // ═══════════════════════════════════════════════════════════════════
  // After an in-place remove, the image is still clean under fsck.fat.
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  [CancelAfter(180_000)]
  public void InPlaceRemove_IsCleanUnderFsckFat() {
    RequireValidator("fsck.fat", "dosfstools");

    var w = new FatPlusWriter();
    w.AddFile("KEEP.TXT", "keep"u8.ToArray());
    w.AddFile("DROP.BIN", Bytes(100_000, 9));
    var image = w.Build();

    FatPlusInPlaceAdder.RemoveFile(image, "DROP.BIN");
    var path = WriteImage("fatplus_removed.img", image);

    var (clean, report) = RunFsckFat(path);
    Assert.That(clean, Is.True, $"fsck.fat flagged a FAT+ image after in-place remove:\n{report}");
  }
}
