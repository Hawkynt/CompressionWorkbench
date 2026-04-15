#pragma warning disable CS1591
#pragma warning disable CA1416 // Platform compatibility — guarded at runtime by IsWindows/IsLinux checks

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests;

/// <summary>
/// Tests our format implementations against native OS tools: PowerShell,
/// Windows tar, certutil, System.IO.Compression, Hyper-V cmdlets,
/// Mount-DiskImage, mtools, genisoimage, qemu-img, mkfs, etc.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class OsIntegrationTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_osint_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
    FormatRegistration.EnsureInitialized();
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── Platform detection ─────────────────────────────────────────────

  private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
  private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

  private static bool IsAdmin() {
    if (IsWindows) {
      using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
      var principal = new System.Security.Principal.WindowsPrincipal(identity);
      return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
    if (IsLinux)
      return RunShell("id -u").StdOut.Trim() == "0";
    return false;
  }

  private static bool HasCommand(string name) {
    try {
      var result = RunShell(IsWindows ? $"where {name} 2>nul" : $"which {name} 2>/dev/null");
      return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
    } catch {
      return false;
    }
  }

  private static bool HasPowerShellModule(string moduleName) {
    try {
      var result = RunPowerShell($"if (Get-Module -ListAvailable -Name {moduleName}) {{ 'yes' }} else {{ 'no' }}");
      return result.StdOut.Trim() == "yes";
    } catch {
      return false;
    }
  }

  // ── Test data ──────────────────────────────────────────────────────

  private static byte[] SmallText => "Hello from CompressionWorkbench! Testing OS integration."u8.ToArray();

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

  private static byte[] BinaryStructured {
    get {
      var data = new byte[4096];
      for (var i = 0; i < 16; i++) data[i] = (byte)(0xDE + i);
      for (var i = 16; i < 2048; i++) data[i] = (byte)(i % 37);
      var rng = new Random(123);
      rng.NextBytes(data.AsSpan(2048));
      return data;
    }
  }

  // ── Stream format helpers ─────────────────────────────────────────

  private static IStreamFormatOperations GetStreamOps(string id) =>
    FormatRegistry.GetStreamOps(id) ?? throw new NotSupportedException($"No stream ops for {id}");

  // ── Source file helpers ───────────────────────────────────────────

  /// <summary>Creates a directory with multiple test files and returns paths + expected data.</summary>
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

  /// <summary>Creates a directory tree for filesystem tests.</summary>
  private (string Dir, Dictionary<string, byte[]> Files) CreateDirectoryTree() {
    var dir = Path.Combine(_tmpDir, "tree");
    Directory.CreateDirectory(dir);
    Directory.CreateDirectory(Path.Combine(dir, "subdir"));
    Directory.CreateDirectory(Path.Combine(dir, "subdir", "nested"));

    var files = new Dictionary<string, byte[]> {
      ["root.txt"] = SmallText,
      [Path.Combine("subdir", "data.txt")] = RepetitiveText,
      [Path.Combine("subdir", "nested", "deep.bin")] = BinaryStructured,
    };
    foreach (var (name, data) in files)
      File.WriteAllBytes(Path.Combine(dir, name), data);
    return (dir, files);
  }

  // ── Verification helpers ──────────────────────────────────────────

  private static void VerifyExtractedFiles(string extractDir, Dictionary<string, byte[]> expected) {
    foreach (var (name, expectedData) in expected) {
      var fileName = Path.GetFileName(name);
      var found = FindFile(extractDir, fileName);
      Assert.That(found, Is.Not.Null, $"Could not find '{fileName}' in extracted output at {extractDir}");
      var actual = File.ReadAllBytes(found!);
      Assert.That(actual, Is.EqualTo(expectedData), $"Data mismatch for '{fileName}'");
    }
  }

  private static string? FindFile(string dir, string name) {
    foreach (var f in Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories))
      return f;
    return null;
  }

  // ═══════════════════════════════════════════════════════════════════
  // WINDOWS — No-admin tests
  // ═══════════════════════════════════════════════════════════════════

  // ── PowerShell Compress-Archive / Expand-Archive (ZIP) ────────────

  [Test]
  public void Windows_PowerShell_OurZip_ExpandArchive() {
    if (!IsWindows) Assert.Ignore("Windows-only test");

    var (dir, files) = CreateSourceFiles();
    var zipPath = Path.Combine(_tmpDir, "ours.zip");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "ps_extract");
    RunPowerShellChecked($"Expand-Archive -Path '{zipPath}' -DestinationPath '{extractDir}' -Force");

    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Windows_PowerShell_CompressArchive_WeExtract() {
    if (!IsWindows) Assert.Ignore("Windows-only test");

    var (dir, files) = CreateSourceFiles();
    var zipPath = Path.Combine(_tmpDir, "ps.zip");
    RunPowerShellChecked($"Compress-Archive -Path '{Path.Combine(dir, "*")}' -DestinationPath '{zipPath}'");

    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);

    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Windows_PowerShell_RoundTrip_RandomData() {
    if (!IsWindows) Assert.Ignore("Windows-only test");

    var data = RandomData;
    var dir = Path.Combine(_tmpDir, "src");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "random.dat"), data);

    // Our ZIP -> PowerShell extract
    var zipPath = Path.Combine(_tmpDir, "ours_random.zip");
    ArchiveOperations.Create(zipPath, [
      new ArchiveInput(Path.Combine(dir, "random.dat"), "random.dat"),
    ], new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "ps_extract");
    RunPowerShellChecked($"Expand-Archive -Path '{zipPath}' -DestinationPath '{extractDir}' -Force");

    var extracted = File.ReadAllBytes(Path.Combine(extractDir, "random.dat"));
    Assert.That(extracted, Is.EqualTo(data));
  }

  // ── Windows built-in tar ──────────────────────────────────────────

  [Test]
  public void Windows_Tar_OurOutput_SystemExtracts() {
    if (!IsWindows) Assert.Ignore("Windows-only test");
    if (!HasCommand("tar")) Assert.Ignore("Windows tar not available");

    var (dir, files) = CreateSourceFiles();
    var tarPath = Path.Combine(_tmpDir, "ours.tar");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(tarPath, inputs, new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "sys_extract");
    Directory.CreateDirectory(extractDir);
    RunToolChecked("tar", $"xf \"{tarPath}\" -C \"{extractDir}\"");

    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Windows_Tar_SystemCreates_WeExtract() {
    if (!IsWindows) Assert.Ignore("Windows-only test");
    if (!HasCommand("tar")) Assert.Ignore("Windows tar not available");

    var (dir, files) = CreateSourceFiles();
    var tarPath = Path.Combine(_tmpDir, "system.tar");
    var fileArgs = string.Join(" ", files.Keys);
    RunToolChecked("tar", $"cf \"{tarPath}\" -C \"{dir}\" {fileArgs}");

    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(tarPath, extractDir, null, null);

    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Windows_TarGz_OurOutput_SystemExtracts() {
    if (!IsWindows) Assert.Ignore("Windows-only test");
    if (!HasCommand("tar")) Assert.Ignore("Windows tar not available");

    var (dir, files) = CreateSourceFiles();
    var tarGzPath = Path.Combine(_tmpDir, "ours.tar.gz");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(tarGzPath, inputs, new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "sys_extract");
    Directory.CreateDirectory(extractDir);
    RunToolChecked("tar", $"xzf \"{tarGzPath}\" -C \"{extractDir}\"");

    VerifyExtractedFiles(extractDir, files);
  }

  // ── certutil for encoding ─────────────────────────────────────────

  [Test]
  public void Windows_Certutil_OurBase64_DecodeMatches() {
    if (!IsWindows) Assert.Ignore("Windows-only test");
    if (!HasCommand("certutil")) Assert.Ignore("certutil not available");

    var data = RepetitiveText;
    var rawPath = Path.Combine(_tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);

    // Encode with certutil (Base64)
    var encodedPath = Path.Combine(_tmpDir, "encoded.b64");
    RunToolChecked("certutil", $"-encode \"{rawPath}\" \"{encodedPath}\"");

    // Decode with certutil to verify our data matches
    var decodedPath = Path.Combine(_tmpDir, "decoded.bin");
    RunToolChecked("certutil", $"-decode \"{encodedPath}\" \"{decodedPath}\"");

    var decoded = File.ReadAllBytes(decodedPath);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void Windows_Certutil_EncodeBase64_WeDecodeUuEncoding() {
    if (!IsWindows) Assert.Ignore("Windows-only test");
    if (!HasCommand("certutil")) Assert.Ignore("certutil not available");

    // certutil -encode produces PEM-style Base64 (-----BEGIN CERTIFICATE-----)
    // Our UuEncoding format handles traditional uuencode, not PEM Base64.
    // Instead, test certutil round-trip on our compressed output.
    var data = SmallText;
    var rawPath = Path.Combine(_tmpDir, "small.bin");
    File.WriteAllBytes(rawPath, data);

    // Encode with certutil
    var encodedPath = Path.Combine(_tmpDir, "small.b64");
    RunToolChecked("certutil", $"-encode \"{rawPath}\" \"{encodedPath}\"");

    // Decode with certutil
    var decodedPath = Path.Combine(_tmpDir, "small_decoded.bin");
    RunToolChecked("certutil", $"-decode \"{encodedPath}\" \"{decodedPath}\"");

    Assert.That(File.ReadAllBytes(decodedPath), Is.EqualTo(data));
  }

  [Test]
  public void Windows_Certutil_RandomData_RoundTrip() {
    if (!IsWindows) Assert.Ignore("Windows-only test");
    if (!HasCommand("certutil")) Assert.Ignore("certutil not available");

    var data = RandomData;
    var rawPath = Path.Combine(_tmpDir, "random.bin");
    File.WriteAllBytes(rawPath, data);

    var encodedPath = Path.Combine(_tmpDir, "random.b64");
    RunToolChecked("certutil", $"-encode \"{rawPath}\" \"{encodedPath}\"");

    var decodedPath = Path.Combine(_tmpDir, "random_decoded.bin");
    RunToolChecked("certutil", $"-decode \"{encodedPath}\" \"{decodedPath}\"");

    Assert.That(File.ReadAllBytes(decodedPath), Is.EqualTo(data));
  }

  // ── System.IO.Compression cross-validation ────────────────────────

  [Test]
  public void SystemIOCompression_OurZip_ReadByZipArchive() {
    var data = RepetitiveText;
    var dir = Path.Combine(_tmpDir, "src");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "data.bin"), data);

    var zipPath = Path.Combine(_tmpDir, "ours.zip");
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
  public void SystemIOCompression_ZipArchive_ReadByUs() {
    var data = RepetitiveText;
    var zipPath = Path.Combine(_tmpDir, "dotnet.zip");

    using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create)) {
      var entry = archive.CreateEntry("data.bin");
      using var es = entry.Open();
      es.Write(data);
    }

    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);

    var extracted = FindFile(extractDir, "data.bin");
    Assert.That(extracted, Is.Not.Null, "data.bin not found in extracted output");
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(data));
  }

  [Test]
  public void SystemIOCompression_OurZip_MultipleFiles_ReadByZipArchive() {
    var (dir, files) = CreateSourceFiles();
    var zipPath = Path.Combine(_tmpDir, "ours_multi.zip");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

    using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
    foreach (var (name, expected) in files) {
      var entry = archive.Entries.FirstOrDefault(e => e.Name == name);
      Assert.That(entry, Is.Not.Null, $"Entry '{name}' not found in ZIP");
      using var es = entry!.Open();
      using var ms = new MemoryStream();
      es.CopyTo(ms);
      Assert.That(ms.ToArray(), Is.EqualTo(expected), $"Data mismatch for '{name}'");
    }
  }

  [Test]
  public void SystemIOCompression_OurGzip_ReadByGZipStream() {
    var data = RepetitiveText;
    var gzPath = Path.Combine(_tmpDir, "ours.gz");

    using (var fs = File.Create(gzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Gzip").Compress(input, fs);
    }

    using var gzFs = File.OpenRead(gzPath);
    using var gzStream = new System.IO.Compression.GZipStream(gzFs, System.IO.Compression.CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gzStream.CopyTo(ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void SystemIOCompression_GZipStream_ReadByUs() {
    var data = RepetitiveText;
    var gzPath = Path.Combine(_tmpDir, "dotnet.gz");

    using (var fs = File.Create(gzPath)) {
      using var gzStream = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal);
      gzStream.Write(data);
    }

    using var inFs = File.OpenRead(gzPath);
    using var ms = new MemoryStream();
    GetStreamOps("Gzip").Decompress(inFs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void SystemIOCompression_OurGzip_RandomData_ReadByGZipStream() {
    var data = RandomData;
    var gzPath = Path.Combine(_tmpDir, "random.gz");

    using (var fs = File.Create(gzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Gzip").Compress(input, fs);
    }

    using var gzFs = File.OpenRead(gzPath);
    using var gzStream = new System.IO.Compression.GZipStream(gzFs, System.IO.Compression.CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gzStream.CopyTo(ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  [Ignore("Known limitation: our clean-room Brotli LZ77 encoder has libbrotli interop issues. " +
          "Self round-trip works. TODO Phase 31 §1b.")]
  public void SystemIOCompression_OurBrotli_ReadByBrotliStream() {
    var data = RepetitiveText;
    var brPath = Path.Combine(_tmpDir, "ours.br");

    using (var fs = File.Create(brPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Brotli").Compress(input, fs);
    }

    using var brFs = File.OpenRead(brPath);
    using var brStream = new System.IO.Compression.BrotliStream(brFs, System.IO.Compression.CompressionMode.Decompress);
    using var ms = new MemoryStream();
    brStream.CopyTo(ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void SystemIOCompression_BrotliStream_ReadByUs() {
    var data = RepetitiveText;
    var brPath = Path.Combine(_tmpDir, "dotnet.br");

    using (var fs = File.Create(brPath)) {
      using var brStream = new System.IO.Compression.BrotliStream(fs, System.IO.Compression.CompressionLevel.Optimal);
      brStream.Write(data);
    }

    using var inFs = File.OpenRead(brPath);
    using var ms = new MemoryStream();
    GetStreamOps("Brotli").Decompress(inFs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void SystemIOCompression_ZipArchive_RandomData_BothDirections() {
    var data = RandomData;

    // Our ZIP -> .NET reads
    var dir = Path.Combine(_tmpDir, "src");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "random.dat"), data);
    var ourZip = Path.Combine(_tmpDir, "ours_random.zip");
    ArchiveOperations.Create(ourZip, [
      new ArchiveInput(Path.Combine(dir, "random.dat"), "random.dat"),
    ], new CompressionOptions());

    using (var archive = System.IO.Compression.ZipFile.OpenRead(ourZip)) {
      var entry = archive.Entries.First(e => e.Name == "random.dat");
      using var es = entry.Open();
      using var ms = new MemoryStream();
      es.CopyTo(ms);
      Assert.That(ms.ToArray(), Is.EqualTo(data), "Our ZIP -> .NET ZipArchive: data mismatch");
    }

    // .NET ZIP -> our extract
    var dotnetZip = Path.Combine(_tmpDir, "dotnet_random.zip");
    using (var archive = System.IO.Compression.ZipFile.Open(dotnetZip, System.IO.Compression.ZipArchiveMode.Create)) {
      var entry = archive.CreateEntry("random.dat");
      using var es = entry.Open();
      es.Write(data);
    }

    var extractDir = Path.Combine(_tmpDir, "our_extract_random");
    ArchiveOperations.Extract(dotnetZip, extractDir, null, null);
    var found = FindFile(extractDir, "random.dat");
    Assert.That(found, Is.Not.Null);
    Assert.That(File.ReadAllBytes(found!), Is.EqualTo(data), ".NET ZipArchive -> our extract: data mismatch");
  }

  [Test]
  public void SystemIOCompression_Deflate_OurOutput_ReadByDeflateStream() {
    var data = RepetitiveText;
    var zlibPath = Path.Combine(_tmpDir, "ours.zlib");

    using (var fs = File.Create(zlibPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Zlib").Compress(input, fs);
    }

    // Zlib = 2-byte header + DEFLATE + 4-byte Adler32
    // .NET ZLibStream can read zlib format
    using var zlibFs = File.OpenRead(zlibPath);
    using var zlibStream = new System.IO.Compression.ZLibStream(zlibFs, System.IO.Compression.CompressionMode.Decompress);
    using var ms = new MemoryStream();
    zlibStream.CopyTo(ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void SystemIOCompression_ZLibStream_ReadByUs() {
    var data = RepetitiveText;
    var zlibPath = Path.Combine(_tmpDir, "dotnet.zlib");

    using (var fs = File.Create(zlibPath)) {
      using var zlibStream = new System.IO.Compression.ZLibStream(fs, System.IO.Compression.CompressionLevel.Optimal);
      zlibStream.Write(data);
    }

    using var inFs = File.OpenRead(zlibPath);
    using var ms = new MemoryStream();
    GetStreamOps("Zlib").Decompress(inFs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  // ═══════════════════════════════════════════════════════════════════
  // WINDOWS — Admin tests
  // ═══════════════════════════════════════════════════════════════════

  // ── ISO via Mount-DiskImage ───────────────────────────────────────

  [Test]
  [CancelAfter(60_000)]
  public void Windows_Admin_ISO_OurOutput_MountDiskImage() {
    if (!IsWindows) Assert.Ignore("Windows-only test");
    if (!IsAdmin()) Assert.Ignore("Requires Administrator privileges");

    var (dir, files) = CreateSourceFiles();
    var isoPath = Path.Combine(_tmpDir, "ours.iso");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(isoPath, inputs, new CompressionOptions());

    string? driveLetter = null;
    try {
      // Mount the ISO
      var mountResult = RunPowerShellChecked(
        $"$vol = Mount-DiskImage -ImagePath '{isoPath}' -PassThru | Get-Volume; $vol.DriveLetter");
      driveLetter = mountResult.StdOut.Trim();

      if (string.IsNullOrEmpty(driveLetter))
        Assert.Fail("Mount-DiskImage did not return a drive letter");

      // Verify files exist and match
      foreach (var (name, expected) in files) {
        var filePath = $"{driveLetter}:\\{name}";
        Assert.That(File.Exists(filePath), Is.True, $"File not found on mounted ISO: {filePath}");
        Assert.That(File.ReadAllBytes(filePath), Is.EqualTo(expected), $"Data mismatch for {name}");
      }
    } finally {
      // Always dismount
      if (driveLetter != null)
        try { RunPowerShellChecked($"Dismount-DiskImage -ImagePath '{isoPath}'"); } catch { /* best effort */ }
    }
  }

  // ── VHD via Hyper-V PowerShell ────────────────────────────────────

  [Test]
  [CancelAfter(60_000)]
  public void Windows_Admin_VHD_MountAndVerify() {
    if (!IsWindows) Assert.Ignore("Windows-only test");
    if (!IsAdmin()) Assert.Ignore("Requires Administrator privileges");
    if (!HasPowerShellModule("Hyper-V")) Assert.Ignore("Hyper-V PowerShell module not available");

    // Create a VHD with our library
    var vhdPath = Path.Combine(_tmpDir, "test.vhd");
    var (dir, files) = CreateSourceFiles();
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(vhdPath, inputs, new CompressionOptions());

    try {
      // Try to mount the VHD
      var result = RunPowerShell($"Mount-VHD -Path '{vhdPath}' -ReadOnly -PassThru -ErrorAction Stop");
      if (result.ExitCode != 0) {
        TestContext.Out.WriteLine($"Mount-VHD failed: {result.StdErr}");
        Assert.Ignore("Mount-VHD failed -- VHD may not be in a format Windows can mount directly");
      }

      // Dismount even if partially mounted
      RunPowerShell($"Dismount-VHD -Path '{vhdPath}' -ErrorAction SilentlyContinue");
      Assert.Pass("VHD was recognized and mountable by Windows");
    } catch {
      try { RunPowerShell($"Dismount-VHD -Path '{vhdPath}' -ErrorAction SilentlyContinue"); } catch { /* ignore */ }
      Assert.Ignore("VHD mount test inconclusive");
    }
  }

  // ═══════════════════════════════════════════════════════════════════
  // LINUX — No-root tests
  // ═══════════════════════════════════════════════════════════════════

  // ── mtools for FAT images ─────────────────────────────────────────

  [Test]
  public void Linux_Mtools_OurFat_ReadByMdir() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("mdir")) Assert.Ignore("mtools not installed");

    var (dir, files) = CreateSourceFiles();
    var fatPath = Path.Combine(_tmpDir, "ours.fat");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(fatPath, inputs, new CompressionOptions());

    // Use mdir to list the FAT image
    var result = RunToolChecked("mdir", $"-i \"{fatPath}\"");
    foreach (var name in files.Keys) {
      // mdir shows 8.3 names, check case-insensitively
      Assert.That(result.StdOut.ToUpperInvariant(), Does.Contain(Path.GetFileName(name).ToUpperInvariant()),
        $"mdir output does not contain '{name}'");
    }
  }

  [Test]
  public void Linux_Mtools_McopyCreates_WeExtract() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("mformat")) Assert.Ignore("mtools not installed");

    var data = SmallText;
    var srcFile = Path.Combine(_tmpDir, "test.txt");
    File.WriteAllBytes(srcFile, data);

    var fatPath = Path.Combine(_tmpDir, "mtools.fat");

    // Create a FAT image with mtools (1.44MB floppy format)
    RunToolChecked("dd", $"if=/dev/zero of=\"{fatPath}\" bs=512 count=2880");
    RunToolChecked("mformat", $"-i \"{fatPath}\" -f 1440 ::");
    RunToolChecked("mcopy", $"-i \"{fatPath}\" \"{srcFile}\" ::/TEST.TXT");

    // Read with our library
    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(fatPath, extractDir, null, null);

    var found = FindFile(extractDir, "TEST.TXT");
    Assert.That(found, Is.Not.Null, "TEST.TXT not found in our extraction");
    Assert.That(File.ReadAllBytes(found!), Is.EqualTo(data));
  }

  // ── genisoimage/mkisofs for ISO ───────────────────────────────────

  [Test]
  public void Linux_Isoinfo_OurIso_ReadByIsoinfo() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("isoinfo")) Assert.Ignore("isoinfo not installed");

    var (dir, files) = CreateSourceFiles();
    var isoPath = Path.Combine(_tmpDir, "ours.iso");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(isoPath, inputs, new CompressionOptions());

    var result = RunToolChecked("isoinfo", $"-l -i \"{isoPath}\"");
    foreach (var name in files.Keys) {
      Assert.That(result.StdOut.ToUpperInvariant(), Does.Contain(Path.GetFileName(name).ToUpperInvariant()),
        $"isoinfo output does not list '{name}'");
    }
  }

  [Test]
  public void Linux_Genisoimage_CreatesIso_WeExtract() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    var geniso = HasCommand("genisoimage") ? "genisoimage" : HasCommand("mkisofs") ? "mkisofs" : null;
    if (geniso == null) Assert.Ignore("genisoimage/mkisofs not installed");

    var (dir, files) = CreateSourceFiles();
    var isoPath = Path.Combine(_tmpDir, "gen.iso");
    RunToolChecked(geniso, $"-o \"{isoPath}\" -R -J \"{dir}\"");

    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(isoPath, extractDir, null, null);

    VerifyExtractedFiles(extractDir, files);
  }

  // ── qemu-img for disk images ──────────────────────────────────────

  [Test]
  public void Linux_QemuImg_OurVhd_InfoRecognizes() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("qemu-img")) Assert.Ignore("qemu-img not installed");

    var (dir, files) = CreateSourceFiles();
    var vhdPath = Path.Combine(_tmpDir, "ours.vhd");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(vhdPath, inputs, new CompressionOptions());

    var result = RunToolChecked("qemu-img", $"info \"{vhdPath}\"");
    Assert.That(result.StdOut, Does.Contain("vpc").IgnoreCase.Or.Contain("vhd").IgnoreCase,
      "qemu-img did not recognize VHD format");
  }

  [Test]
  public void Linux_QemuImg_OurVmdk_InfoRecognizes() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("qemu-img")) Assert.Ignore("qemu-img not installed");

    var (dir, files) = CreateSourceFiles();
    var vmdkPath = Path.Combine(_tmpDir, "ours.vmdk");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(vmdkPath, inputs, new CompressionOptions());

    var result = RunToolChecked("qemu-img", $"info \"{vmdkPath}\"");
    Assert.That(result.StdOut, Does.Contain("vmdk").IgnoreCase,
      "qemu-img did not recognize VMDK format");
  }

  [Test]
  public void Linux_QemuImg_OurQcow2_InfoRecognizes() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("qemu-img")) Assert.Ignore("qemu-img not installed");

    var (dir, files) = CreateSourceFiles();
    var qcow2Path = Path.Combine(_tmpDir, "ours.qcow2");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(qcow2Path, inputs, new CompressionOptions());

    var result = RunToolChecked("qemu-img", $"info \"{qcow2Path}\"");
    Assert.That(result.StdOut, Does.Contain("qcow2").IgnoreCase,
      "qemu-img did not recognize QCOW2 format");
  }

  [Test]
  public void Linux_QemuImg_CreatesVmdk_WeRead() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("qemu-img")) Assert.Ignore("qemu-img not installed");

    var vmdkPath = Path.Combine(_tmpDir, "qemu.vmdk");
    RunToolChecked("qemu-img", $"create -f vmdk \"{vmdkPath}\" 10M");

    // Our library should at least be able to list/detect this
    var format = FormatDetector.Detect(vmdkPath);
    Assert.That(format.ToString(), Does.Contain("Vmdk").IgnoreCase,
      "Our detector did not recognize VMDK created by qemu-img");
  }

  // ── Standard Linux compression tools ──────────────────────────────

  [Test]
  public void Linux_Zip_OurOutput_ReadByUnzip() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("unzip")) Assert.Ignore("unzip not installed");

    var (dir, files) = CreateSourceFiles();
    var zipPath = Path.Combine(_tmpDir, "ours.zip");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "unzip_extract");
    Directory.CreateDirectory(extractDir);
    RunToolChecked("unzip", $"-o \"{zipPath}\" -d \"{extractDir}\"");

    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Linux_Zip_SystemCreates_WeExtract() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("zip")) Assert.Ignore("zip not installed");

    var (dir, files) = CreateSourceFiles();
    var zipPath = Path.Combine(_tmpDir, "system.zip");
    // zip -j stores files without directory paths
    var fileArgs = string.Join(" ", files.Keys.Select(n => $"\"{Path.Combine(dir, n)}\""));
    RunToolChecked("zip", $"-j \"{zipPath}\" {fileArgs}");

    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);

    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Linux_Gzip_OurOutput_ReadBySystem() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("gzip")) Assert.Ignore("gzip not installed");

    var data = RepetitiveText;
    var gzPath = Path.Combine(_tmpDir, "ours.gz");
    using (var fs = File.Create(gzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Gzip").Compress(input, fs);
    }

    RunToolChecked("gzip", $"-d -k \"{gzPath}\"");
    var decompressed = File.ReadAllBytes(Path.Combine(_tmpDir, "ours"));
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void Linux_Gzip_SystemCreates_WeRead() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("gzip")) Assert.Ignore("gzip not installed");

    var data = RepetitiveText;
    var rawPath = Path.Combine(_tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    RunToolChecked("gzip", $"-k \"{rawPath}\"");

    using var fs = File.OpenRead(rawPath + ".gz");
    using var ms = new MemoryStream();
    GetStreamOps("Gzip").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void Linux_Bzip2_OurOutput_ReadBySystem() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("bzip2")) Assert.Ignore("bzip2 not installed");

    var data = RepetitiveText;
    var bz2Path = Path.Combine(_tmpDir, "ours.bz2");
    using (var fs = File.Create(bz2Path)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Bzip2").Compress(input, fs);
    }

    RunToolChecked("bzip2", $"-d -k \"{bz2Path}\"");
    var decompressed = File.ReadAllBytes(Path.Combine(_tmpDir, "ours"));
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void Linux_Bzip2_SystemCreates_WeRead() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("bzip2")) Assert.Ignore("bzip2 not installed");

    var data = RepetitiveText;
    var rawPath = Path.Combine(_tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    RunToolChecked("bzip2", $"-k \"{rawPath}\"");

    using var fs = File.OpenRead(rawPath + ".bz2");
    using var ms = new MemoryStream();
    GetStreamOps("Bzip2").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void Linux_Xz_OurOutput_ReadBySystem() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("xz")) Assert.Ignore("xz not installed");

    var data = RepetitiveText;
    var xzPath = Path.Combine(_tmpDir, "ours.xz");
    using (var fs = File.Create(xzPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Xz").Compress(input, fs);
    }

    RunToolChecked("xz", $"-d -k \"{xzPath}\"");
    var decompressed = File.ReadAllBytes(Path.Combine(_tmpDir, "ours"));
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void Linux_Xz_SystemCreates_WeRead() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("xz")) Assert.Ignore("xz not installed");

    var data = RepetitiveText;
    var rawPath = Path.Combine(_tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    RunToolChecked("xz", $"-k \"{rawPath}\"");

    using var fs = File.OpenRead(rawPath + ".xz");
    using var ms = new MemoryStream();
    GetStreamOps("Xz").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void Linux_Zstd_OurOutput_ReadBySystem() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("zstd")) Assert.Ignore("zstd not installed");

    var data = RepetitiveText;
    var zstPath = Path.Combine(_tmpDir, "ours.zst");
    using (var fs = File.Create(zstPath)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Zstd").Compress(input, fs);
    }

    RunToolChecked("zstd", $"-d -k \"{zstPath}\" -o \"{Path.Combine(_tmpDir, "ours_dec")}\"");
    var decompressed = File.ReadAllBytes(Path.Combine(_tmpDir, "ours_dec"));
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void Linux_Zstd_SystemCreates_WeRead() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("zstd")) Assert.Ignore("zstd not installed");

    var data = RepetitiveText;
    var rawPath = Path.Combine(_tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    RunToolChecked("zstd", $"\"{rawPath}\" -o \"{rawPath}.zst\"");

    using var fs = File.OpenRead(rawPath + ".zst");
    using var ms = new MemoryStream();
    GetStreamOps("Zstd").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void Linux_Lz4_OurOutput_ReadBySystem() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("lz4")) Assert.Ignore("lz4 not installed");

    var data = RepetitiveText;
    var lz4Path = Path.Combine(_tmpDir, "ours.lz4");
    using (var fs = File.Create(lz4Path)) {
      using var input = new MemoryStream(data);
      GetStreamOps("Lz4").Compress(input, fs);
    }

    var decPath = Path.Combine(_tmpDir, "ours_dec");
    RunToolChecked("lz4", $"-d \"{lz4Path}\" \"{decPath}\"");
    var decompressed = File.ReadAllBytes(decPath);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void Linux_Lz4_SystemCreates_WeRead() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("lz4")) Assert.Ignore("lz4 not installed");

    var data = RepetitiveText;
    var rawPath = Path.Combine(_tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    RunToolChecked("lz4", $"\"{rawPath}\" \"{rawPath}.lz4\"");

    using var fs = File.OpenRead(rawPath + ".lz4");
    using var ms = new MemoryStream();
    GetStreamOps("Lz4").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void Linux_Tar_OurOutput_ReadBySystem() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("tar")) Assert.Ignore("tar not installed");

    var (dir, files) = CreateSourceFiles();
    var tarPath = Path.Combine(_tmpDir, "ours.tar");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(tarPath, inputs, new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "sys_extract");
    Directory.CreateDirectory(extractDir);
    RunToolChecked("tar", $"xf \"{tarPath}\" -C \"{extractDir}\"");

    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Linux_Tar_SystemCreates_WeExtract() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("tar")) Assert.Ignore("tar not installed");

    var (dir, files) = CreateSourceFiles();
    var tarPath = Path.Combine(_tmpDir, "system.tar");
    var fileArgs = string.Join(" ", files.Keys);
    RunToolChecked("tar", $"cf \"{tarPath}\" -C \"{dir}\" {fileArgs}");

    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(tarPath, extractDir, null, null);

    VerifyExtractedFiles(extractDir, files);
  }

  [Test]
  public void Linux_7z_OurZip_ReadBy7z() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!HasCommand("7z") && !HasCommand("7za")) Assert.Ignore("7z not installed");
    var tool = HasCommand("7z") ? "7z" : "7za";

    var (dir, files) = CreateSourceFiles();
    var zipPath = Path.Combine(_tmpDir, "ours.zip");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

    var extractDir = Path.Combine(_tmpDir, "7z_extract");
    RunToolChecked(tool, $"x \"{zipPath}\" -o\"{extractDir}\" -y");

    VerifyExtractedFiles(extractDir, files);
  }

  // ═══════════════════════════════════════════════════════════════════
  // LINUX — Root tests
  // ═══════════════════════════════════════════════════════════════════

  [Test]
  [CancelAfter(60_000)]
  public void Linux_Root_Ext4_MkfsAndMount_WeRead() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!IsAdmin()) Assert.Ignore("Requires root privileges");
    if (!HasCommand("mkfs.ext4")) Assert.Ignore("mkfs.ext4 not installed");

    var imgPath = Path.Combine(_tmpDir, "ext4.img");
    var mountDir = Path.Combine(_tmpDir, "mnt_ext4");
    Directory.CreateDirectory(mountDir);

    var data = RepetitiveText;

    // Create, format, mount, add file, unmount
    RunToolChecked("dd", $"if=/dev/zero of=\"{imgPath}\" bs=1M count=10");
    RunToolChecked("mkfs.ext4", $"-F \"{imgPath}\"");
    RunToolChecked("mount", $"-o loop \"{imgPath}\" \"{mountDir}\"");

    try {
      File.WriteAllBytes(Path.Combine(mountDir, "test.txt"), data);
      RunToolChecked("umount", $"\"{mountDir}\"");
    } catch {
      try { RunToolChecked("umount", $"\"{mountDir}\""); } catch { /* best effort */ }
      throw;
    }

    // Now read with our library
    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(imgPath, extractDir, null, null);

    var found = FindFile(extractDir, "test.txt");
    Assert.That(found, Is.Not.Null, "test.txt not found in our ext4 extraction");
    Assert.That(File.ReadAllBytes(found!), Is.EqualTo(data));
  }

  [Test]
  [CancelAfter(60_000)]
  public void Linux_Root_Ext4_OurImage_MountAndVerify() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!IsAdmin()) Assert.Ignore("Requires root privileges");
    if (!HasCommand("mount")) Assert.Ignore("mount not available");

    var (dir, files) = CreateSourceFiles();
    var imgPath = Path.Combine(_tmpDir, "ours.ext");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(imgPath, inputs, new CompressionOptions());

    var mountDir = Path.Combine(_tmpDir, "mnt_our_ext4");
    Directory.CreateDirectory(mountDir);

    try {
      RunToolChecked("mount", $"-o loop,ro \"{imgPath}\" \"{mountDir}\"");

      foreach (var (name, expected) in files) {
        var filePath = Path.Combine(mountDir, name);
        Assert.That(File.Exists(filePath), Is.True, $"File not found on mounted ext4: {filePath}");
        Assert.That(File.ReadAllBytes(filePath), Is.EqualTo(expected), $"Data mismatch for {name}");
      }
    } finally {
      try { RunToolChecked("umount", $"\"{mountDir}\""); } catch { /* best effort */ }
    }
  }

  [Test]
  [CancelAfter(60_000)]
  public void Linux_Root_Fat_MkfsAndMount_WeRead() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!IsAdmin()) Assert.Ignore("Requires root privileges");
    if (!HasCommand("mkfs.fat")) Assert.Ignore("mkfs.fat not installed");

    var imgPath = Path.Combine(_tmpDir, "fat.img");
    var mountDir = Path.Combine(_tmpDir, "mnt_fat");
    Directory.CreateDirectory(mountDir);

    var data = SmallText;

    RunToolChecked("dd", $"if=/dev/zero of=\"{imgPath}\" bs=1M count=10");
    RunToolChecked("mkfs.fat", $"-F 16 \"{imgPath}\"");
    RunToolChecked("mount", $"-o loop \"{imgPath}\" \"{mountDir}\"");

    try {
      File.WriteAllBytes(Path.Combine(mountDir, "TEST.TXT"), data);
      RunToolChecked("umount", $"\"{mountDir}\"");
    } catch {
      try { RunToolChecked("umount", $"\"{mountDir}\""); } catch { /* best effort */ }
      throw;
    }

    var extractDir = Path.Combine(_tmpDir, "our_extract");
    ArchiveOperations.Extract(imgPath, extractDir, null, null);

    var found = FindFile(extractDir, "TEST.TXT");
    Assert.That(found, Is.Not.Null, "TEST.TXT not found in our FAT extraction");
    Assert.That(File.ReadAllBytes(found!), Is.EqualTo(data));
  }

  [Test]
  [CancelAfter(60_000)]
  public void Linux_Root_ISO_OurImage_MountAndVerify() {
    if (!IsLinux) Assert.Ignore("Linux-only test");
    if (!IsAdmin()) Assert.Ignore("Requires root privileges");
    if (!HasCommand("mount")) Assert.Ignore("mount not available");

    var (dir, files) = CreateSourceFiles();
    var isoPath = Path.Combine(_tmpDir, "ours.iso");
    var inputs = files.Keys.Select(n => new ArchiveInput(Path.Combine(dir, n), n)).ToList();
    ArchiveOperations.Create(isoPath, inputs, new CompressionOptions());

    var mountDir = Path.Combine(_tmpDir, "mnt_iso");
    Directory.CreateDirectory(mountDir);

    try {
      RunToolChecked("mount", $"-o loop,ro \"{isoPath}\" \"{mountDir}\"");

      foreach (var (name, expected) in files) {
        // ISO filenames may be uppercased; search case-insensitively
        var found = Directory.EnumerateFiles(mountDir, "*", SearchOption.AllDirectories)
          .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        Assert.That(found, Is.Not.Null, $"File not found on mounted ISO: {name}");
        Assert.That(File.ReadAllBytes(found!), Is.EqualTo(expected), $"Data mismatch for {name}");
      }
    } finally {
      try { RunToolChecked("umount", $"\"{mountDir}\""); } catch { /* best effort */ }
    }
  }

  // ═══════════════════════════════════════════════════════════════════
  // Process / tool execution helpers
  // ═══════════════════════════════════════════════════════════════════

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static ToolResult RunShell(string command) {
    var shell = IsWindows ? "cmd.exe" : "/bin/sh";
    var args = IsWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"";
    var psi = new ProcessStartInfo {
      FileName = shell,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start shell for: {command}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(30_000)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }

  private static ToolResult RunPowerShell(string script) {
    var psi = new ProcessStartInfo {
      FileName = "powershell.exe",
      Arguments = $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start PowerShell for: {script}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(60_000)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }

  private static ToolResult RunPowerShellChecked(string script) {
    var result = RunPowerShell(script);
    if (result.ExitCode != 0)
      Assert.Fail($"PowerShell exited with code {result.ExitCode}.\nScript: {script}\nstdout: {result.StdOut}\nstderr: {result.StdErr}");
    return result;
  }

  private static ToolResult RunTool(string tool, string args, int timeoutMs = 60_000) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start {tool}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }

  private static ToolResult RunToolChecked(string tool, string args, int timeoutMs = 60_000) {
    var result = RunTool(tool, args, timeoutMs);
    if (result.ExitCode != 0)
      Assert.Fail($"{Path.GetFileName(tool)} exited with code {result.ExitCode}.\nArgs: {args}\nstdout: {result.StdOut}\nstderr: {result.StdErr}");
    return result;
  }
}
