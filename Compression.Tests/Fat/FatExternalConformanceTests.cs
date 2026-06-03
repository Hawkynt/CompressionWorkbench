#pragma warning disable CS1591

using System.Diagnostics;
using System.Text;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// Stage-2 external-tool acceptance gate for <see cref="FatWriter"/>
/// (CONTRIBUTING.md → "External-tool gate"). Every <c>CanCreate</c> writer
/// must round-trip through a real, third-party validator — for FAT the
/// canonical pair is <c>fsck.fat</c> (dosfstools) for structural sanity and
/// <c>mtools mdir</c> (an independent FAT implementation) for directory
/// readability.
///
/// <para>Validators are invoked through WSL when available (the typical
/// Windows-dev / CI configuration this project ships under) and fall back
/// to the host PATH so a Linux CI host with dosfstools/mtools installed
/// natively still runs the gate. Both invocation channels skip cleanly
/// via <see cref="Assert.Ignore"/> when neither is reachable, so CI hosts
/// without the tools see a clean pass instead of a red test.</para>
///
/// <para>Three image sizes exercise distinct on-disk layouts that the BPB
/// auto-selection code must get right:
/// <list type="bullet">
///   <item>FAT12 — 1.44 MB floppy (2 880 sectors). Smallest BPB,
///     12-bit FAT entries, fixed root-dir area.</item>
///   <item>FAT16 — ~16 MB (32 768 sectors). 16-bit FAT entries,
///     fixed root-dir area, larger reserved region.</item>
///   <item>FAT32 — ~256 MB (524 288 sectors). 32-bit FAT, FSInfo
///     sector, backup boot sector, root-dir as a cluster chain.</item>
/// </list>
/// </para>
///
/// <para>An <c>mtools mdir -b -/</c> listing test confirms that an
/// independent FAT reader sees the exact file set we wrote — this catches
/// directory-entry corruption that <c>fsck.fat -n</c> doesn't model (it
/// validates the FAT chain, not the LFN slot content).</para>
///
/// <para>A "bites" guard test corrupts the second FAT copy of a clean
/// image and asserts the conformance check rejects it — proving the gate
/// can actually fail when something is wrong, not just exit 0 on garbage
/// because <c>fsck.fat -n</c> happens to be lenient about FAT mismatches.</para>
/// </summary>
[TestFixture]
[Category("ExternalConformance")]
public class FatExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_fat_conf_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── Tool routing: WSL preferred (Windows dev/CI), host PATH fallback ─

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

  /// <summary>
  /// Routes a Linux tool invocation through WSL (preferred — the way this
  /// project's CI hosts have dosfstools/mtools installed) and falls back to
  /// the host PATH so a native Linux CI host with the tool installed locally
  /// still runs the gate. The path tail is appended after the tool-specific
  /// flags via the channel-appropriate quoting form (single-quoted POSIX for
  /// WSL bash, double-quoted Windows for host PATH), so callers never
  /// hand-quote the image path themselves. Returns <c>null</c> when neither
  /// channel can serve the request; the caller should Assert.Ignore.
  /// </summary>
  private static ToolResult? RunValidator(string tool, string flags, string imagePath) {
    if (WslAvailable && WslHasTool(tool))
      // WinToWsl returns the POSIX path wrapped in single quotes already, so
      // it slots into a bash -c argument verbatim without further quoting.
      return RunWsl($"{tool} {flags} {WinToWsl(imagePath)}");
    if (TryFromPath(tool, out var hostTool))
      // RunExact passes args to ProcessStartInfo unchanged; the OS argv
      // splitter respects double-quotes around the Windows path.
      return RunExact(hostTool, $"{flags} \"{imagePath}\"");
    return null;
  }

  /// <summary>
  /// Invokes <c>mdir -b -/ -i &lt;image&gt; ::/</c> through whichever channel
  /// has mtools. mdir uses <c>-i</c> as the image-file flag, so the path goes
  /// inside the flag's value (not as a trailing positional arg), which means
  /// the generic <see cref="RunValidator"/> path-tail composer doesn't fit;
  /// this helper does the channel-aware quoting locally.
  /// </summary>
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
      $"{tool} not reachable. Install via WSL (`sudo apt install -y {aptHint}`) or add it to host PATH. " +
      $"The Stage-2 external-tool gate skips cleanly when no validator is available.");
  }

  // ── fsck.fat result interpretation ──────────────────────────────────
  //
  // fsck.fat -n is "no-op": it reports problems but never repairs. Crucially
  // it returns exit 0 even for FAT-copy mismatches and a wrong FSInfo free
  // count ("Auto-correcting." — which it then doesn't do, because -n). So the
  // exit code alone is NOT a reliable pass signal; we must also assert the
  // output is free of fsck.fat's problem phrases. These phrases appear only
  // when fsck flags a defect — the benign header line "Checking free cluster
  // summary." is deliberately excluded.
  private static readonly string[] FsckFatProblemPhrases = [
    "differ",            // "FATs differ ..."
    "wrong",             // "Free cluster summary wrong ..."
    "Invalid",           // "Invalid '.' entry ..."
    "Fixing",            // structural repair
    "Auto-correcting",   // FSInfo repair
    "Corrupt",
    "corrupted",
    "orphan",
    "truncated",
    "mismatch",
    "Dropping",
    "Reclaiming",
    "Removing",
    "Unaligned",
    "bad cluster",
    "Cluster start",     // "Cluster start does/should ..."
    "contains a bad",
  ];

  private static (bool Clean, string Report) RunFsckFat(string imagePath) {
    var r = RunValidator("fsck.fat", "-n -V", imagePath)
      ?? throw new InvalidOperationException("fsck.fat unreachable — caller must RequireValidator first");
    var combined = r.StdOut + "\n" + r.StdErr;
    // fsck.fat echoes the image path on its trailing summary line
    // ("<path>: N files, ...") and again on any error line that names the
    // file. Strip the path (in both Windows and WSL forms) before phrase-
    // matching, so a temp dir literally containing e.g. "corrupt" can't
    // contribute false positives.
    var scan = combined
      .Replace(imagePath, "<image>", StringComparison.Ordinal)
      .Replace(WinToWsl(imagePath).Trim('\''), "<image>", StringComparison.Ordinal);
    var flagged = FsckFatProblemPhrases
      .Where(p => scan.Contains(p, StringComparison.OrdinalIgnoreCase))
      .ToList();
    var clean = r.ExitCode == 0 && flagged.Count == 0;
    var report = $"exit={r.ExitCode}; flagged=[{string.Join(", ", flagged)}]\n{combined}";
    return (clean, report);
  }

  // ── Deterministic test payloads ─────────────────────────────────────

  private static byte[] Bytes(int n, int seed) {
    var r = new Random(seed);
    var b = new byte[n];
    r.NextBytes(b);
    return b;
  }

  /// <summary>FAT12 1.44 MB floppy: a root file, a two-level nested tree, and
  /// a single subdirectory holding ~50 small files.</summary>
  private static byte[] BuildFat12Floppy() {
    var w = new FatWriter();
    w.AddFile("README.TXT", Encoding.ASCII.GetBytes("conformance probe: FAT12 root file"));
    w.AddFile("docs/readme.md", Bytes(1234, 1));
    w.AddFile("docs/sub/deep.bin", Bytes(2000, 2));
    for (var i = 0; i < 50; i++)
      w.AddFile($"many/file{i:D3}.dat", Bytes(100 + i, 100 + i));
    return w.Build(totalSectors: 2880, forcedFatType: 12);
  }

  /// <summary>FAT16 ~16 MB image with a nested tree and a moderate subdirectory.
  /// 32 768 sectors × 512 B = 16 MiB, which lands in the FAT16 cluster-count
  /// range (4 085 – 65 524 clusters) regardless of the auto-selected
  /// cluster size.</summary>
  private static byte[] BuildFat16_16MB() {
    var w = new FatWriter();
    w.AddFile("ROOT.TXT", Encoding.ASCII.GetBytes("conformance probe: FAT16 root file"));
    w.AddFile("dir1/data.bin", Bytes(5000, 10));
    w.AddFile("dir1/dir2/nested.bin", Bytes(9000, 11));
    for (var i = 0; i < 30; i++)
      w.AddFile($"stuff/item{i:D2}.txt", Bytes(500 + i * 7, 200 + i));
    return w.Build(totalSectors: 32_768, forcedFatType: 16);
  }

  /// <summary>FAT32 ~256 MB image with a three-level nested tree, a large file,
  /// and a subdirectory holding ~500 files. 524 288 sectors × 512 B = 256 MiB.
  /// Exercises the FSInfo sector, the backup boot sector, and the root-dir-as-
  /// cluster-chain path.</summary>
  private static byte[] BuildFat32_256MB() {
    var w = new FatWriter();
    w.AddFile("README.TXT", Encoding.ASCII.GetBytes("conformance probe: FAT32 root file"));
    w.AddFile("a/b/c/deep.bin", Bytes(70_000, 20));
    w.AddFile("big.bin", Bytes(300_000, 21));
    for (var i = 0; i < 500; i++)
      w.AddFile($"bigdir/f{i:D4}.dat", Bytes(50 + i % 200, 1000 + i));
    return w.Build(totalSectors: 524_288, forcedFatType: 32);
  }

  private string WriteImage(string name, byte[] image) {
    var path = Path.Combine(_tmpDir, name);
    File.WriteAllBytes(path, image);
    return path;
  }

  // ═══════════════════════════════════════════════════════════════════
  // Given a FatWriter image, when fsck.fat -n verifies it, then it is clean.
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Fat12FloppyImage_IsCleanUnderFsckFat() {
    RequireValidator("fsck.fat", "dosfstools");
    var path = WriteImage("fat12.img", BuildFat12Floppy());
    var (clean, report) = RunFsckFat(path);
    Assert.That(clean, Is.True, $"fsck.fat flagged the FAT12 floppy image:\n{report}");
  }

  [Test]
  public void Fat16_16MB_IsCleanUnderFsckFat() {
    RequireValidator("fsck.fat", "dosfstools");
    var path = WriteImage("fat16.img", BuildFat16_16MB());
    var (clean, report) = RunFsckFat(path);
    Assert.That(clean, Is.True, $"fsck.fat flagged the FAT16 16 MB image:\n{report}");
  }

  [Test]
  [CancelAfter(180_000)]
  public void Fat32_256MB_IsCleanUnderFsckFat() {
    RequireValidator("fsck.fat", "dosfstools");
    var path = WriteImage("fat32.img", BuildFat32_256MB());
    var (clean, report) = RunFsckFat(path);
    Assert.That(clean, Is.True, $"fsck.fat flagged the FAT32 256 MB image:\n{report}");
  }

  /// <summary>The streaming <see cref="FatWriter.BuildTo"/> path must produce
  /// an image fsck.fat also considers clean (and, per the parity tests,
  /// byte-identical to <see cref="FatWriter.Build"/>).</summary>
  [Test]
  [CancelAfter(180_000)]
  public void Fat32StreamedImage_IsCleanUnderFsckFat() {
    RequireValidator("fsck.fat", "dosfstools");

    var w = new FatWriter();
    w.AddFile("README.TXT", Encoding.ASCII.GetBytes("conformance probe: streamed FAT32"));
    w.AddFile("a/b/c/deep.bin", Bytes(70_000, 20));
    w.AddFile("big.bin", Bytes(300_000, 21));
    for (var i = 0; i < 500; i++)
      w.AddFile($"bigdir/f{i:D4}.dat", Bytes(50 + i % 200, 1000 + i));

    var path = Path.Combine(_tmpDir, "fat32_streamed.img");
    using (var fs = File.Create(path))
      w.BuildTo(fs, totalSectors: 524_288, forcedFatType: 32);

    var (clean, report) = RunFsckFat(path);
    Assert.That(clean, Is.True, $"fsck.fat flagged the streamed FAT32 image:\n{report}");
  }

  // ═══════════════════════════════════════════════════════════════════
  // Given a FatWriter image, when mtools mdir lists it, then every file
  // we wrote appears at its expected path.
  //
  // mdir -b prints one entry per line in dotted-path form (e.g.
  // "::/SUBDIR/FILE.TXT"); -/ recurses into subdirs. This independent
  // FAT reader catches directory-entry corruption (broken LFN chains,
  // bad checksums, misordered slots) that fsck.fat -n does not model.
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Fat16Image_ListedByMtoolsMdir_MatchesExpectedFileSet() {
    RequireValidator("mdir", "mtools");

    // Curate a small, deterministic file set we can assert on exactly.
    // mdir uppercases all paths in its 8.3 view, so the expected set is
    // expressed in upper case too.
    var w = new FatWriter();
    w.AddFile("HELLO.TXT", "hello"u8.ToArray());
    w.AddFile("SUBDIR/DATA.BIN", Bytes(1000, 7));
    w.AddFile("SUBDIR/NESTED/DEEP.BIN", Bytes(2000, 8));
    w.AddFile("DOCS/README.MD", "# readme"u8.ToArray());
    var path = WriteImage("fat_mdir.img", w.Build(totalSectors: 32_768, forcedFatType: 16));

    var r = RunMdir(path)
      ?? throw new InvalidOperationException("mdir unreachable — RequireValidator should have skipped");
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"mdir failed on our FAT16 image:\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");

    // Normalise the listing: mtools uses backslashes on some builds, forward
    // slashes on others; tests assert on the canonical "::/PATH" form.
    var listing = r.StdOut.ToUpperInvariant().Replace('\\', '/');
    Assert.Multiple(() => {
      Assert.That(listing, Does.Contain("::/HELLO.TXT"), "root file HELLO.TXT must appear in mdir listing");
      Assert.That(listing, Does.Contain("::/SUBDIR/DATA.BIN"), "nested SUBDIR/DATA.BIN must appear at its full path");
      Assert.That(listing, Does.Contain("::/SUBDIR/NESTED/DEEP.BIN"), "two-level-nested SUBDIR/NESTED/DEEP.BIN must appear at its full path");
      Assert.That(listing, Does.Contain("::/DOCS/README.MD"), "DOCS/README.MD must appear at its full path");
    });
  }

  // ═══════════════════════════════════════════════════════════════════
  // Guard: prove the conformance assertion actually bites.
  //
  // fsck.fat -n returns exit 0 even for a FAT-copy mismatch, so a naive
  // "exit code only" assertion would silently pass on a corrupt image. This
  // test corrupts the second FAT copy of an otherwise-clean image and shows
  // RunFsckFat reports it as NOT clean — i.e. the phrase-based check is what
  // makes the conformance tests above meaningful.
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  [CancelAfter(180_000)]
  public void DeliberatelyCorruptedSecondFat_IsRejected_ProvingTheCheckBites() {
    RequireValidator("fsck.fat", "dosfstools");

    var image = BuildFat32_256MB();
    var path = WriteImage("fat32_corrupt.img", image);

    // Sanity: the pristine image is clean.
    var (cleanBefore, reportBefore) = RunFsckFat(path);
    Assert.That(cleanBefore, Is.True, $"baseline image was not clean:\n{reportBefore}");

    // Corrupt the second FAT copy so it diverges from the first. fsck.fat
    // detects this as "FATs differ" but still exits 0 under -n.
    var bytesPerSector = BitConverter.ToUInt16(image, 11);
    var reservedSectors = BitConverter.ToUInt16(image, 14);
    var fatSize = BitConverter.ToUInt32(image, 36); // BPB_FATSz32
    var secondFatByteOffset = (reservedSectors + fatSize) * bytesPerSector;
    // Flip an in-use entry (cluster 3) in the second FAT only.
    var entryOffset = (int)secondFatByteOffset + 3 * 4;
    image[entryOffset] ^= 0xFF;
    image[entryOffset + 1] ^= 0xFF;
    File.WriteAllBytes(path, image);

    var (cleanAfter, reportAfter) = RunFsckFat(path);
    Assert.That(cleanAfter, Is.False,
      $"corruption went undetected — the conformance check does not bite:\n{reportAfter}");
  }
}
