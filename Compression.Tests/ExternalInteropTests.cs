using System.Diagnostics;
using System.Text;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests;

/// <summary>
/// Tests that verify our format implementations produce output readable by
/// external system tools and vice versa.
/// Requires: gzip, bzip2, xz, tar (Git for Windows), 7z
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class ExternalInteropTests {
  /// <summary>
  /// Well-known install locations checked before falling back to PATH. The
  /// portable entries come first because a developer box with 7-ZipPortable
  /// usually has no 7z on PATH at all.
  /// </summary>
  private static readonly string[] SevenZipCandidates = [
    @"D:\PortableApps\7-ZipPortable\App\7-Zip64\7z.exe",
    @"D:\PortableApps\7-ZipPortable\App\7-Zip\7z.exe",
    @"C:\Program Files\7-Zip\7z.exe",
    @"C:\Program Files (x86)\7-Zip\7z.exe",
  ];

  /// <summary>
  /// Resolved 7-Zip executable, or null when none is installed. `7zz` is the
  /// official upstream binary name, `7za` the standalone/p7zip one — both are
  /// accepted so Linux and macOS hosts find a usable binary.
  /// </summary>
  private static readonly string? SevenZipPath =
    SevenZipCandidates.FirstOrDefault(File.Exists)
    ?? ResolveOnPath("7z") ?? ResolveOnPath("7zz") ?? ResolveOnPath("7za");

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_interop_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
    FormatRegistration.EnsureInitialized();
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Test data ──────────────────────────────────────────────────────

  private static byte[] SmallText => "Hello from CompressionWorkbench! Testing external tool interop."u8.ToArray();

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

  private static IStreamFormatOperations GetStreamOps(string id) =>
    FormatRegistry.GetStreamOps(id) ?? throw new NotSupportedException($"No stream ops for {id}");

  /// <summary>Creates source files and returns their paths and expected data.</summary>
  private (string Dir, byte[] File1, byte[] File2) CreateSourceFiles() {
    var dir = Path.Combine(this._tmpDir, "src");
    Directory.CreateDirectory(dir);
    var file1 = RepetitiveText;
    var file2 = SmallText;
    File.WriteAllBytes(Path.Combine(dir, "repeat.txt"), file1);
    File.WriteAllBytes(Path.Combine(dir, "small.txt"), file2);
    return (dir, file1, file2);
  }

  /// <summary>Creates an archive with our tool from two test files.</summary>
  private string CreateOurArchive(string extension) {
    var (dir, _, _) = CreateSourceFiles();
    var archivePath = Path.Combine(this._tmpDir, $"ours{extension}");
    ArchiveOperations.Create(archivePath, [
      new ArchiveInput(Path.Combine(dir, "repeat.txt"), "repeat.txt"),
      new ArchiveInput(Path.Combine(dir, "small.txt"), "small.txt"),
    ], new CompressionOptions());
    return archivePath;
  }

  /// <summary>Verifies extracted files match expected data.</summary>
  private static void VerifyExtractedFiles(string extractDir, byte[] expected1, byte[] expected2) {
    var f1 = FindFile(extractDir, "repeat.txt");
    Assert.That(f1, Is.Not.Null, "Couldn't find repeat.txt in extracted output");
    Assert.That(File.ReadAllBytes(f1!), Is.EqualTo(expected1));

    var f2 = FindFile(extractDir, "small.txt");
    Assert.That(f2, Is.Not.Null, "Couldn't find small.txt in extracted output");
    Assert.That(File.ReadAllBytes(f2!), Is.EqualTo(expected2));
  }

  // ── GZIP ───────────────────────────────────────────────────────────

  [Test]
  public void Gzip_OurOutput_ReadBySystemGzip() {
    var data = RepetitiveText;
    var gzPath = Path.Combine(this._tmpDir, "ours.gz");
    using (var fs = File.Create(gzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Gzip").Compress(input, fs);
    }
    RunTool("gzip", $"-d -k \"{gzPath}\"");
    Assert.That(File.ReadAllBytes(Path.Combine(this._tmpDir, "ours")), Is.EqualTo(data));
  }

  [Test]
  public void Gzip_SystemOutput_ReadByUs() {
    var data = RepetitiveText;
    var rawPath = Path.Combine(this._tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    RunTool("gzip", $"-k \"{rawPath}\"");
    using var fs = File.OpenRead(rawPath + ".gz");
    using var ms = new MemoryStream();
    GetStreamOps("Gzip").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void Gzip_OurOutput_RandomData_ReadBySystemGzip() {
    var data = RandomData;
    var gzPath = Path.Combine(this._tmpDir, "random.gz");
    using (var fs = File.Create(gzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Gzip").Compress(input, fs);
    }
    RunTool("gzip", $"-d -k \"{gzPath}\"");
    Assert.That(File.ReadAllBytes(Path.Combine(this._tmpDir, "random")), Is.EqualTo(data));
  }

  // ── BZIP2 ──────────────────────────────────────────────────────────

  [Test]
  public void Bzip2_OurOutput_ReadBySystemBzip2() {
    var data = RepetitiveText;
    var bz2Path = Path.Combine(this._tmpDir, "ours.bz2");
    using (var fs = File.Create(bz2Path)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Bzip2").Compress(input, fs);
    }
    RunTool("bzip2", $"-d -k \"{bz2Path}\"");
    Assert.That(File.ReadAllBytes(Path.Combine(this._tmpDir, "ours")), Is.EqualTo(data));
  }

  [Test]
  public void Bzip2_SystemOutput_ReadByUs() {
    var data = RepetitiveText;
    var rawPath = Path.Combine(this._tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    RunTool("bzip2", $"-k \"{rawPath}\"");
    using var fs = File.OpenRead(rawPath + ".bz2");
    using var ms = new MemoryStream();
    GetStreamOps("Bzip2").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  // ── XZ ─────────────────────────────────────────────────────────────

  [Test]
  public void Xz_OurOutput_ReadBySystemXz() {
    var data = RepetitiveText;
    var xzPath = Path.Combine(this._tmpDir, "ours.xz");
    using (var fs = File.Create(xzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Xz").Compress(input, fs);
    }
    RunTool("xz", $"-d -k \"{xzPath}\"");
    Assert.That(File.ReadAllBytes(Path.Combine(this._tmpDir, "ours")), Is.EqualTo(data));
  }

  [Test]
  public void Xz_SystemOutput_ReadByUs() {
    var data = RepetitiveText;
    var rawPath = Path.Combine(this._tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    RunTool("xz", $"-k \"{rawPath}\"");
    using var fs = File.OpenRead(rawPath + ".xz");
    using var ms = new MemoryStream();
    GetStreamOps("Xz").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  // ── ZSTD, LZ4, LZOP, BROTLI ────────────────────────────────────────

  /// <summary>
  /// The four stream codecs whose own tools are installed and which nothing was
  /// asking, with the switches each needs to name its output explicitly.
  /// </summary>
  /// <remarks>
  /// Each is checked both ways round. Writing something the tool refuses and
  /// reading something it wrote are different faults, and a codec can have one
  /// without the other — our deflate wrote blocks a strict inflater refused
  /// while reading everything put in front of it perfectly well.
  /// </remarks>
  private static IEnumerable<TestCaseData> StreamCodecs() {
    yield return new TestCaseData("Zstd", "zstd", "zst").SetName("Zstd via zstd");
    yield return new TestCaseData("Lz4", "lz4", "lz4").SetName("Lz4 via lz4");
    yield return new TestCaseData("Lzop", "lzop", "lzo").SetName("Lzop via lzop");
    yield return new TestCaseData("Brotli", "brotli", "br").SetName("Brotli via brotli");
  }

  [TestCaseSource(nameof(StreamCodecs))]
  public void StreamCodec_OurOutput_ReadByTheirTool(string formatId, string tool, string extension) {
    foreach (var data in new[] { RepetitiveText, RandomData, SmallText }) {
      var ours = Path.Combine(this._tmpDir, $"ours.{extension}");
      var back = Path.Combine(this._tmpDir, "back.bin");
      File.Delete(ours);
      File.Delete(back);

      using (var fs = File.Create(ours)) {
        using var input = new MemoryStream(data);
        GetStreamOps(formatId).Compress(input, fs);
      }

      RunTool(tool, DecompressArguments(tool, ours, back));
      Assert.That(File.ReadAllBytes(back), Is.EqualTo(data),
        $"{tool} read back something other than what {formatId} was given");
    }
  }

  [TestCaseSource(nameof(StreamCodecs))]
  public void StreamCodec_TheirOutput_ReadByUs(string formatId, string tool, string extension) {
    foreach (var data in new[] { RepetitiveText, RandomData, SmallText }) {
      var raw = Path.Combine(this._tmpDir, "input.bin");
      var theirs = Path.Combine(this._tmpDir, $"theirs.{extension}");
      File.Delete(theirs);
      File.WriteAllBytes(raw, data);

      RunTool(tool, CompressArguments(tool, raw, theirs));

      using var fs = File.OpenRead(theirs);
      using var ms = new MemoryStream();
      GetStreamOps(formatId).Decompress(fs, ms);
      Assert.That(ms.ToArray(), Is.EqualTo(data),
        $"{formatId} read back something other than what {tool} was given");
    }
  }

  private static string DecompressArguments(string tool, string from, string to) => tool switch {
    "lz4" => $"-d -f \"{from}\" \"{to}\"",
    _ => $"-d -f -o \"{to}\" \"{from}\"",
  };

  private static string CompressArguments(string tool, string from, string to) => tool switch {
    "lz4" => $"-f -9 \"{from}\" \"{to}\"",
    "brotli" => $"-f -q 11 -o \"{to}\" \"{from}\"",
    "zstd" => $"-f -19 -o \"{to}\" \"{from}\"",
    _ => $"-f -9 -o \"{to}\" \"{from}\"",
  };

  // ── TAR ────────────────────────────────────────────────────────────

  [Test]
  public void Tar_OurOutput_ReadBySystemTar() {
    var (_, file1, file2) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".tar");
    var extractDir = Path.Combine(this._tmpDir, "sys_extract");
    Directory.CreateDirectory(extractDir);
    RunTool("tar", $"xf \"{archivePath}\" -C \"{extractDir}\"");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  [Test]
  public void Tar_SystemOutput_ReadByUs() {
    var (dir, file1, _) = CreateSourceFiles();
    var tarPath = Path.Combine(this._tmpDir, "system.tar");
    RunTool("tar", $"cf \"{tarPath}\" -C \"{dir}\" repeat.txt small.txt");
    var extractDir = Path.Combine(this._tmpDir, "our_extract");
    ArchiveOperations.Extract(tarPath, extractDir, null, null);
    var f1 = FindFile(extractDir, "repeat.txt");
    Assert.That(f1, Is.Not.Null);
    Assert.That(File.ReadAllBytes(f1!), Is.EqualTo(file1));
  }

  // ── TAR.GZ / TAR.BZ2 / TAR.XZ compound ────────────────────────────

  [Test]
  public void TarGz_OurOutput_ReadBySystemTar() {
    var archivePath = CreateOurArchive(".tar.gz");
    var extractDir = Path.Combine(this._tmpDir, "extract");
    Directory.CreateDirectory(extractDir);
    RunTool("tar", $"xzf \"{archivePath}\" -C \"{extractDir}\"");
    VerifyExtractedFiles(extractDir, RepetitiveText, SmallText);
  }

  [Test]
  public void TarBz2_OurOutput_ReadBySystemTar() {
    var archivePath = CreateOurArchive(".tar.bz2");
    var extractDir = Path.Combine(this._tmpDir, "extract");
    Directory.CreateDirectory(extractDir);
    RunTool("tar", $"xjf \"{archivePath}\" -C \"{extractDir}\"");
    VerifyExtractedFiles(extractDir, RepetitiveText, SmallText);
  }

  [Test]
  public void TarXz_OurOutput_ReadBySystemTar() {
    var archivePath = CreateOurArchive(".tar.xz");
    var extractDir = Path.Combine(this._tmpDir, "extract");
    Directory.CreateDirectory(extractDir);
    RunTool("tar", $"xJf \"{archivePath}\" -C \"{extractDir}\"");
    VerifyExtractedFiles(extractDir, RepetitiveText, SmallText);
  }

  // ── ZIP via .NET + 7-Zip ───────────────────────────────────────────

  [Test]
  public void Zip_OurOutput_ReadByDotNet() {
    var data = RepetitiveText;
    var dir = Path.Combine(this._tmpDir, "src");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "data.bin"), data);
    var zipPath = Path.Combine(this._tmpDir, "ours.zip");
    ArchiveOperations.Create(zipPath, [
      new ArchiveInput(Path.Combine(dir, "data.bin"), "data.bin"),
    ], new CompressionOptions());
    using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
    var entry = archive.Entries.First(e => e.Name == "data.bin");
    using var entryStream = entry.Open();
    using var ms = new MemoryStream();
    entryStream.CopyTo(ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void Zip_OurOutput_ReadBy7Zip() {
    var (_, file1, file2) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".zip");
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  [Test]
  public void Zip_7ZipOutput_ReadByUs() {
    var (dir, file1, file2) = CreateSourceFiles();
    var zipPath = Path.Combine(this._tmpDir, "7z.zip");
    Run7z($"a -tzip \"{zipPath}\" \"{Path.Combine(dir, "repeat.txt")}\" \"{Path.Combine(dir, "small.txt")}\"");
    var extractDir = Path.Combine(this._tmpDir, "our_extract");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  // ── 7z ─────────────────────────────────────────────────────────────

  [Test]
  public void SevenZip_OurOutput_ReadBy7Zip() {
    var (_, file1, file2) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".7z");
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  [Test]
  public void SevenZip_7ZipOutput_ReadByUs() {
    var (dir, file1, file2) = CreateSourceFiles();
    var szPath = Path.Combine(this._tmpDir, "7z.7z");
    Run7z($"a -t7z \"{szPath}\" \"{Path.Combine(dir, "repeat.txt")}\" \"{Path.Combine(dir, "small.txt")}\"");
    var extractDir = Path.Combine(this._tmpDir, "our_extract");
    ArchiveOperations.Extract(szPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  [Test]
  public void SevenZip_OurOutput_LZMA_ReadBy7Zip() {
    var (dir, file1, file2) = CreateSourceFiles();
    var archivePath = Path.Combine(this._tmpDir, "ours_lzma.7z");
    ArchiveOperations.Create(archivePath, [
      new ArchiveInput(Path.Combine(dir, "repeat.txt"), "repeat.txt"),
      new ArchiveInput(Path.Combine(dir, "small.txt"), "small.txt"),
    ], new CompressionOptions { Method = new MethodSpec("lzma", false) });
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  // ── RAR (extract-only: our output → 7-Zip reads) ──────────────────

  [Test]
  public void Rar_OurOutput_ReadBy7Zip() {
    var (dir, file1, file2) = CreateSourceFiles();
    var archivePath = Path.Combine(this._tmpDir, "ours.rar");
    ArchiveOperations.Create(archivePath, [
      new ArchiveInput(Path.Combine(dir, "repeat.txt"), "repeat.txt"),
      new ArchiveInput(Path.Combine(dir, "small.txt"), "small.txt"),
    ], new CompressionOptions());
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  [Test]
  public void Rar_OurOutput_ReadBy7Zip_MultiFile() {
    var (dir, file1, file2) = CreateSourceFiles();
    var archivePath = Path.Combine(this._tmpDir, "ours_multi.rar");
    ArchiveOperations.Create(archivePath, [
      new ArchiveInput(Path.Combine(dir, "repeat.txt"), "repeat.txt"),
      new ArchiveInput(Path.Combine(dir, "small.txt"), "small.txt"),
    ], new CompressionOptions());
    var extractDir = Path.Combine(this._tmpDir, "7z_extract_multi");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  // ── LZH (our output → 7-Zip reads) ────────────────────────────────

  [Test]
  public void Lzh_OurOutput_ReadBy7Zip() {
    var (_, file1, file2) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".lzh");
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  // ── ARJ (our output → 7-Zip reads) ────────────────────────────────

  [Test]
  public void Arj_OurOutput_ReadBy7Zip() {
    var (_, file1, file2) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".arj");
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  // ── CAB (our output → 7-Zip reads) ────────────────────────────────

  [Test]
  public void Cab_OurOutput_ReadBy7Zip() {
    var (_, file1, file2) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".cab");
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  // ── WIM (7-Zip output → we read) ──────────────────────────────────

  [Test]
  public void Wim_7ZipOutput_ReadByUs() {
    var (dir, file1, file2) = CreateSourceFiles();
    var wimPath = Path.Combine(this._tmpDir, "7z.wim");
    Run7z($"a -twim \"{wimPath}\" \"{Path.Combine(dir, "repeat.txt")}\" \"{Path.Combine(dir, "small.txt")}\"");
    var extractDir = Path.Combine(this._tmpDir, "our_extract");
    ArchiveOperations.Extract(wimPath, extractDir, null, null);
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  // ── CPIO (our output → 7-Zip reads) ───────────────────────────────

  [Test]
  public void Cpio_OurOutput_ReadBy7Zip() {
    var (_, file1, file2) = CreateSourceFiles();
    var archivePath = CreateOurArchive(".cpio");
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  // ── Optimal compression (+ methods) ─────────────────────────────────

  [Test]
  public void Gzip_Optimal_OurOutput_ReadBySystemGzip() {
    var data = RepetitiveText;
    var gzPath = Path.Combine(this._tmpDir, "optimal.gz");
    using (var fs = File.Create(gzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Gzip").CompressOptimal(input, fs);
    }
    RunTool("gzip", $"-d -k \"{gzPath}\"");
    Assert.That(File.ReadAllBytes(Path.Combine(this._tmpDir, "optimal")), Is.EqualTo(data));
  }

  [Test]
  public void Zip_Optimal_OurOutput_ReadBy7Zip() {
    var (dir, file1, file2) = CreateSourceFiles();
    var archivePath = Path.Combine(this._tmpDir, "optimal.zip");
    ArchiveOperations.Create(archivePath, [
      new ArchiveInput(Path.Combine(dir, "repeat.txt"), "repeat.txt"),
      new ArchiveInput(Path.Combine(dir, "small.txt"), "small.txt"),
    ], new CompressionOptions { Method = new MethodSpec("deflate", true) });
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  [Test]
  public void SevenZip_Optimal_OurOutput_ReadBy7Zip() {
    var (dir, file1, file2) = CreateSourceFiles();
    var archivePath = Path.Combine(this._tmpDir, "optimal.7z");
    ArchiveOperations.Create(archivePath, [
      new ArchiveInput(Path.Combine(dir, "repeat.txt"), "repeat.txt"),
      new ArchiveInput(Path.Combine(dir, "small.txt"), "small.txt"),
    ], new CompressionOptions { Method = new MethodSpec("lzma", true) });
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    VerifyExtractedFiles(extractDir, file1, file2);
  }

  // ── Cross-format: random / large data ─────────────────────────────

  [Test]
  public void SevenZip_OurOutput_RandomData_ReadBy7Zip() {
    var data = RandomData;
    var dir = Path.Combine(this._tmpDir, "src");
    Directory.CreateDirectory(dir);
    // Use .dat extension to avoid BCJ filter auto-detection (.bin triggers BCJ)
    File.WriteAllBytes(Path.Combine(dir, "random.dat"), data);
    var archivePath = Path.Combine(this._tmpDir, "random.7z");
    ArchiveOperations.Create(archivePath, [
      new ArchiveInput(Path.Combine(dir, "random.dat"), "random.dat"),
    ], new CompressionOptions { ForceCompress = true });
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    var extracted = FindFile(extractDir, "random.dat");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(data));
  }

  [Test]
  public void Zip_OurOutput_RandomData_ReadBy7Zip() {
    var data = RandomData;
    var dir = Path.Combine(this._tmpDir, "src");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "random.bin"), data);
    var archivePath = Path.Combine(this._tmpDir, "random.zip");
    ArchiveOperations.Create(archivePath, [
      new ArchiveInput(Path.Combine(dir, "random.bin"), "random.bin"),
    ], new CompressionOptions());
    var extractDir = Path.Combine(this._tmpDir, "7z_extract");
    Run7z($"x \"{archivePath}\" -o\"{extractDir}\" -y");
    var extracted = FindFile(extractDir, "random.bin");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(data));
  }

  // ── Helpers ─────────────────────────────────────────────────────────

  private static void Run7z(string args) =>
    RunTool(SevenZipPath!, args);

  private static void Require7z() {
    if (SevenZipPath is null)
      Assert.Ignore("7-Zip not found — install p7zip / 7-Zip, or put 7z on PATH.");
  }

  /// <summary>
  /// Resolves a bare tool name against PATH. Callers pass names like "tar", and
  /// <see cref="File.Exists"/> alone never matches those — it does no PATH
  /// lookup, so every such test would skip itself on every platform.
  /// An argument that already contains a directory separator is taken as-is.
  /// </summary>
  private static string? ResolveOnPath(string tool) {
    if (tool.Contains(Path.DirectorySeparatorChar) || tool.Contains(Path.AltDirectorySeparatorChar))
      return File.Exists(tool) ? tool : null;

    var pathEnv = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(pathEnv)) return null;

    var exeName = OperatingSystem.IsWindows() && !tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
      ? tool + ".exe"
      : tool;

    foreach (var dir in pathEnv.Split(Path.PathSeparator)) {
      if (string.IsNullOrWhiteSpace(dir)) continue;
      string candidate;
      try {
        candidate = Path.Combine(dir.Trim(), exeName);
      } catch {
        continue; // malformed PATH entry (invalid chars) — ignore it
      }
      if (File.Exists(candidate)) return candidate;
    }
    return null;
  }

  private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _gnuTar = new(StringComparer.OrdinalIgnoreCase);

  private static bool IsGnuTar(string path) => _gnuTar.GetOrAdd(path, static p => {
    try {
      using var proc = Process.Start(new ProcessStartInfo {
        FileName = p, Arguments = "--version", RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true,
      })!;
      var text = proc.StandardOutput.ReadToEnd();
      proc.WaitForExit(10000);
      return text.Contains("GNU tar", StringComparison.Ordinal);
    } catch {
      return false;
    }
  });

  private static (string StdOut, string StdErr, int ExitCode) RunTool(string tool, string args) {
    var resolved = ResolveOnPath(tool);
    if (resolved is null)
      Assert.Ignore($"Tool not found: {tool}");
    // GNU tar (Git for Windows ships one) reads "C:" in an archive path as a remote host and fails
    // with "Cannot connect to C: resolve failed"; --force-local makes it a local file. Windows' own
    // bsdtar has no such option and needs none.
    if (tool == "tar" && OperatingSystem.IsWindows() && IsGnuTar(resolved!))
      args += " --force-local"; // last: the dash-less "xf" bundle must stay the first argument
    var psi = new ProcessStartInfo {
      FileName = resolved,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit(30000);
    if (proc.ExitCode != 0)
      Assert.Fail($"{Path.GetFileName(tool)} exited with code {proc.ExitCode}.\nstdout: {stdout}\nstderr: {stderr}");
    return (stdout, stderr, proc.ExitCode);
  }

  private static string? FindFile(string dir, string name) {
    foreach (var f in Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories))
      return f;
    return null;
  }

}
