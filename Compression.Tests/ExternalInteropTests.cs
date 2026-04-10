using System.Diagnostics;
using System.Text;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests;

/// <summary>
/// Tests that verify our format implementations produce output readable by
/// external system tools and vice versa.
/// Requires: gzip, bzip2, xz, tar (Git for Windows), 7z.exe
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class ExternalInteropTests {
  private const string SevenZipPath = @"D:\PortableApps\7-ZipPortable\App\7-Zip64\7z.exe";
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
    RunTool(SevenZipPath, args);

  private static void Require7z() {
    if (!File.Exists(SevenZipPath))
      Assert.Ignore($"7-Zip not found at {SevenZipPath}");
  }

  private static (string StdOut, string StdErr, int ExitCode) RunTool(string tool, string args) {
    if (!File.Exists(tool))
      Assert.Ignore($"Tool not found: {tool}");
    var psi = new ProcessStartInfo {
      FileName = tool,
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

  [Test]
  public void Rar_Diagnostic_LiteralsOnly() {
    // Test with data that produces NO matches (only literals) to isolate the bug
    foreach (var size in new[] { 197, 260, 261, 300, 500 }) {
      var data = new byte[size];
      for (var i = 0; i < size; i++) data[i] = (byte)((i * 7 + 13) & 0xFF);
      TestWith7z($"litonly_{size}", data);
    }
  }

  [Test]
  public void Rar_Diagnostic_MatchDistances() {
    // Test with truly random filler (PRNG) to avoid any unintended matches
    var distances = new[] { 3, 50, 100, 256, 257, 385, 512, 513, 1000 };
    foreach (var dist in distances) {
      var data = new byte[dist + 6];
      var rng = new Random(42);
      rng.NextBytes(data);
      data[0] = 0xAA; data[1] = 0xBB; data[2] = 0xCC;
      data[dist] = 0xAA; data[dist+1] = 0xBB; data[dist+2] = 0xCC;
      data[dist+3] = 0xDD; data[dist+4] = 0xEE; data[dist+5] = 0xFF;
      TestWith7z($"rng_dist_{dist}", data);
    }

    // Now test with 0xFF filler (many matches) at various distances
    foreach (var dist in new[] { 100, 200, 256, 257, 300, 385, 512 }) {
      var data = new byte[dist + 4];
      Array.Fill(data, (byte)0xFF);
      data[0] = 0x01; data[1] = 0x02; data[2] = 0x03;
      data[dist] = 0x01; data[dist+1] = 0x02; data[dist+2] = 0x03;
      data[dist+3] = 0xFE;
      TestWith7z($"ff_dist_{dist}", data);
    }

    // Separate distance from match count:
    // Fix match at dist 3 (always passes), vary 0xFF count
    TestContext.Out.WriteLine("\n--- Fixed dist=3, vary 0xFF count ---");
    foreach (var ffCount in new[] { 100, 200, 253, 254, 255, 256, 300 }) {
      var data = new byte[ffCount + 10];
      data[0] = 0x01; data[1] = 0x02; data[2] = 0x03;
      for (var i = 3; i < 3 + ffCount; i++) data[i] = 0xFF;
      data[3 + ffCount] = 0x01; data[4 + ffCount] = 0x02; data[5 + ffCount] = 0x03;
      for (var i = 6 + ffCount; i < data.Length; i++) data[i] = 0xFE;
      TestWith7z($"ff{ffCount}_d3", data);
    }

    // Fix 0xFF count at 100 (always passes), vary distance by adding random filler
    TestContext.Out.WriteLine("\n--- Fixed 100 0xFF, vary long-dist match ---");
    foreach (var dist in new[] { 200, 256, 257, 300, 385, 512 }) {
      var data = new byte[dist + 6];
      data[0] = 0xAA; data[1] = 0xBB; data[2] = 0xCC;
      for (var i = 3; i < 103; i++) data[i] = 0xFF; // 100 0xFF bytes
      var rng = new Random(42);
      for (var i = 103; i < dist; i++) data[i] = (byte)rng.Next(256);
      data[dist] = 0xAA; data[dist+1] = 0xBB; data[dist+2] = 0xCC;
      data[dist+3] = 0xDD; data[dist+4] = 0xEE; data[dist+5] = 0xFF;
      TestWith7z($"ff100_d{dist}", data);
    }

    // Fix 0xFF count at 254 (many matches), vary distance
    TestContext.Out.WriteLine("\n--- Fixed 254 0xFF, vary long-dist match ---");
    foreach (var dist in new[] { 256, 257, 258, 300, 385, 512 }) {
      var data = new byte[dist + 6];
      data[0] = 0xAA; data[1] = 0xBB; data[2] = 0xCC;
      for (var i = 3; i < 257; i++) data[i] = 0xFF;
      var rng = new Random(42);
      for (var i = 257; i < dist; i++) data[i] = (byte)rng.Next(256);
      data[dist] = 0xAA; data[dist+1] = 0xBB; data[dist+2] = 0xCC;
      data[dist+3] = 0xDD; data[dist+4] = 0xEE; data[dist+5] = 0xFF;
      TestWith7z($"ff254_d{dist}", data);
    }
  }

  private void TestWith7z(string label, byte[] data) {
    Require7z();
    var rarPath = Path.Combine(this._tmpDir, $"{label}.rar");
    using (var fs = File.Create(rarPath)) {
      using var w = new FileFormat.Rar.RarWriter(fs, method: 3, solid: false);
      w.AddFile("data.txt", data);
      w.Finish();
    }
    var psi = new ProcessStartInfo {
      FileName = SevenZipPath, Arguments = $"t \"{rarPath}\"",
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)!;
    proc.StandardOutput.ReadToEnd(); var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit(10000);
    var slot = dist_slot(data);
    TestContext.Out.WriteLine($"{label} ({data.Length}b): 7z={proc.ExitCode} {(proc.ExitCode == 0 ? "OK" : "FAIL")} {slot}");
    static string dist_slot(byte[] d) => "";
  }

  [Test]
  public void Rar_Diagnostic_MinimalFail() {
    Require7z();
    // dist257 is the minimal failing case: 1 match at distance 257 (slot 16, extraBits=7)
    // dist256 (slot 15, extraBits=6) passes. Let's compare their compressed data.

    foreach (var dist in new[] { 193, 256, 257 }) {
      var data = new byte[dist + 4];
      Array.Fill(data, (byte)0xFF);
      data[0] = 0x01; data[1] = 0x02; data[2] = 0x03;
      data[dist] = 0x01; data[dist + 1] = 0x02; data[dist + 2] = 0x03;
      data[dist + 3] = 0xFE;

      var encoder = new Compression.Core.Dictionary.Rar.Rar5Encoder();
      var compressed = encoder.Compress(data);

      TestContext.Out.WriteLine($"\n=== DISTANCE {dist} ({data.Length} bytes → {compressed.Length} compressed) ===");
      TestContext.Out.WriteLine($"Full compressed hex: {BitConverter.ToString(compressed)}");

      // Parse block header
      var pos = 0;
      var flags = compressed[pos++];
      var checksum = compressed[pos++];
      var byteCount = ((flags >> 3) & 3) + 1;
      var blockSize = 0;
      for (var b = 0; b < byteCount; ++b)
        blockSize += compressed[pos++] << (b * 8);
      var paddingBits = 7 - (flags & 7);
      TestContext.Out.WriteLine($"Header: flags=0x{flags:X2} check=0x{checksum:X2} byteCount={byteCount} blockSize={blockSize} padding={paddingBits}");

      // Parse tables with our bit reader
      var reader = new Compression.Core.Dictionary.Rar.Rar5BitReader(compressed[pos..]);

      // Pre-code
      var preCodes = new int[20];
      for (var i = 0; i < 20;) {
        var len = (int)reader.ReadBits(4);
        if (len == 15) {
          var count = (int)reader.ReadBits(4);
          if (count != 0) { for (var j = 0; j < count + 2 && i < 20; ++j) preCodes[i++] = 0; continue; }
        }
        preCodes[i++] = len;
      }
      TestContext.Out.WriteLine($"Pre-codes: [{string.Join(",", preCodes)}]");

      var clDec = new Compression.Core.Dictionary.Rar.Rar5HuffmanDecoder();
      clDec.Build(preCodes, 20);

      // Read all 4 tables
      var mainLens = ReadCodeLens(reader, clDec, 306);
      var offsetLens = ReadCodeLens(reader, clDec, 64);
      var lowOffsetLens = ReadCodeLens(reader, clDec, 16);
      var lengthLens = ReadCodeLens(reader, clDec, 44);

      // Print non-zero main table entries
      TestContext.Out.Write("Main table: ");
      for (var i = 0; i < 306; ++i)
        if (mainLens[i] > 0) {
          var desc = i < 256 ? $"0x{i:X2}" : i >= 262 ? $"m{i-262}" : $"s{i}";
          TestContext.Out.Write($"[{desc}]={mainLens[i]} ");
        }
      TestContext.Out.WriteLine();

      // Also print what the ENCODER computed
      {
        var enc2 = new Compression.Core.Dictionary.Rar.Rar5Encoder();
        // We can't easily get internal encoder state, so skip this for now
      }

      // Print non-zero entries in offset table
      TestContext.Out.Write("Offset table: ");
      for (var i = 0; i < 64; ++i)
        if (offsetLens[i] > 0) TestContext.Out.Write($"[{i}]={offsetLens[i]} ");
      TestContext.Out.WriteLine();

      TestContext.Out.Write("LowOffset table: ");
      for (var i = 0; i < 16; ++i)
        if (lowOffsetLens[i] > 0) TestContext.Out.Write($"[{i}]={lowOffsetLens[i]} ");
      TestContext.Out.WriteLine();

      // Build canonical codes for verification
      TestContext.Out.Write("Offset canonical: ");
      var offCodes = BuildCanonical(offsetLens, 64);
      for (var i = 0; i < 64; ++i)
        if (offsetLens[i] > 0) TestContext.Out.Write($"[{i}]=code {ToBin(offCodes[i], offsetLens[i])} ");
      TestContext.Out.WriteLine();

      TestContext.Out.Write("LowOffset canonical: ");
      var loCodes = BuildCanonical(lowOffsetLens, 16);
      for (var i = 0; i < 16; ++i)
        if (lowOffsetLens[i] > 0) TestContext.Out.Write($"[{i}]=code {ToBin(loCodes[i], lowOffsetLens[i])} ");
      TestContext.Out.WriteLine();

      TestContext.Out.WriteLine($"Bits consumed for tables: approx byte pos={reader.BytePosition}");

      // Now decode tokens one by one
      var mainDec = new Compression.Core.Dictionary.Rar.Rar5HuffmanDecoder();
      mainDec.Build(mainLens, 306);
      var offsetDec = new Compression.Core.Dictionary.Rar.Rar5HuffmanDecoder();
      offsetDec.Build(offsetLens, 64);
      var lowOffsetDec = new Compression.Core.Dictionary.Rar.Rar5HuffmanDecoder();
      lowOffsetDec.Build(lowOffsetLens, 16);
      var lengthDec = new Compression.Core.Dictionary.Rar.Rar5HuffmanDecoder();
      lengthDec.Build(lengthLens, 44);

      var output = new byte[data.Length];
      var outPos = 0;
      var tokenIdx = 0;
      while (outPos < data.Length && !reader.IsAtEnd) {
        var sym = mainDec.DecodeSymbol(reader);
        if (sym < 256) {
          output[outPos++] = (byte)sym;
          if (tokenIdx < 5 || outPos > data.Length - 10)
            TestContext.Out.WriteLine($"  T{tokenIdx}: Literal 0x{sym:X2} → outPos={outPos}");
        } else if (sym >= 262) {
          var lengthSlot = sym - 262;
          int matchLen;
          if (lengthSlot < 8) matchLen = lengthSlot + 2;
          else { var lb = lengthSlot / 4 - 1; matchLen = 2 + ((4 | (lengthSlot & 3)) << lb) + (int)reader.ReadBits(lb); }

          var distSlot = offsetDec.DecodeSymbol(reader);
          int distance;
          if (distSlot < 4) { distance = distSlot + 1; }
          else {
            var eb = (distSlot - 2) >> 1;
            var bd = (2 + (distSlot & 1)) << eb;
            if (eb >= 4) {
              var hi = eb > 4 ? (int)reader.ReadBits(eb - 4) : 0;
              var lo = lowOffsetDec.DecodeSymbol(reader);
              distance = bd + (hi << 4) + lo + 1;
              TestContext.Out.WriteLine($"  T{tokenIdx}: Match len={matchLen} distSlot={distSlot} eb={eb} hi={hi}({eb-4}bits) lo={lo} → dist={distance} outPos={outPos}");
            } else {
              var extra = (int)reader.ReadBits(eb);
              distance = bd + extra + 1;
              TestContext.Out.WriteLine($"  T{tokenIdx}: Match len={matchLen} distSlot={distSlot} eb={eb} extra={extra} → dist={distance} outPos={outPos}");
            }
          }
          for (var i = 0; i < matchLen && outPos < output.Length; ++i)
            output[outPos++] = outPos > distance ? output[outPos - 1 - distance] : (byte)0;
        }
        ++tokenIdx;
      }

      var ourOk = output.SequenceEqual(data);
      TestContext.Out.WriteLine($"Manual decode OK: {ourOk}");

      // Test with 7z
      var rarPath = Path.Combine(this._tmpDir, $"minifail_d{dist}.rar");
      using (var fs = File.Create(rarPath)) {
        using var w = new FileFormat.Rar.RarWriter(fs, method: 3, solid: false);
        w.AddFile("data.txt", data);
        w.Finish();
      }
      // Test with 7-Zip
      var psi = new ProcessStartInfo {
        FileName = SevenZipPath, Arguments = $"t \"{rarPath}\"",
        RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true,
      };
      using var proc = Process.Start(psi)!;
      var stdout7 = proc.StandardOutput.ReadToEnd();
      var stderr7 = proc.StandardError.ReadToEnd();
      proc.WaitForExit(10000);
      TestContext.Out.WriteLine($"7-Zip test: exit={proc.ExitCode} ({(proc.ExitCode == 0 ? "OK" : "FAIL")})");
      if (proc.ExitCode != 0) {
        TestContext.Out.WriteLine($"  stderr: {stderr7.Trim().Replace("\r\n", " | ")}");

        // Extract and compare
        var extractDir = Path.Combine(this._tmpDir, $"ext_d{dist}");
        Directory.CreateDirectory(extractDir);
        var epsi = new ProcessStartInfo {
          FileName = SevenZipPath, Arguments = $"x -o\"{extractDir}\" -y \"{rarPath}\"",
          RedirectStandardOutput = true, RedirectStandardError = true,
          UseShellExecute = false, CreateNoWindow = true,
        };
        using var eproc = Process.Start(epsi)!;
        eproc.StandardOutput.ReadToEnd(); eproc.StandardError.ReadToEnd();
        eproc.WaitForExit(10000);
        var extFile = Path.Combine(extractDir, "data.txt");
        if (File.Exists(extFile)) {
          var ext = File.ReadAllBytes(extFile);
          var match = ext.SequenceEqual(data);
          TestContext.Out.WriteLine($"  7-Zip extracted: {ext.Length} bytes, correct={match}");
          if (!match) {
            for (var x = 0; x < Math.Min(ext.Length, data.Length); ++x) {
              if (ext[x] != data[x]) {
                TestContext.Out.WriteLine($"  MISMATCH at byte {x}: expected 0x{data[x]:X2} got 0x{ext[x]:X2}");
                break;
              }
            }
          }
        } else {
          TestContext.Out.WriteLine("  7-Zip did not extract any file");
        }

      }
    }

    static string ToBin(uint code, int len) {
      var s = "";
      for (var i = len - 1; i >= 0; --i) s += ((code >> i) & 1) != 0 ? "1" : "0";
      return s;
    }

    static uint[] BuildCanonical(int[] lens, int n) {
      var maxLen = lens.Max();
      if (maxLen == 0) return new uint[n];
      var blCount = new int[maxLen + 1];
      foreach (var l in lens) if (l > 0) ++blCount[l];
      var nextCode = new uint[maxLen + 1];
      uint code = 0;
      for (var b = 1; b <= maxLen; ++b) { code = (code + (uint)blCount[b-1]) << 1; nextCode[b] = code; }
      var codes = new uint[n];
      for (var i = 0; i < n; ++i) if (lens[i] > 0) codes[i] = nextCode[lens[i]]++;
      return codes;
    }

    static int[] ReadCodeLens(Compression.Core.Dictionary.Rar.Rar5BitReader r,
        Compression.Core.Dictionary.Rar.Rar5HuffmanDecoder cl, int count) {
      var lens = new int[count]; var i = 0;
      while (i < count) {
        var sym = cl.DecodeSymbol(r);
        switch (sym) {
          case < 16: lens[i++] = sym; break;
          case 16: { var rep = (int)r.ReadBits(3) + 3; var prev = i > 0 ? lens[i-1] : 0;
            for (var j = 0; j < rep && i < count; ++j) lens[i++] = prev; break; }
          case 17: { var rep = (int)r.ReadBits(7) + 11; var prev = i > 0 ? lens[i-1] : 0;
            for (var j = 0; j < rep && i < count; ++j) lens[i++] = prev; break; }
          case 18: { var rep = (int)r.ReadBits(3) + 3;
            for (var j = 0; j < rep && i < count; ++j) lens[i++] = 0; break; }
          case 19: { var rep = (int)r.ReadBits(7) + 11;
            for (var j = 0; j < rep && i < count; ++j) lens[i++] = 0; break; }
        }
      }
      return lens;
    }
  }

  [Test]
  public void Rar_Diagnostic_BitstreamParse() {
    Require7z();
    // Parse the raw compressed bitstream for passing vs failing cases to find the divergence
    var num10 = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 10).Select(i => $"Line {i}: The quick brown fox jumps over the lazy dog.\n")));
    var num11 = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 11).Select(i => $"Line {i}: The quick brown fox jumps over the lazy dog.\n")));
    // Data with only short matches (distance <= 4, no low-offset table needed)
    var shortRep = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcd", 200)));
    // Data with distance exactly at low-offset boundary (distance 33-64, slot 10-11)
    var midDist = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "abcdefghijklmnopqrstuvwxyz0123456789 abcdefghijklmnopqrstuvwxyz0123456789 ", 10)));

    // Create data with multiple different distances in the low-offset range (33-64)
    var multiDist = new byte[600];
    // Fill with a pattern, then insert specific repeats at different distances
    var rng = new Random(42);
    rng.NextBytes(multiDist);
    // Place "XY" at various offsets to create matches at different distances
    multiDist[0] = (byte)'A'; multiDist[1] = (byte)'B';
    multiDist[35] = (byte)'A'; multiDist[36] = (byte)'B'; // dist 35
    multiDist[80] = (byte)'A'; multiDist[81] = (byte)'B'; // dist 45 from pos 35
    multiDist[140] = (byte)'A'; multiDist[141] = (byte)'B'; // dist 60 from pos 80
    multiDist[200] = (byte)'C'; multiDist[201] = (byte)'D';
    multiDist[250] = (byte)'C'; multiDist[251] = (byte)'D'; // dist 50

    // Data with exactly 3 different distance slots using low-offset table
    var threeDist = Encoding.ASCII.GetBytes(
      new string('A', 40) + "XY" + new string('B', 38) + "XY" + // dist 40
      new string('C', 48) + "XY" + // dist 50 from prev XY
      new string('D', 55) + "XY"   // dist 57 from prev XY
    );

    // Simple data that forces 4 offset symbols
    var fourSlots = Encoding.ASCII.GetBytes(
      "ab" + new string('x', 31) + "ab" +  // dist 33 (slot 10, extraBits=4)
      new string('y', 15) + "cd" + new string('z', 47) + "cd" +  // dist 49 (slot 11)
      new string('w', 10) + "ef" + new string('v', 33) + "ef" +  // dist 35 (slot 10)
      new string('u', 5) + "gh" + new string('t', 57) + "gh"     // dist 59 (slot 11)
    );

    var cases = new List<(string name, byte[] data)>();
    cases.Add(("pre535", num11[..535]));
    cases.Add(("pre536", num11[..536]));
    cases.Add(("num11", num11));

    foreach (var (name, data) in cases) {
      var encoder = new Compression.Core.Dictionary.Rar.Rar5Encoder();
      var compressed = encoder.Compress(data);
      TestContext.Out.WriteLine($"\n=== {name}: {data.Length} bytes → {compressed.Length} compressed ===");
      TestContext.Out.WriteLine($"First 32 bytes: {BitConverter.ToString(compressed, 0, Math.Min(32, compressed.Length))}");

      // Parse block header
      var pos = 0;
      var flags = compressed[pos++];
      var checksum = compressed[pos++];
      var byteCount = ((flags >> 3) & 3) + 1;
      var blockSize = 0;
      for (var b = 0; b < byteCount; ++b)
        blockSize += compressed[pos++] << (b * 8);
      var paddingBits = 7 - (flags & 7);
      var tablePresent = (flags & 0x80) != 0;
      var lastBlock = (flags & 0x40) != 0;

      // Verify checksum
      var expectedCheck = (byte)(0x5A ^ flags);
      for (var b = 0; b < byteCount; ++b)
        expectedCheck ^= (byte)((blockSize >> (b * 8)) & 0xFF);

      TestContext.Out.WriteLine($"Flags=0x{flags:X2} Checksum=0x{checksum:X2} (expected=0x{expectedCheck:X2}, {(checksum == expectedCheck ? "OK" : "MISMATCH!")})");
      TestContext.Out.WriteLine($"BlockSize={blockSize} bytes, padding={paddingBits} bits, table={tablePresent}, last={lastBlock}");
      TestContext.Out.WriteLine($"Remaining data after header: {compressed.Length - pos} bytes");

      // Use a bit reader to parse the table section
      var reader = new Compression.Core.Dictionary.Rar.Rar5BitReader(compressed[pos..]);

      // Read pre-code lengths (20 values, 4 bits each, with value-15 escape)
      var preCodes = new int[20];
      for (var i = 0; i < 20;) {
        var len = (int)reader.ReadBits(4);
        if (len == 15) {
          var count = (int)reader.ReadBits(4);
          if (count != 0) {
            for (var j = 0; j < count + 2 && i < 20; ++j)
              preCodes[i++] = 0;
            continue;
          }
        }
        preCodes[i++] = len;
      }
      TestContext.Out.WriteLine($"Pre-code lengths: [{string.Join(",", preCodes)}]");

      // Build code-length decoder
      var clDecoder = new Compression.Core.Dictionary.Rar.Rar5HuffmanDecoder();
      clDecoder.Build(preCodes, 20);

      // Read main table (306)
      var mainLens = ReadLens(reader, clDecoder, 306);
      var mainUsed = mainLens.Count(x => x > 0);
      var mainMax = mainLens.Max();
      TestContext.Out.WriteLine($"Main table: {mainUsed} used symbols, max len={mainMax}");
      // Print non-zero entries
      for (var i = 0; i < 306; ++i) {
        if (mainLens[i] > 0) {
          var desc = i < 256 ? $"lit '{(char)i}' (0x{i:X2})" :
            i >= 262 ? $"match slot {i - 262}" :
            i == 256 ? "rep0" : i == 257 ? "rep1" : i == 258 ? "rep2" : i == 259 ? "rep3" :
            i == 260 ? "filter" : i == 261 ? "EOB" : $"sym{i}";
          TestContext.Out.WriteLine($"  main[{i}] = {mainLens[i]}  ({desc})");
        }
      }

      // Read offset table (64)
      var offsetLens = ReadLens(reader, clDecoder, 64);
      var offsetUsed = offsetLens.Count(x => x > 0);
      var offsetDetail = string.Join(", ", Enumerable.Range(0, 64).Where(j => offsetLens[j] > 0).Select(j => $"slot{j}={offsetLens[j]}"));
      TestContext.Out.WriteLine($"Offset table: {offsetUsed} used symbols: {offsetDetail}");

      // Read low-offset table (16)
      var lowOffsetLens = ReadLens(reader, clDecoder, 16);
      TestContext.Out.WriteLine($"LowOffset table: [{string.Join(",", lowOffsetLens)}]");

      // Read length table (44)
      var lengthLens = ReadLens(reader, clDecoder, 44);
      var lengthUsed = lengthLens.Count(x => x > 0);
      TestContext.Out.WriteLine($"Length table: {lengthUsed} used symbols");

      TestContext.Out.WriteLine($"Bits consumed for tables: {reader.BytePosition * 8} (approx)");

      // Now verify roundtrip
      var decoder = new Compression.Core.Dictionary.Rar.Rar5Decoder(128 * 1024);
      var decoded = decoder.Decompress(compressed, data.Length);
      TestContext.Out.WriteLine($"Our roundtrip: {decoded.SequenceEqual(data)}");

      // Test with 7z - extract to get actual bytes
      var rarPath = Path.Combine(this._tmpDir, $"parse_{name}.rar");
      using (var fs = File.Create(rarPath)) {
        using var w = new FileFormat.Rar.RarWriter(fs, method: 3, solid: false);
        w.AddFile("data.txt", data);
        w.Finish();
      }
      // First test
      var psi = new ProcessStartInfo {
        FileName = SevenZipPath,
        Arguments = $"t \"{rarPath}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      using var proc = Process.Start(psi)!;
      var stdout = proc.StandardOutput.ReadToEnd();
      var stderr = proc.StandardError.ReadToEnd();
      proc.WaitForExit(10000);
      var exitCode = proc.ExitCode;
      TestContext.Out.WriteLine($"7-Zip test: exit={exitCode}");
      if (exitCode != 0) {
        TestContext.Out.WriteLine($"  stdout: {stdout.Trim().Replace("\r\n", " | ")}");
        TestContext.Out.WriteLine($"  stderr: {stderr.Trim().Replace("\r\n", " | ")}");
        // Also show our CRC
        var crc = Compression.Core.Checksums.Crc32.Compute(data);
        TestContext.Out.WriteLine($"  Our CRC32: 0x{crc:X8}");
      }

      if (exitCode != 0) {
        // Verify CRC by reading the RAR file and finding the data CRC field
        var rarBytes = File.ReadAllBytes(rarPath);
        var ourCrc = Compression.Core.Checksums.Crc32.Compute(data);
        // Also compute using System.IO.Hashing if available
        TestContext.Out.WriteLine($"  Our CRC32: 0x{ourCrc:X8}");
        // Find the data CRC in the RAR file
        var crcBytes = BitConverter.GetBytes(ourCrc);
        var crcFound = false;
        for (var si = 0; si < rarBytes.Length - 4; ++si) {
          if (rarBytes[si] == crcBytes[0] && rarBytes[si+1] == crcBytes[1] &&
              rarBytes[si+2] == crcBytes[2] && rarBytes[si+3] == crcBytes[3]) {
            TestContext.Out.WriteLine($"  CRC found at offset {si} (bytes: {BitConverter.ToString(rarBytes, si, 4)})");
            crcFound = true;
          }
        }
        if (!crcFound)
          TestContext.Out.WriteLine("  CRC NOT FOUND in RAR file!");
        // Try extracting to see what 7-Zip produces
        var extractDir = Path.Combine(this._tmpDir, $"extract_{name}");
        Directory.CreateDirectory(extractDir);
        var epsi = new ProcessStartInfo {
          FileName = SevenZipPath,
          Arguments = $"x -o\"{extractDir}\" -y \"{rarPath}\"",
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true,
        };
        using var eproc = Process.Start(epsi)!;
        eproc.StandardOutput.ReadToEnd();
        eproc.StandardError.ReadToEnd();
        eproc.WaitForExit(10000);
        var extractedFile = Path.Combine(extractDir, "data.txt");
        if (File.Exists(extractedFile)) {
          var extracted = File.ReadAllBytes(extractedFile);
          TestContext.Out.WriteLine($"7-Zip extracted: {extracted.Length} bytes (expected {data.Length})");
          // Find first mismatch
          var minLen = Math.Min(extracted.Length, data.Length);
          for (var x = 0; x < minLen; ++x) {
            if (extracted[x] != data[x]) {
              TestContext.Out.WriteLine($"MISMATCH at byte {x}: expected 0x{data[x]:X2} '{(char)data[x]}', got 0x{extracted[x]:X2} '{(char)extracted[x]}'");
              TestContext.Out.WriteLine($"  Context: expected[{x-2}..{x+10}] = {Encoding.ASCII.GetString(data, Math.Max(0,x-2), Math.Min(12, data.Length-Math.Max(0,x-2)))}");
              TestContext.Out.WriteLine($"  Context: got[{x-2}..{x+10}] = {Encoding.ASCII.GetString(extracted, Math.Max(0,x-2), Math.Min(12, extracted.Length-Math.Max(0,x-2)))}");
              break;
            }
          }
          if (minLen < Math.Max(extracted.Length, data.Length))
            TestContext.Out.WriteLine($"Size mismatch: {extracted.Length} vs {data.Length}");
        } else {
          TestContext.Out.WriteLine("7-Zip did not extract any file");
        }
      }
    }

    static int[] ReadLens(Compression.Core.Dictionary.Rar.Rar5BitReader reader,
        Compression.Core.Dictionary.Rar.Rar5HuffmanDecoder clDecoder, int count) {
      var lengths = new int[count];
      var i = 0;
      while (i < count) {
        var sym = clDecoder.DecodeSymbol(reader);
        switch (sym) {
          case < 16:
            lengths[i++] = sym;
            break;
          case 16: {
            if (i == 0) throw new Exception("repeat at start");
            var repeat = (int)reader.ReadBits(3) + 3;
            var prev = lengths[i - 1];
            for (var j = 0; j < repeat && i < count; ++j) lengths[i++] = prev;
            break;
          }
          case 17: {
            if (i == 0) throw new Exception("repeat at start");
            var repeat = (int)reader.ReadBits(7) + 11;
            var prev = lengths[i - 1];
            for (var j = 0; j < repeat && i < count; ++j) lengths[i++] = prev;
            break;
          }
          case 18: {
            var repeat = (int)reader.ReadBits(3) + 3;
            for (var j = 0; j < repeat && i < count; ++j) lengths[i++] = 0;
            break;
          }
          case 19: {
            var repeat = (int)reader.ReadBits(7) + 11;
            for (var j = 0; j < repeat && i < count; ++j) lengths[i++] = 0;
            break;
          }
        }
      }
      return lengths;
    }
  }

  [Test]
  public void Rar_Diagnostic_CompressedBitstream() {
    Require7z();
    // Systematically test different distance ranges to find the exact trigger
    var results = new List<string>();

    // Helper: create data with a match at exact distance.
    // Uses 3-byte markers to ensure the hash chain finds the match.
    // Filler uses 0xFF bytes to avoid hash collisions with markers.
    byte[] MakeDistData(int dist) {
      var data = new byte[dist + 4]; // 3 marker + filler + 3 marker + 1 tail
      Array.Fill(data, (byte)0xFF);
      data[0] = 0x01; data[1] = 0x02; data[2] = 0x03;
      data[dist] = 0x01; data[dist + 1] = 0x02; data[dist + 2] = 0x03;
      data[dist + 3] = 0xFE; // tail byte
      return data;
    }

    // Helper: create data with TWO matches at exact distances.
    byte[] Make2Dist(int dist1, int dist2) {
      var size = dist1 + dist2 + 10;
      var data = new byte[size];
      Array.Fill(data, (byte)0xFF);
      // First pair at distance dist1
      data[0] = 0x01; data[1] = 0x02; data[2] = 0x03;
      data[dist1] = 0x01; data[dist1 + 1] = 0x02; data[dist1 + 2] = 0x03;
      // Second pair at distance dist2
      var p2 = dist1 + 4;
      data[p2] = 0x04; data[p2 + 1] = 0x05; data[p2 + 2] = 0x06;
      if (p2 + dist2 + 2 < size) {
        data[p2 + dist2] = 0x04; data[p2 + dist2 + 1] = 0x05; data[p2 + dist2 + 2] = 0x06;
      }
      return data;
    }

    // Helper: create data with THREE matches at exact distances.
    byte[] Make3Dist(int dist1, int dist2, int dist3) {
      var size = dist1 + dist2 + dist3 + 20;
      var data = new byte[size];
      Array.Fill(data, (byte)0xFF);
      var pos = 0;
      data[pos] = 0x01; data[pos + 1] = 0x02; data[pos + 2] = 0x03;
      pos += dist1;
      data[pos] = 0x01; data[pos + 1] = 0x02; data[pos + 2] = 0x03;
      pos += 4;
      data[pos] = 0x04; data[pos + 1] = 0x05; data[pos + 2] = 0x06;
      pos += dist2;
      if (pos + 2 < size) {
        data[pos] = 0x04; data[pos + 1] = 0x05; data[pos + 2] = 0x06;
      }
      pos += 4;
      if (pos + 2 + dist3 + 2 < size) {
        data[pos] = 0x07; data[pos + 1] = 0x08; data[pos + 2] = 0x09;
        data[pos + dist3] = 0x07; data[pos + dist3 + 1] = 0x08; data[pos + dist3 + 2] = 0x09;
      }
      return data;
    }

    var cases = new List<(string name, byte[] data)> {
      // Basic: short distances (no low-offset table, slots < 10)
      ("dist5", MakeDistData(5)),
      ("dist16", MakeDistData(16)),
      ("dist32", MakeDistData(32)),

      // Distances using low-offset table (slot 10+, extraBits >= 4)
      ("dist33", MakeDistData(33)),   // slot 10, eb=4
      ("dist49", MakeDistData(49)),   // slot 11, eb=4
      ("dist65", MakeDistData(65)),   // slot 12, eb=5
      ("dist97", MakeDistData(97)),   // slot 13, eb=5
      ("dist129", MakeDistData(129)), // slot 14, eb=6
      ("dist193", MakeDistData(193)), // slot 15, eb=6
      ("dist257", MakeDistData(257)), // slot 16, eb=7
      ("dist385", MakeDistData(385)), // slot 17, eb=7  ← pre536 introduces this
      ("dist513", MakeDistData(513)), // slot 18, eb=8  ← num11 introduces this

      // Single distance but with 0xFF filler (tests low-offset with 1 slot)
      ("dist400", MakeDistData(400)), // slot 17
      ("dist500", MakeDistData(500)), // slot 18

      // Two distances crossing the threshold
      ("2d_33_385", Make2Dist(33, 385)),  // slots 10,17
      ("2d_49_385", Make2Dist(49, 385)),  // slots 11,17
      ("2d_33_513", Make2Dist(33, 513)),  // slots 10,18

      // Three distances including slot 17
      ("3d_5_33_385", Make3Dist(5, 33, 385)),   // slots ~5,10,17
      ("3d_33_49_385", Make3Dist(33, 49, 385)),  // slots 10,11,17

      // Exact boundary around slot 15/16
      ("dist192", MakeDistData(192)),  // slot 15 (last before 16)
      ("dist193", MakeDistData(193)),  // slot 15
      ("dist194", MakeDistData(194)),  // slot 15
      ("dist255", MakeDistData(255)),  // slot 15
      ("dist256", MakeDistData(256)),  // slot 15 or 16?
      ("dist257", MakeDistData(257)),  // slot 16
      ("dist258", MakeDistData(258)),  // slot 16

      // Original failing cases
      ("pre536", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 11).Select(i => $"Line {i}: The quick brown fox jumps over the lazy dog.\n")))[..536]),
      ("num11", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 11).Select(i => $"Line {i}: The quick brown fox jumps over the lazy dog.\n")))),
    };

    foreach (var (name, data) in cases) {
      var rarPath = Path.Combine(this._tmpDir, $"diag_{name}.rar");
      using (var fs = File.Create(rarPath)) {
        using var w = new FileFormat.Rar.RarWriter(fs, method: 3, solid: false);
        w.AddFile("data.txt", data);
        w.Finish();
      }

      // Verify our own roundtrip first
      var encoder = new Compression.Core.Dictionary.Rar.Rar5Encoder();
      var compressed = encoder.Compress(data);
      var decoder = new Compression.Core.Dictionary.Rar.Rar5Decoder(128 * 1024);
      var decoded = decoder.Decompress(compressed, data.Length);
      var ourOk = decoded.SequenceEqual(data);

      // Count offset slots used
      var offsetSlots = new HashSet<int>();
      var lowOffsetSyms = new HashSet<int>();
      // Re-tokenize to count
      {
        var mf = new Compression.Core.Dictionary.MatchFinders.HashChainMatchFinder(128 * 1024);
        var repD = new int[4];
        var pos = 0;
        while (pos < data.Length) {
          var m = mf.FindMatch(data, pos, 128 * 1024, 0x101 + 8, 2);
          if (m.Length >= 2) {
            var len = Math.Min(m.Length, 2);
            var d0 = m.Distance - 1;
            int slot;
            if (d0 < 4) slot = d0;
            else { var p = 31 - int.LeadingZeroCount(d0); slot = 2 * p + ((d0 >> (p - 1)) & 1); }
            offsetSlots.Add(slot);
            var eb = slot < 4 ? 0 : (slot - 2) >> 1;
            if (eb >= 4) {
              var bd = slot < 4 ? slot : (2 + (slot & 1)) << eb;
              lowOffsetSyms.Add((d0 - bd) & 0xF);
            }
            for (var i = 1; i < len && pos + i < data.Length; ++i) mf.InsertPosition(data, pos + i);
            pos += len;
          } else {
            pos++;
          }
        }
      }

      // Test with 7z
      var psi = new ProcessStartInfo {
        FileName = SevenZipPath,
        Arguments = $"t \"{rarPath}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      using var proc = Process.Start(psi)!;
      proc.StandardOutput.ReadToEnd();
      proc.StandardError.ReadToEnd();
      proc.WaitForExit(10000);
      var sevenZipOk = proc.ExitCode == 0;

      var slotStr = string.Join(",", offsetSlots.OrderBy(x => x));
      var lowStr = lowOffsetSyms.Count > 0 ? string.Join(",", lowOffsetSyms.OrderBy(x => x)) : "none";
      var result = $"{name}: size={data.Length} offSlots=[{slotStr}] lowOff=[{lowStr}] ours={ourOk} 7z={sevenZipOk}";
      results.Add(result);
      TestContext.Out.WriteLine(result);
    }

    TestContext.Out.WriteLine("\n=== SUMMARY ===");
    foreach (var r in results)
      TestContext.Out.WriteLine(r);

    var allPass = results.All(r => r.Contains("7z=True"));
    if (!allPass) {
      TestContext.Out.WriteLine("\n=== FAILURES ===");
      foreach (var r in results.Where(r => r.Contains("7z=False")))
        TestContext.Out.WriteLine(r);
    }
  }

  [Test]
  public void Rar_Diagnostic_ExtractAndCompare() {
    Require7z();
    // Create a minimal failing case and extract with 7-Zip to compare output
    var data = new byte[261]; // ff_dist_257
    Array.Fill(data, (byte)0xFF);
    data[0] = 0x01; data[1] = 0x02; data[2] = 0x03;
    data[257] = 0x01; data[258] = 0x02; data[259] = 0x03;
    data[260] = 0xFE;

    // Also create a passing case for comparison
    var dataPass = new byte[260]; // ff_dist_256
    Array.Fill(dataPass, (byte)0xFF);
    dataPass[0] = 0x01; dataPass[1] = 0x02; dataPass[2] = 0x03;
    dataPass[256] = 0x01; dataPass[257] = 0x02; dataPass[258] = 0x03;
    dataPass[259] = 0xFE;

    foreach (var (label, original) in new[] { ("pass_d256", dataPass), ("fail_d257", data) }) {
      // Compress with our encoder and dump raw compressed bytes
      var encoder = new Compression.Core.Dictionary.Rar.Rar5Encoder();
      var compressed = encoder.Compress(original);
      TestContext.Out.WriteLine($"\n=== {label}: original={original.Length}b, compressed={compressed.Length}b ===");
      TestContext.Out.WriteLine($"Compressed hex ({compressed.Length} bytes):");
      for (var i = 0; i < compressed.Length; i += 16) {
        var line = string.Join(" ", Enumerable.Range(i, Math.Min(16, compressed.Length - i))
          .Select(j => compressed[j].ToString("X2")));
        TestContext.Out.WriteLine($"  {i:X4}: {line}");
      }

      // Parse block header manually
      var br = new Compression.Core.Dictionary.Rar.Rar5BitReader(compressed);
      // Align (should be at 0)
      var flags = (int)br.ReadBits(8);
      var checksum = (int)br.ReadBits(8);
      var byteCount = ((flags >> 3) & 3) + 1;
      var blockSize = 0;
      for (var b = 0; b < byteCount; b++)
        blockSize += (int)br.ReadBits(8) << (b * 8);
      var blockBitSize7 = (flags & 7) + 1;
      var paddingBits = 8 - blockBitSize7;
      if (paddingBits == 8) paddingBits = 0;
      var totalBlockBits = blockSize * 8 - paddingBits;
      var computedChecksum = 0x5A ^ flags;
      for (var b = 0; b < byteCount; b++)
        computedChecksum ^= (blockSize >> (b * 8)) & 0xFF;
      computedChecksum &= 0xFF;

      TestContext.Out.WriteLine($"Block header: flags=0x{flags:X2} checksum=0x{checksum:X2} (computed=0x{computedChecksum:X2}) byteCount={byteCount} blockSize={blockSize}");
      TestContext.Out.WriteLine($"  blockBitSize7={blockBitSize7} paddingBits={paddingBits} totalBlockBits={totalBlockBits}");
      TestContext.Out.WriteLine($"  tablePresent={(flags & 0x80) != 0} lastBlock={(flags & 0x40) != 0}");

      // 7-Zip interpretation
      var sz_blockBitSize7 = (flags & 7) + 1;
      var sz_blockSize = blockSize;
      sz_blockSize += sz_blockBitSize7 >> 3;
      sz_blockSize--;
      sz_blockBitSize7 &= 7;
      TestContext.Out.WriteLine($"  7-Zip view: blockSize={sz_blockSize} blockBitSize7={sz_blockBitSize7} totalBits={sz_blockSize * 8 + sz_blockBitSize7}");

      // Write RAR file
      var rarPath = Path.Combine(this._tmpDir, $"{label}.rar");
      using (var fs = File.Create(rarPath)) {
        using var w = new FileFormat.Rar.RarWriter(fs, method: 3, solid: false);
        w.AddFile("data.txt", original);
        w.Finish();
      }

      // Also dump the RAR file bytes (just the compressed payload area)
      var rarBytes = File.ReadAllBytes(rarPath);
      TestContext.Out.WriteLine($"RAR file: {rarBytes.Length} bytes");

      // Extract with 7-Zip
      var extractDir = Path.Combine(this._tmpDir, $"extract_{label}");
      Directory.CreateDirectory(extractDir);
      var psi = new ProcessStartInfo {
        FileName = SevenZipPath,
        Arguments = $"e \"{rarPath}\" -o\"{extractDir}\" -y",
        RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true,
      };
      using var proc = Process.Start(psi)!;
      var stdout = proc.StandardOutput.ReadToEnd();
      var stderr = proc.StandardError.ReadToEnd();
      proc.WaitForExit(10000);
      TestContext.Out.WriteLine($"7-Zip exit={proc.ExitCode}");
      if (!string.IsNullOrEmpty(stderr)) TestContext.Out.WriteLine($"7-Zip stderr: {stderr}");
      if (!string.IsNullOrEmpty(stdout)) TestContext.Out.WriteLine($"7-Zip stdout: {stdout}");

      // Check extracted file (7-Zip)
      var extractedPath = Path.Combine(extractDir, "data.txt");
      if (File.Exists(extractedPath)) {
        var extracted = File.ReadAllBytes(extractedPath);
        TestContext.Out.WriteLine($"7-Zip extracted: {extracted.Length} bytes");
        if (extracted.SequenceEqual(original)) {
          TestContext.Out.WriteLine("7-Zip: MATCH");
        } else {
          var diffIdx = Enumerable.Range(0, Math.Min(extracted.Length, original.Length))
            .FirstOrDefault(i => extracted[i] != original[i], -1);
          TestContext.Out.WriteLine($"7-Zip: MISMATCH at byte {diffIdx} (expected 0x{original[diffIdx]:X2}, got 0x{extracted[diffIdx]:X2})");
        }
      } else {
        TestContext.Out.WriteLine("7-Zip: No extracted file");
      }

    }
  }
}
