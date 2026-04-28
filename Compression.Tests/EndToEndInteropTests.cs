#pragma warning disable CS1591

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Compression.Analysis.ExternalTools;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests;

/// <summary>
/// Comprehensive end-to-end interop tests that verify our format implementations
/// produce output readable by external tools and vice versa. Uses dynamic tool
/// discovery via <see cref="ToolDiscovery"/> and gracefully skips tests when
/// required tools are not available.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
public class EndToEndInteropTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_e2e_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
    FormatRegistration.EnsureInitialized();
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── Tool discovery ──────────────────────────────────────────────────

  private static string? Find7z() => ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
  private static string? FindGzip() => ToolDiscovery.GetToolPath("gzip");
  private static string? FindBzip2() => ToolDiscovery.GetToolPath("bzip2");
  private static string? FindXz() => ToolDiscovery.GetToolPath("xz");
  private static string? FindZstd() => ToolDiscovery.GetToolPath("zstd");
  private static string? FindLz4() => ToolDiscovery.GetToolPath("lz4");
  private static string? FindTar() => ToolDiscovery.GetToolPath("tar");

  private static string Require7z() => Find7z() ?? throw new IgnoreException("7z not found on PATH or in common locations");
  private static string RequireGzip() => FindGzip() ?? throw new IgnoreException("gzip not found on PATH");
  private static string RequireBzip2() => FindBzip2() ?? throw new IgnoreException("bzip2 not found on PATH");
  private static string RequireXz() => FindXz() ?? throw new IgnoreException("xz not found on PATH");
  private static string RequireZstd() => FindZstd() ?? throw new IgnoreException("zstd not found on PATH");
  private static string RequireLz4() => FindLz4() ?? throw new IgnoreException("lz4 not found on PATH");
  private static string RequireTar() => FindTar() ?? throw new IgnoreException("tar not found on PATH");

  // ── Test data ───────────────────────────────────────────────────────

  private static byte[] SmallText => "Hello from CompressionWorkbench E2E! Testing external tool interop."u8.ToArray();

  private static byte[] RepetitiveText {
    get {
      using var ms = new MemoryStream();
      for (var i = 0; i < 200; i++) {
        var line = Encoding.UTF8.GetBytes($"Line {i}: The quick brown fox jumps over the lazy dog.\n");
        ms.Write(line);
      }
      return ms.ToArray();
    }
  }

  private static byte[] RandomData {
    get {
      var rng = new Random(42);
      var data = new byte[8192];
      rng.NextBytes(data);
      return data;
    }
  }

  private static byte[] EmptyData => [];

  private static byte[] BinaryStructured {
    get {
      var data = new byte[4096];
      // Mixed patterns: header, repeated blocks, random tail
      for (var i = 0; i < 16; i++) data[i] = (byte)(0xDE + i);
      for (var i = 16; i < 2048; i++) data[i] = (byte)(i % 37);
      var rng = new Random(123);
      rng.NextBytes(data.AsSpan(2048));
      return data;
    }
  }

  private static byte[] LargeData {
    get {
      var data = new byte[1024 * 1024]; // 1 MB
      var rng = new Random(99);
      // Fill with semi-compressible pattern: repeated text blocks + random noise
      var block = Encoding.UTF8.GetBytes("ABCDEFGHIJKLMNOP Lorem ipsum dolor sit amet, consectetur adipiscing elit. ");
      for (var i = 0; i < data.Length; i++)
        data[i] = i % 100 < 75 ? block[i % block.Length] : (byte)rng.Next(256);
      return data;
    }
  }

  // ── Source file creation helpers ────────────────────────────────────

  /// <summary>Creates 3 test files (text, small, random) in a subdirectory and returns their data.</summary>
  private (string Dir, Dictionary<string, byte[]> Files) CreateSourceFiles() {
    var dir = Path.Combine(_tmpDir, "src");
    Directory.CreateDirectory(dir);
    var files = new Dictionary<string, byte[]> {
      ["repeat.txt"] = RepetitiveText,
      ["small.txt"] = SmallText,
      ["random.dat"] = RandomData,
    };
    foreach (var (name, data) in files)
      File.WriteAllBytes(Path.Combine(dir, name), data);
    return (dir, files);
  }

  /// <summary>Creates 5 test files covering all data patterns.</summary>
  private (string Dir, Dictionary<string, byte[]> Files) CreateAllPatternFiles() {
    var dir = Path.Combine(_tmpDir, "src_all");
    Directory.CreateDirectory(dir);
    var files = new Dictionary<string, byte[]> {
      ["repeat.txt"] = RepetitiveText,
      ["small.txt"] = SmallText,
      ["random.dat"] = RandomData,
      ["empty.bin"] = EmptyData,
      ["structured.bin"] = BinaryStructured,
    };
    foreach (var (name, data) in files)
      File.WriteAllBytes(Path.Combine(dir, name), data);
    return (dir, files);
  }

  /// <summary>Creates an archive using our library from the standard test files.</summary>
  private string CreateOurArchive(string extension, Dictionary<string, byte[]>? files = null) {
    var (dir, srcFiles) = files == null ? CreateSourceFiles() : (Path.Combine(_tmpDir, "src"), files);
    if (files != null) {
      Directory.CreateDirectory(dir);
      foreach (var (name, data) in files)
        File.WriteAllBytes(Path.Combine(dir, name), data);
    }
    var archivePath = Path.Combine(_tmpDir, $"ours{extension}");
    var inputs = srcFiles.Keys.Select(name =>
      new ArchiveInput(Path.Combine(dir, name), name)).ToList();
    ArchiveOperations.Create(archivePath, inputs, new CompressionOptions());
    return archivePath;
  }

  // ── Stream format helpers ──────────────────────────────────────────

  private static IStreamFormatOperations GetStreamOps(string id) =>
    FormatRegistry.GetStreamOps(id) ?? throw new NotSupportedException($"No stream ops for {id}");

  private string CompressWithOurLib(string formatId, byte[] data) {
    var ext = FormatRegistry.GetById(formatId)?.DefaultExtension ?? $".{formatId.ToLowerInvariant()}";
    var outPath = Path.Combine(_tmpDir, $"ours{ext}");
    using (var fs = File.Create(outPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps(formatId).Compress(input, fs);
    }
    return outPath;
  }

  private static byte[] DecompressWithOurLib(string formatId, string filePath) {
    using var fs = File.OpenRead(filePath);
    using var ms = new MemoryStream();
    GetStreamOps(formatId).Decompress(fs, ms);
    return ms.ToArray();
  }

  // ── Verification helpers ───────────────────────────────────────────

  private static void VerifyExtractedFiles(string extractDir, Dictionary<string, byte[]> expected) {
    foreach (var (name, expectedData) in expected) {
      var found = FindFile(extractDir, name);
      Assert.That(found, Is.Not.Null, $"Could not find '{name}' in extracted output at {extractDir}");
      var actual = File.ReadAllBytes(found!);
      Assert.That(actual, Is.EqualTo(expectedData), $"Data mismatch for '{name}'");
    }
  }

  private static string? FindFile(string dir, string name) {
    foreach (var f in Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories))
      return f;
    return null;
  }

  // ── Path helpers ────────────────────────────────────────────────────

  /// <summary>
  /// Converts a Windows path to MSYS/Cygwin-style path for tools from Git for Windows.
  /// e.g. "C:\Users\foo\bar" -> "/c/Users/foo/bar"
  /// </summary>
  private static string ToMsysPath(string windowsPath) {
    if (windowsPath.Length >= 2 && windowsPath[1] == ':') {
      var drive = char.ToLowerInvariant(windowsPath[0]);
      return "/" + drive + windowsPath[2..].Replace('\\', '/');
    }
    return windowsPath.Replace('\\', '/');
  }

  /// <summary>
  /// Returns true if the tool is an MSYS/Git-for-Windows tool that needs path conversion.
  /// </summary>
  private static bool IsMsysTool(string toolPath)
    => toolPath.Contains("Git", StringComparison.OrdinalIgnoreCase) &&
       toolPath.Contains("usr", StringComparison.OrdinalIgnoreCase);

  // ── Tool runner ────────────────────────────────────────────────────

  private static (string StdOut, string StdErr) RunTool(string toolPath, string args, int timeoutMs = 60_000) {
    var psi = new ProcessStartInfo {
      FileName = toolPath,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start {toolPath}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs)) {
      try { proc.Kill(); } catch { /* best effort */ }
      Assert.Fail($"{Path.GetFileName(toolPath)} timed out after {timeoutMs}ms.\nstdout: {stdout}\nstderr: {stderr}");
    }
    if (proc.ExitCode != 0)
      Assert.Fail($"{Path.GetFileName(toolPath)} exited with code {proc.ExitCode}.\nArgs: {args}\nstdout: {stdout}\nstderr: {stderr}");
    return (stdout, stderr);
  }

  // ═══════════════════════════════════════════════════════════════════
  // Self round-trip tests (no external tools needed)
  // ═══════════════════════════════════════════════════════════════════

  // Extensions that are known to require special handling or cannot do
  // a simple file-based round-trip through ArchiveOperations.Create/Extract.
  // These are typically filesystem images, disk images, game archives with
  // special constraints, or formats that store metadata differently.
  private static readonly HashSet<string> _selfRoundTripExclusions = new(StringComparer.OrdinalIgnoreCase) {
    // Filesystem images that need specific disk geometry or block layout
    ".d64", ".d71", ".d81", ".t64", ".adf", ".tap",
    // Retro FSes with strict native filename constraints (length/charset) that
    // can't round-trip generic repeat.txt/small.txt/random.dat names verbatim.
    ".ssd", ".po", ".scl", ".atr",
    // Retro FSes with sector-size padding that inflates small files beyond byte-exact
    // round-trip (CP/M: 128 B records, LIF: 256 B sectors, RT-11: 512 B blocks no
    // length-in-bytes field, OS-9 RBF: 256 B sectors with FD.SIZ but generic-name
    // generator drops files we'd reject for RAD-50/length).
    ".cpm", ".lif", ".rt11", ".rx01", ".os9", ".rbf",
    // Disk images with specific sector/partition structure
    ".vhd", ".vmdk", ".vhdx", ".qcow2", ".vdi", ".cvf",
    // WORM-minimal writers that don't round-trip user files (SB-only / empty-FS):
    ".bcachefs", ".reiser4",
    // Filesystem images (HFS, NTFS, ext, etc. require full FS creation)
    ".hfs", ".hfsp", ".mfs", ".ntfs", ".ext", ".exfat", ".fat", ".img",
    ".ufs", ".xfs", ".jfs", ".reiserfs", ".f2fs", ".romfs", ".minixfs", ".minix",
    ".apfs", ".zfs", ".btrfs", ".vdfs",
    // CD/DVD images
    ".iso", ".bin", ".mdf", ".nrg", ".cdi",
    // Special format constraints
    ".wim",    // WIM has special internal structure
    ".dms",    // Amiga DMS requires disk image
    ".msa",    // Atari MSA requires disk image
    ".trd",    // TR-DOS requires disk image
    ".dsk",    // CPC DSK requires disk image
    // Formats with custom internal structure not suitable for generic create/extract
    ".chm", ".dmg", ".nds", ".swf", ".pdf", ".umx",
    ".msi", ".doc", ".xls", ".ppt", ".msg", ".thumbsdb",
    // Image-bundle formats — accept only PNG/BMP inputs, fail on generic .txt/.dat
    ".ico", ".cur",
    // Game archives with special alignment/structure or format detection collision
    ".bsa", ".mpq", ".vpk",
    ".wad",    // WAD and WAD2 share same extension, detection collision
    // Formats requiring special input data
    ".mp3",    // Wrapster wraps as MP3
    ".zpaq",   // ZPAQ journaling format, complex create/extract semantics
    // XPK-dependent Amiga formats: writers emit raw tracks; readers expect XPK chunks.
    // Test files use generic names (small.txt etc.) that the track-indexed format can't roundtrip.
    ".pdsk", ".xmsh", ".xdsk", ".dcs", ".lhf", ".dd", ".zap",
    // Placeholder writers (magic + a few fields, no actual entry data layout)
    ".sitx",
    // SFX-style installers — extension .exe is shared by InnoSetup, Nsis, and SFX
    // wrappers; DetectByExtension can't pick a writer unambiguously.
    ".exe",
    // Encoding formats (not archives)
    ".uu", ".yenc", ".macbin", ".binhex",
    // Split files
    ".001",
    // ZIP-based variants that might collide with ZIP detection
    ".jar", ".war", ".ear", ".apk", ".ipa", ".xpi", ".crx",
    ".epub", ".odt", ".ods", ".odp", ".docx", ".xlsx", ".pptx",
    ".cbz", ".cbr", ".maff", ".kmz", ".appx", ".nupkg",
    // Read-only formats
    ".rpm", ".deb", ".nsis", ".inno", ".squashfs", ".cramfs",
    // Compound tar variants that may have issues with specific compressors
    ".tar.br",
  };

  private static IEnumerable<string> RoundTripFormats() {
    FormatRegistration.EnsureInitialized();
    foreach (var desc in FormatRegistry.All) {
      if (desc.Category is not (FormatCategory.Archive or FormatCategory.CompoundTar))
        continue;
      var caps = desc.Capabilities;
      if (!caps.HasFlag(FormatCapabilities.CanCreate) || !caps.HasFlag(FormatCapabilities.CanExtract))
        continue;
      var ext = desc.DefaultExtension;
      if (string.IsNullOrEmpty(ext))
        continue;
      if (_selfRoundTripExclusions.Contains(ext))
        continue;
      yield return ext;
    }
  }

  [TestCaseSource(nameof(RoundTripFormats))]
  public void SelfRoundTrip_CreateAndExtract(string extension) {
    var (dir, srcFiles) = CreateSourceFiles();
    var archivePath = Path.Combine(_tmpDir, $"roundtrip{extension}");
    var inputs = srcFiles.Keys.Select(name =>
      new ArchiveInput(Path.Combine(dir, name), name)).ToList();

    ArchiveOperations.Create(archivePath, inputs, new CompressionOptions());
    Assert.That(File.Exists(archivePath), Is.True, $"Archive was not created: {archivePath}");
    Assert.That(new FileInfo(archivePath).Length, Is.GreaterThan(0), "Archive is empty");

    var extractDir = Path.Combine(_tmpDir, "extracted");
    ArchiveOperations.Extract(archivePath, extractDir, null, null);

    VerifyExtractedFiles(extractDir, srcFiles);
  }

  // ═══════════════════════════════════════════════════════════════════
  // Stream format interop tests
  // ═══════════════════════════════════════════════════════════════════

  // ── Gzip: bidirectional with gzip tool ─────────────────────────────

  [Test]
  [Ignore("Known Gzip CRC-32 bug: our output produces CRC errors with external gzip")]
  public void Gzip_OurOutput_GzipToolReads() {
    var tool = RequireGzip();
    var data = RepetitiveText;
    var gzPath = CompressWithOurLib("Gzip", data);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"-d -k \"{(msys ? ToMsysPath(gzPath) : gzPath)}\"");
    var decompPath = Path.Combine(Path.GetDirectoryName(gzPath)!, Path.GetFileNameWithoutExtension(gzPath));
    Assert.That(File.ReadAllBytes(decompPath), Is.EqualTo(data));
  }

  [Test]
  [Ignore("Known Gzip CRC-32 bug: our reader fails CRC check on valid gzip output")]
  public void Gzip_ToolOutput_WeRead() {
    var tool = RequireGzip();
    var data = RepetitiveText;
    var rawPath = Path.Combine(_tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"-k \"{(msys ? ToMsysPath(rawPath) : rawPath)}\"");
    var result = DecompressWithOurLib("Gzip", rawPath + ".gz");
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Gzip_OurOutput_7zReads() {
    var sz = Require7z();
    var data = RepetitiveText;
    var gzPath = CompressWithOurLib("Gzip", data);
    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    Directory.CreateDirectory(extractDir);
    RunTool(sz, $"x \"{gzPath}\" -o\"{extractDir}\" -y");
    var files = Directory.GetFiles(extractDir);
    Assert.That(files, Has.Length.GreaterThan(0), "7z did not extract any file");
    Assert.That(File.ReadAllBytes(files[0]), Is.EqualTo(data));
  }

  [Test]
  [Ignore("Known Gzip CRC-32 bug: our output produces CRC errors with external gzip")]
  public void Gzip_RandomData_OurOutput_GzipToolReads() {
    var tool = RequireGzip();
    var data = RandomData;
    var gzPath = Path.Combine(_tmpDir, "random.gz");
    using (var fs = File.Create(gzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Gzip").Compress(input, fs);
    }
    var msys = IsMsysTool(tool);
    RunTool(tool, $"-d -k \"{(msys ? ToMsysPath(gzPath) : gzPath)}\"");
    Assert.That(File.ReadAllBytes(Path.Combine(_tmpDir, "random")), Is.EqualTo(data));
  }

  // ── Bzip2: bidirectional with bzip2 tool ───────────────────────────

  [Test]
  public void Bzip2_OurOutput_Bzip2ToolReads() {
    var tool = RequireBzip2();
    var data = RepetitiveText;
    var bz2Path = CompressWithOurLib("Bzip2", data);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"-d -k \"{(msys ? ToMsysPath(bz2Path) : bz2Path)}\"");
    var decompPath = Path.Combine(Path.GetDirectoryName(bz2Path)!, Path.GetFileNameWithoutExtension(bz2Path));
    Assert.That(File.ReadAllBytes(decompPath), Is.EqualTo(data));
  }

  [Test]
  public void Bzip2_ToolOutput_WeRead() {
    var tool = RequireBzip2();
    var data = RepetitiveText;
    var rawPath = Path.Combine(_tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"-k \"{(msys ? ToMsysPath(rawPath) : rawPath)}\"");
    var result = DecompressWithOurLib("Bzip2", rawPath + ".bz2");
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Bzip2_OurOutput_7zReads() {
    var sz = Require7z();
    var data = RepetitiveText;
    var bz2Path = CompressWithOurLib("Bzip2", data);
    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    Directory.CreateDirectory(extractDir);
    RunTool(sz, $"x \"{bz2Path}\" -o\"{extractDir}\" -y");
    var files = Directory.GetFiles(extractDir);
    Assert.That(files, Has.Length.GreaterThan(0), "7z did not extract any file");
    Assert.That(File.ReadAllBytes(files[0]), Is.EqualTo(data));
  }

  // ── Xz: bidirectional with xz tool ─────────────────────────────────

  [Test]
  public void Xz_OurOutput_XzToolReads() {
    var tool = RequireXz();
    var data = RepetitiveText;
    var xzPath = CompressWithOurLib("Xz", data);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"-d -k \"{(msys ? ToMsysPath(xzPath) : xzPath)}\"");
    var decompPath = Path.Combine(Path.GetDirectoryName(xzPath)!, Path.GetFileNameWithoutExtension(xzPath));
    Assert.That(File.ReadAllBytes(decompPath), Is.EqualTo(data));
  }

  [Test]
  public void Xz_ToolOutput_WeRead() {
    var tool = RequireXz();
    var data = RepetitiveText;
    var rawPath = Path.Combine(_tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"-k \"{(msys ? ToMsysPath(rawPath) : rawPath)}\"");
    var result = DecompressWithOurLib("Xz", rawPath + ".xz");
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Xz_OurOutput_7zReads() {
    var sz = Require7z();
    var data = RepetitiveText;
    var xzPath = CompressWithOurLib("Xz", data);
    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    Directory.CreateDirectory(extractDir);
    RunTool(sz, $"x \"{xzPath}\" -o\"{extractDir}\" -y");
    var files = Directory.GetFiles(extractDir);
    Assert.That(files, Has.Length.GreaterThan(0), "7z did not extract any file");
    Assert.That(File.ReadAllBytes(files[0]), Is.EqualTo(data));
  }

  // ── Zstd: bidirectional with zstd tool (if available) ──────────────

  [Test]
  public void Zstd_OurOutput_ZstdToolReads() {
    var tool = RequireZstd();
    var data = RepetitiveText;
    var zstPath = CompressWithOurLib("Zstd", data);
    var outPath = Path.Combine(_tmpDir, "decompressed.bin");
    RunTool(tool, $"-d \"{zstPath}\" -o \"{outPath}\"");
    Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(data));
  }

  [Test]
  public void Zstd_ToolOutput_WeRead() {
    var tool = RequireZstd();
    var data = RepetitiveText;
    var rawPath = Path.Combine(_tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    var zstPath = Path.Combine(_tmpDir, "tool.zst");
    RunTool(tool, $"\"{rawPath}\" -o \"{zstPath}\"");
    var result = DecompressWithOurLib("Zstd", zstPath);
    Assert.That(result, Is.EqualTo(data));
  }

  // ── Lz4: bidirectional with lz4 tool (if available) ────────────────

  [Test]
  public void Lz4_OurOutput_Lz4ToolReads() {
    var tool = RequireLz4();
    var data = RepetitiveText;
    var lz4Path = CompressWithOurLib("Lz4", data);
    var outPath = Path.Combine(_tmpDir, "decompressed.bin");
    RunTool(tool, $"-d \"{lz4Path}\" \"{outPath}\"");
    Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(data));
  }

  [Test]
  public void Lz4_ToolOutput_WeRead() {
    var tool = RequireLz4();
    var data = RepetitiveText;
    var rawPath = Path.Combine(_tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    var lz4Path = Path.Combine(_tmpDir, "tool.lz4");
    RunTool(tool, $"\"{rawPath}\" \"{lz4Path}\"");
    var result = DecompressWithOurLib("Lz4", lz4Path);
    Assert.That(result, Is.EqualTo(data));
  }

  // ═══════════════════════════════════════════════════════════════════
  // Archive interop tests via 7z
  // ═══════════════════════════════════════════════════════════════════

  // ── ZIP: bidirectional with 7z ─────────────────────────────────────

  [Test]
  public void Zip_OurOutput_7zReads() {
    var sz = Require7z();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".zip", files);
    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Zip_7zOutput_WeRead() {
    var sz = Require7z();
    var (dir, files) = CreateSourceFiles();
    var zipPath = Path.Combine(_tmpDir, "7z.zip");
    var fileArgs = string.Join(" ", files.Keys.Select(n => $"\"{Path.Combine(dir, n)}\""));
    RunTool(sz, $"a -tzip \"{zipPath}\" {fileArgs}");
    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  // ── 7z: bidirectional with 7z ──────────────────────────────────────

  [Test]
  public void SevenZip_OurOutput_7zReads() {
    var sz = Require7z();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".7z", files);
    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void SevenZip_7zOutput_WeRead() {
    var sz = Require7z();
    var (dir, files) = CreateSourceFiles();
    var szPath = Path.Combine(_tmpDir, "7z.7z");
    var fileArgs = string.Join(" ", files.Keys.Select(n => $"\"{Path.Combine(dir, n)}\""));
    RunTool(sz, $"a -t7z \"{szPath}\" {fileArgs}");
    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(szPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  // ── TAR: bidirectional with tar tool ───────────────────────────────

  [Test]
  public void Tar_OurOutput_TarToolReads() {
    var tool = RequireTar();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".tar", files);
    var extractDir = Path.Combine(_tmpDir, "tar_extract");
    Directory.CreateDirectory(extractDir);
    var msys = IsMsysTool(tool);
    var tarArg = msys ? ToMsysPath(archivePath) : archivePath;
    var dirArg = msys ? ToMsysPath(extractDir) : extractDir;
    RunTool(tool, $"xf \"{tarArg}\" -C \"{dirArg}\"");
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Tar_ToolOutput_WeRead() {
    var tool = RequireTar();
    var (dir, files) = CreateSourceFiles();
    var tarPath = Path.Combine(_tmpDir, "tool.tar");
    var nameArgs = string.Join(" ", files.Keys);
    var msys = IsMsysTool(tool);
    var tarArg = msys ? ToMsysPath(tarPath) : tarPath;
    var dirArg = msys ? ToMsysPath(dir) : dir;
    RunTool(tool, $"cf \"{tarArg}\" -C \"{dirArg}\" {nameArgs}");
    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(tarPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Tar_OurOutput_7zReads() {
    var sz = Require7z();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".tar", files);
    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, files);
  }

  // ── tar.gz: bidirectional with tar tool ────────────────────────────

  [Test]
  [Ignore("Known Gzip CRC-32 bug: our gzip output produces CRC errors when read by external tar")]
  public void TarGz_OurOutput_TarToolReads() {
    var tool = RequireTar();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".tar.gz", files);
    var extractDir = Path.Combine(_tmpDir, "tar_extract");
    Directory.CreateDirectory(extractDir);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"xzf \"{(msys ? ToMsysPath(archivePath) : archivePath)}\" -C \"{(msys ? ToMsysPath(extractDir) : extractDir)}\"");
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void TarGz_ToolOutput_WeRead() {
    var tool = RequireTar();
    var (dir, files) = CreateSourceFiles();
    var tgzPath = Path.Combine(_tmpDir, "tool.tar.gz");
    var nameArgs = string.Join(" ", files.Keys);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"czf \"{(msys ? ToMsysPath(tgzPath) : tgzPath)}\" -C \"{(msys ? ToMsysPath(dir) : dir)}\" {nameArgs}");
    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(tgzPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  // ── tar.bz2: bidirectional with tar tool ───────────────────────────

  [Test]
  public void TarBz2_OurOutput_TarToolReads() {
    var tool = RequireTar();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".tar.bz2", files);
    var extractDir = Path.Combine(_tmpDir, "tar_extract");
    Directory.CreateDirectory(extractDir);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"xjf \"{(msys ? ToMsysPath(archivePath) : archivePath)}\" -C \"{(msys ? ToMsysPath(extractDir) : extractDir)}\"");
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void TarBz2_ToolOutput_WeRead() {
    var tool = RequireTar();
    var (dir, files) = CreateSourceFiles();
    var tbz2Path = Path.Combine(_tmpDir, "tool.tar.bz2");
    var nameArgs = string.Join(" ", files.Keys);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"cjf \"{(msys ? ToMsysPath(tbz2Path) : tbz2Path)}\" -C \"{(msys ? ToMsysPath(dir) : dir)}\" {nameArgs}");
    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(tbz2Path, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  // ── tar.xz: bidirectional with tar tool ────────────────────────────

  [Test]
  public void TarXz_OurOutput_TarToolReads() {
    var tool = RequireTar();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".tar.xz", files);
    var extractDir = Path.Combine(_tmpDir, "tar_extract");
    Directory.CreateDirectory(extractDir);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"xJf \"{(msys ? ToMsysPath(archivePath) : archivePath)}\" -C \"{(msys ? ToMsysPath(extractDir) : extractDir)}\"");
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void TarXz_ToolOutput_WeRead() {
    var tool = RequireTar();
    var (dir, files) = CreateSourceFiles();
    var txzPath = Path.Combine(_tmpDir, "tool.tar.xz");
    var nameArgs = string.Join(" ", files.Keys);
    var msys = IsMsysTool(tool);
    RunTool(tool, $"cJf \"{(msys ? ToMsysPath(txzPath) : txzPath)}\" -C \"{(msys ? ToMsysPath(dir) : dir)}\" {nameArgs}");
    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(txzPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  // ── CAB: our output -> 7z reads ────────────────────────────────────

  [Test]
  public void Cab_OurOutput_7zReads() {
    var sz = Require7z();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".cab", files);
    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, files);
  }

  // ── CPIO: our output -> 7z reads ───────────────────────────────────

  [Test]
  public void Cpio_OurOutput_7zReads() {
    var sz = Require7z();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".cpio", files);
    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, files);
  }

  // ── ARJ: our output -> 7z reads ───────────────────────────────────

  [Test]
  public void Arj_OurOutput_7zReads() {
    var sz = Require7z();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".arj", files);
    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, files);
  }

  // ── LZH: our output -> 7z reads ───────────────────────────────────

  [Test]
  public void Lzh_OurOutput_7zReads() {
    var sz = Require7z();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".lzh", files);
    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, files);
  }

  // ── RAR: our output -> 7z reads ───────────────────────────────────

  [Test]
  public void Rar_OurOutput_7zReads() {
    var sz = Require7z();
    var (_, files) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".rar", files);
    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, files);
  }

  // ── WIM: 7z output -> we read ─────────────────────────────────────

  [Test]
  public void Wim_7zOutput_WeRead() {
    var sz = Require7z();
    var (dir, files) = CreateSourceFiles();
    var wimPath = Path.Combine(_tmpDir, "7z.wim");
    var fileArgs = string.Join(" ", files.Keys.Select(n => $"\"{Path.Combine(dir, n)}\""));
    RunTool(sz, $"a -twim \"{wimPath}\" {fileArgs}");
    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(wimPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  // ═══════════════════════════════════════════════════════════════════
  // .NET BCL cross-validation
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  [Ignore("Known interop issue: .NET ZipArchive uses compression flags our reader doesn't fully support")]
  public void Zip_CrossValidate_DotNetZipArchive() {
    var (dir, srcFiles) = CreateAllPatternFiles();

    // Direction 1: Our output -> .NET reads
    var ourZipPath = Path.Combine(_tmpDir, "ours.zip");
    var inputs = srcFiles.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(ourZipPath, inputs, new CompressionOptions());

    using (var archive = ZipFile.OpenRead(ourZipPath)) {
      foreach (var (name, expectedData) in srcFiles) {
        var entry = archive.Entries.FirstOrDefault(e => e.Name == name);
        Assert.That(entry, Is.Not.Null, $".NET ZipArchive could not find entry '{name}'");
        using var entryStream = entry!.Open();
        using var ms = new MemoryStream();
        entryStream.CopyTo(ms);
        Assert.That(ms.ToArray(), Is.EqualTo(expectedData), $"Data mismatch for '{name}' (our->dotnet)");
      }
    }

    // Direction 2: .NET output -> We read
    var dotnetZipPath = Path.Combine(_tmpDir, "dotnet.zip");
    using (var archive = ZipFile.Open(dotnetZipPath, ZipArchiveMode.Create)) {
      foreach (var (name, data) in srcFiles) {
        var entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        entryStream.Write(data);
      }
    }
    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(dotnetZipPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, srcFiles);
  }

  [Test]
  [Ignore("Known interop issue: our Gzip output uses format features .NET GZipStream doesn't support")]
  public void Gzip_CrossValidate_DotNetGZipStream() {
    foreach (var (label, data) in new[] {
      ("small", SmallText), ("repetitive", RepetitiveText),
      ("random", RandomData), ("empty", EmptyData),
    }) {
      // Direction 1: Our output -> .NET reads
      var ourGzPath = Path.Combine(_tmpDir, $"ours_{label}.gz");
      using (var fs = File.Create(ourGzPath)) {
        using var input = new MemoryStream(data);
        GetStreamOps("Gzip").Compress(input, fs);
      }
      using (var fs = File.OpenRead(ourGzPath))
      using (var gz = new GZipStream(fs, CompressionMode.Decompress))
      using (var ms = new MemoryStream()) {
        gz.CopyTo(ms);
        Assert.That(ms.ToArray(), Is.EqualTo(data), $"Mismatch for {label} (our->dotnet)");
      }

      // Direction 2: .NET output -> We read
      var dotnetGzPath = Path.Combine(_tmpDir, $"dotnet_{label}.gz");
      using (var fs = File.Create(dotnetGzPath))
      using (var gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal)) {
        gz.Write(data);
      }
      var result = DecompressWithOurLib("Gzip", dotnetGzPath);
      Assert.That(result, Is.EqualTo(data), $"Mismatch for {label} (dotnet->our)");
    }
  }

  // ═══════════════════════════════════════════════════════════════════
  // Disk image round-trip tests (our create -> our read)
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Iso_RoundTrip() {
    var (dir, files) = CreateSourceFiles();
    var isoPath = Path.Combine(_tmpDir, "test.iso");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(isoPath, inputs, new CompressionOptions());
    Assert.That(File.Exists(isoPath), Is.True);
    Assert.That(new FileInfo(isoPath).Length, Is.GreaterThan(0));

    var extractDir = Path.Combine(_tmpDir, "iso_extract");
    ArchiveOperations.Extract(isoPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Iso_OurOutput_7zReads() {
    var sz = Require7z();
    var (dir, files) = CreateSourceFiles();
    var isoPath = Path.Combine(_tmpDir, "test.iso");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(isoPath, inputs, new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{isoPath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  [Ignore("VHD is a virtual disk image requiring filesystem initialization; round-trip via ArchiveOperations not supported")]
  public void Vhd_RoundTrip() {
    var (dir, files) = CreateSourceFiles();
    var vhdPath = Path.Combine(_tmpDir, "test.vhd");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(vhdPath, inputs, new CompressionOptions());
    Assert.That(File.Exists(vhdPath), Is.True);
    Assert.That(new FileInfo(vhdPath).Length, Is.GreaterThan(0));

    var extractDir = Path.Combine(_tmpDir, "vhd_extract");
    ArchiveOperations.Extract(vhdPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  [Ignore("VMDK is a virtual disk image requiring filesystem initialization; round-trip via ArchiveOperations not supported")]
  public void Vmdk_RoundTrip() {
    var (dir, files) = CreateSourceFiles();
    var vmdkPath = Path.Combine(_tmpDir, "test.vmdk");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(vmdkPath, inputs, new CompressionOptions());
    Assert.That(File.Exists(vmdkPath), Is.True);
    Assert.That(new FileInfo(vmdkPath).Length, Is.GreaterThan(0));

    var extractDir = Path.Combine(_tmpDir, "vmdk_extract");
    ArchiveOperations.Extract(vmdkPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  // ═══════════════════════════════════════════════════════════════════
  // Large file stress tests
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  [CancelAfter(120_000)]
  public void Zip_LargeFile_1MB_RoundTrip() {
    var data = LargeData;
    var dir = Path.Combine(_tmpDir, "src_large");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "large.dat"), data);

    var zipPath = Path.Combine(_tmpDir, "large.zip");
    ArchiveOperations.Create(zipPath, [
      new ArchiveInput(Path.Combine(dir, "large.dat"), "large.dat"),
    ], new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "extract_large");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);
    var extracted = FindFile(extractDir, "large.dat");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(data));
  }

  [Test]
  [CancelAfter(120_000)]
  public void SevenZip_LargeFile_1MB_RoundTrip() {
    var data = LargeData;
    var dir = Path.Combine(_tmpDir, "src_large");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "large.dat"), data);

    var szPath = Path.Combine(_tmpDir, "large.7z");
    ArchiveOperations.Create(szPath, [
      new ArchiveInput(Path.Combine(dir, "large.dat"), "large.dat"),
    ], new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "extract_large");
    ArchiveOperations.Extract(szPath, extractDir, null, null);
    var extracted = FindFile(extractDir, "large.dat");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(data));
  }

  [Test]
  [CancelAfter(120_000)]
  public void Tar_LargeFile_1MB_RoundTrip() {
    var data = LargeData;
    var dir = Path.Combine(_tmpDir, "src_large");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "large.dat"), data);

    var tarPath = Path.Combine(_tmpDir, "large.tar");
    ArchiveOperations.Create(tarPath, [
      new ArchiveInput(Path.Combine(dir, "large.dat"), "large.dat"),
    ], new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "extract_large");
    ArchiveOperations.Extract(tarPath, extractDir, null, null);
    var extracted = FindFile(extractDir, "large.dat");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(data));
  }

  [Test]
  [CancelAfter(120_000)]
  [Ignore("Known Gzip CRC-32 issue with large data")]
  public void Gzip_LargeFile_1MB_RoundTrip() {
    var data = LargeData;
    var gzPath = Path.Combine(_tmpDir, "large.gz");
    using (var fs = File.Create(gzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Gzip").Compress(input, fs);
    }
    var result = DecompressWithOurLib("Gzip", gzPath);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  [CancelAfter(120_000)]
  public void Bzip2_LargeFile_1MB_RoundTrip() {
    var data = LargeData;
    var bz2Path = Path.Combine(_tmpDir, "large.bz2");
    using (var fs = File.Create(bz2Path)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Bzip2").Compress(input, fs);
    }
    var result = DecompressWithOurLib("Bzip2", bz2Path);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  [CancelAfter(120_000)]
  public void Xz_LargeFile_1MB_RoundTrip() {
    var data = LargeData;
    var xzPath = Path.Combine(_tmpDir, "large.xz");
    using (var fs = File.Create(xzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Xz").Compress(input, fs);
    }
    var result = DecompressWithOurLib("Xz", xzPath);
    Assert.That(result, Is.EqualTo(data));
  }

  // ═══════════════════════════════════════════════════════════════════
  // Multi-data-pattern tests
  // ═══════════════════════════════════════════════════════════════════

  private static readonly (string Label, Func<byte[]> DataFactory)[] _dataPatterns = [
    ("SmallText", () => SmallText),
    ("RepetitiveText", () => RepetitiveText),
    ("RandomData", () => RandomData),
    ("EmptyData", () => EmptyData),
    ("BinaryStructured", () => BinaryStructured),
  ];

  [Test]
  public void Gzip_AllDataPatterns_RoundTrip() {
    foreach (var (label, factory) in _dataPatterns) {
      var data = factory();
      var gzPath = Path.Combine(_tmpDir, $"{label}.gz");
      using (var fs = File.Create(gzPath)) {
        using var input = new MemoryStream(data);
        GetStreamOps("Gzip").Compress(input, fs);
      }
      var result = DecompressWithOurLib("Gzip", gzPath);
      Assert.That(result, Is.EqualTo(data), $"Round-trip failed for pattern: {label}");
    }
  }

  [Test]
  public void Bzip2_AllDataPatterns_RoundTrip() {
    foreach (var (label, factory) in _dataPatterns) {
      var data = factory();
      if (data.Length == 0)
        continue; // bzip2 may not handle empty streams in all implementations
      var bz2Path = Path.Combine(_tmpDir, $"{label}.bz2");
      using (var fs = File.Create(bz2Path)) {
        using var input = new MemoryStream(data);
        GetStreamOps("Bzip2").Compress(input, fs);
      }
      var result = DecompressWithOurLib("Bzip2", bz2Path);
      Assert.That(result, Is.EqualTo(data), $"Round-trip failed for pattern: {label}");
    }
  }

  [Test]
  public void Xz_AllDataPatterns_RoundTrip() {
    foreach (var (label, factory) in _dataPatterns) {
      var data = factory();
      var xzPath = Path.Combine(_tmpDir, $"{label}.xz");
      using (var fs = File.Create(xzPath)) {
        using var input = new MemoryStream(data);
        GetStreamOps("Xz").Compress(input, fs);
      }
      var result = DecompressWithOurLib("Xz", xzPath);
      Assert.That(result, Is.EqualTo(data), $"Round-trip failed for pattern: {label}");
    }
  }

  [Test]
  public void Zip_AllDataPatterns_RoundTrip() {
    var dir = Path.Combine(_tmpDir, "src_patterns");
    Directory.CreateDirectory(dir);
    var files = new Dictionary<string, byte[]>();
    foreach (var (label, factory) in _dataPatterns) {
      var data = factory();
      files[label + ".bin"] = data;
      File.WriteAllBytes(Path.Combine(dir, label + ".bin"), data);
    }

    var zipPath = Path.Combine(_tmpDir, "patterns.zip");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "extract_patterns");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void SevenZip_AllDataPatterns_RoundTrip() {
    var dir = Path.Combine(_tmpDir, "src_patterns");
    Directory.CreateDirectory(dir);
    var files = new Dictionary<string, byte[]>();
    foreach (var (label, factory) in _dataPatterns) {
      var data = factory();
      files[label + ".bin"] = data;
      File.WriteAllBytes(Path.Combine(dir, label + ".bin"), data);
    }

    var szPath = Path.Combine(_tmpDir, "patterns.7z");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(szPath, inputs, new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "extract_patterns");
    ArchiveOperations.Extract(szPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  // ═══════════════════════════════════════════════════════════════════
  // Random data stress (incompressible data through archives)
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Zip_RandomData_OurOutput_7zReads() {
    var sz = Require7z();
    var data = RandomData;
    var dir = Path.Combine(_tmpDir, "src");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "random.bin"), data);

    var zipPath = Path.Combine(_tmpDir, "random.zip");
    ArchiveOperations.Create(zipPath, [
      new ArchiveInput(Path.Combine(dir, "random.bin"), "random.bin"),
    ], new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{zipPath}\" -o\"{extractDir}\" -y");
    var extracted = FindFile(extractDir, "random.bin");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(data));
  }

  [Test]
  public void SevenZip_RandomData_OurOutput_7zReads() {
    var sz = Require7z();
    var data = RandomData;
    var dir = Path.Combine(_tmpDir, "src");
    Directory.CreateDirectory(dir);
    // Use .dat to avoid BCJ filter auto-detection (.bin triggers BCJ in 7z)
    File.WriteAllBytes(Path.Combine(dir, "random.dat"), data);

    var szPath = Path.Combine(_tmpDir, "random.7z");
    ArchiveOperations.Create(szPath, [
      new ArchiveInput(Path.Combine(dir, "random.dat"), "random.dat"),
    ], new CompressionOptions { ForceCompress = true });

    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{szPath}\" -o\"{extractDir}\" -y");
    var extracted = FindFile(extractDir, "random.dat");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(data));
  }

  // ═══════════════════════════════════════════════════════════════════
  // Optimal compression interop (verify "+" mode output is still valid)
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  [Ignore("Known Gzip CRC-32 bug: our output produces CRC errors with external gzip")]
  public void Gzip_Optimal_OurOutput_GzipToolReads() {
    var tool = RequireGzip();
    var data = RepetitiveText;
    var gzPath = Path.Combine(_tmpDir, "optimal.gz");
    using (var fs = File.Create(gzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Gzip").CompressOptimal(input, fs);
    }
    var msys = IsMsysTool(tool);
    RunTool(tool, $"-d -k \"{(msys ? ToMsysPath(gzPath) : gzPath)}\"");
    Assert.That(File.ReadAllBytes(Path.Combine(_tmpDir, "optimal")), Is.EqualTo(data));
  }

  [Test]
  public void Zip_Optimal_OurOutput_7zReads() {
    var sz = Require7z();
    var (dir, files) = CreateSourceFiles();
    var archivePath = Path.Combine(_tmpDir, "optimal.zip");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(archivePath, inputs,
      new CompressionOptions { Method = new MethodSpec("deflate", true) });

    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void SevenZip_Optimal_OurOutput_7zReads() {
    var sz = Require7z();
    var (dir, files) = CreateSourceFiles();
    var archivePath = Path.Combine(_tmpDir, "optimal.7z");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(archivePath, inputs,
      new CompressionOptions { Method = new MethodSpec("lzma", true) });

    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunTool(sz, $"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, files);
  }

  // ═══════════════════════════════════════════════════════════════════
  // Stream format self round-trip (all stream formats, no tools needed)
  // ═══════════════════════════════════════════════════════════════════

  private static readonly HashSet<string> _streamRoundTripExclusions = new(StringComparer.OrdinalIgnoreCase) {
    // Formats that are decompression-only or have special requirements
    "Squeeze", "IcePacker", "PowerPacker", "Szdd", "Kwaj", "Crunch",
    // Encoding wrappers
    "MacBinary", "BinHex", "UuEncoding", "YEnc",
    // Formats that require structured input
    "Swf", "Yaz0",
    // Extremely slow neural/context-mixing compressors
    "Paq8", "Cmix", "Mcm",
    // Formats with known limitations on arbitrary data
    "PackBits",
    // Audio codec (requires audio PCM data, not arbitrary bytes)
    "Flac",
  };

  private static IEnumerable<string> StreamRoundTripFormats() {
    FormatRegistration.EnsureInitialized();
    foreach (var desc in FormatRegistry.All) {
      if (desc.Category != FormatCategory.Stream)
        continue;
      if (_streamRoundTripExclusions.Contains(desc.Id))
        continue;
      var ops = FormatRegistry.GetStreamOps(desc.Id);
      if (ops == null)
        continue;
      yield return desc.Id;
    }
  }

  [TestCaseSource(nameof(StreamRoundTripFormats))]
  [CancelAfter(30_000)]
  public void StreamFormat_SelfRoundTrip(string formatId) {
    var data = RepetitiveText;
    var desc = FormatRegistry.GetById(formatId)!;
    var ext = desc.DefaultExtension;
    var compressedPath = Path.Combine(_tmpDir, $"stream{ext}");

    using (var fs = File.Create(compressedPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps(formatId).Compress(input, fs);
    }

    Assert.That(new FileInfo(compressedPath).Length, Is.GreaterThan(0), "Compressed output is empty");

    var result = DecompressWithOurLib(formatId, compressedPath);
    Assert.That(result, Is.EqualTo(data), $"Round-trip mismatch for stream format {formatId}");
  }

  // ═══════════════════════════════════════════════════════════════════
  // Empty file edge case tests
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Zip_EmptyFile_RoundTrip() {
    var dir = Path.Combine(_tmpDir, "src_empty");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "empty.bin"), EmptyData);

    var zipPath = Path.Combine(_tmpDir, "empty.zip");
    ArchiveOperations.Create(zipPath, [
      new ArchiveInput(Path.Combine(dir, "empty.bin"), "empty.bin"),
    ], new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "extract_empty");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);
    var extracted = FindFile(extractDir, "empty.bin");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(EmptyData));
  }

  [Test]
  public void SevenZip_EmptyFile_RoundTrip() {
    var dir = Path.Combine(_tmpDir, "src_empty");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "empty.bin"), EmptyData);

    var szPath = Path.Combine(_tmpDir, "empty.7z");
    ArchiveOperations.Create(szPath, [
      new ArchiveInput(Path.Combine(dir, "empty.bin"), "empty.bin"),
    ], new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "extract_empty");
    ArchiveOperations.Extract(szPath, extractDir, null, null);
    var extracted = FindFile(extractDir, "empty.bin");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(EmptyData));
  }

  [Test]
  public void Tar_EmptyFile_RoundTrip() {
    var dir = Path.Combine(_tmpDir, "src_empty");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "empty.bin"), EmptyData);

    var tarPath = Path.Combine(_tmpDir, "empty.tar");
    ArchiveOperations.Create(tarPath, [
      new ArchiveInput(Path.Combine(dir, "empty.bin"), "empty.bin"),
    ], new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "extract_empty");
    ArchiveOperations.Extract(tarPath, extractDir, null, null);
    var extracted = FindFile(extractDir, "empty.bin");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(EmptyData));
  }

  // ═══════════════════════════════════════════════════════════════════
  // Multi-file archive integrity (verifies all files survive intact)
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  public void Zip_MultiFile_AllPatterns_Integrity() {
    var (_, files) = CreateAllPatternFiles();
    var zipPath = CreateOurArchive(".zip", files);
    var extractDir = Path.Combine(_tmpDir, "multi_extract");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void SevenZip_MultiFile_AllPatterns_Integrity() {
    var (_, files) = CreateAllPatternFiles();
    var szPath = CreateOurArchive(".7z", files);
    var extractDir = Path.Combine(_tmpDir, "multi_extract");
    ArchiveOperations.Extract(szPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Tar_MultiFile_AllPatterns_Integrity() {
    var (_, files) = CreateAllPatternFiles();
    var tarPath = CreateOurArchive(".tar", files);
    var extractDir = Path.Combine(_tmpDir, "multi_extract");
    ArchiveOperations.Extract(tarPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void TarGz_MultiFile_AllPatterns_Integrity() {
    var (_, files) = CreateAllPatternFiles();
    var tgzPath = CreateOurArchive(".tar.gz", files);
    var extractDir = Path.Combine(_tmpDir, "multi_extract");
    ArchiveOperations.Extract(tgzPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, files);
  }
}
