#pragma warning disable CS1591

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Compression.Analysis.ExternalTools;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests;

/// <summary>
/// Tests that verify our format implementations against libraries in other language
/// ecosystems (Python, Node.js, Perl, Ruby, PowerShell). Different languages have
/// independent implementations of common formats — if our output round-trips through
/// all of them, we're likely spec-compliant.
/// Each test skips gracefully when the required interpreter is not available.
/// </summary>
[TestFixture]
[Category("PolyglotInterop")]
public class PolyglotInteropTests {

  private string _tmpDir = null!;

  [OneTimeSetUp]
  public void OneTimeSetup() {
    FormatRegistration.EnsureInitialized();
  }

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_polyglot_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Test data ──────────────────────────────────────────────────────

  /// <summary>Deterministic ~1 KiB test payload.</summary>
  private static byte[] Payload {
    get {
      var sb = new StringBuilder();
      for (var i = 0; i < 20; i++)
        sb.Append($"Line {i:D3}: The quick brown fox jumps over the lazy dog.\n");
      return Encoding.ASCII.GetBytes(sb.ToString());
    }
  }

  /// <summary>Varied test patterns to catch edge cases in interop.</summary>
  public static IEnumerable<TestCaseData> VariedPayloads() {
    yield return new TestCaseData(new byte[0]).SetName("Empty");
    yield return new TestCaseData(new byte[] { 0x42 }).SetName("SingleByte");
    yield return new TestCaseData(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()).SetName("AllByteValues");
    yield return new TestCaseData(new byte[1024]).SetName("AllZeros1K");
    yield return new TestCaseData(Enumerable.Repeat((byte)0xFF, 1024).ToArray()).SetName("AllOnes1K");
    var rnd = new Random(42);
    var randomData = new byte[8192];
    rnd.NextBytes(randomData);
    yield return new TestCaseData(randomData).SetName("Random8K");
    var utf8 = Encoding.UTF8.GetBytes("Hello 世界 🌍 café naïve résumé " + new string('x', 500));
    yield return new TestCaseData(utf8).SetName("Utf8WithEmoji");
  }

  private static IStreamFormatOperations GetStreamOps(string id) =>
    FormatRegistry.GetStreamOps(id) ?? throw new NotSupportedException($"No stream ops for {id}");

  // ── Interpreter discovery ──────────────────────────────────────────

  private static string? FindPython() =>
    FindInterpreter(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
      ? ["python3", "python", "py"]
      : ["python3", "python"]);

  private static string? FindNode() => FindInterpreter(["node"]);

  private static string? FindPerl() => FindInterpreter(["perl"]);

  private static string? FindRuby() => FindInterpreter(["ruby"]);

  /// <summary>
  /// Try ToolDiscovery first, then fall back to a raw PATH search for names
  /// that ToolDiscovery doesn't know about. Verifies the candidate can actually run
  /// (skips Windows Store "App Execution Alias" stubs which exit with 9009).
  /// </summary>
  private static string? FindInterpreter(string[] names) {
    foreach (var name in names) {
      var candidate = ToolDiscovery.GetToolPath(name) ?? FindOnPath(name);
      if (candidate != null && IsRunnable(candidate))
        return candidate;
    }
    return null;
  }

  /// <summary>
  /// Probe whether <paramref name="path"/> actually runs by invoking it with a harmless
  /// version flag. Returns false for Windows Store "App Execution Alias" stubs that
  /// exist on disk but just print a "please install" message on stderr.
  /// </summary>
  private static bool IsRunnable(string path) {
    // Windows Store alias stubs live under %LOCALAPPDATA%\Microsoft\WindowsApps.
    // Short-circuit those before even probing.
    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (!string.IsNullOrEmpty(local) &&
        path.StartsWith(Path.Combine(local, "Microsoft", "WindowsApps"), StringComparison.OrdinalIgnoreCase))
      return false;

    try {
      var psi = new ProcessStartInfo {
        FileName = path,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      psi.ArgumentList.Add("--version");
      using var proc = Process.Start(psi);
      if (proc == null) return false;
      proc.StandardOutput.ReadToEnd();
      proc.StandardError.ReadToEnd();
      if (!proc.WaitForExit(5_000)) {
        try { proc.Kill(true); } catch { /* ignore */ }
        return false;
      }
      return proc.ExitCode == 0;
    } catch {
      return false;
    }
  }

  private static string? FindOnPath(string toolName) {
    var pathEnv = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(pathEnv)) return null;

    var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    var separator = isWindows ? ';' : ':';
    var extensions = isWindows ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
    var dirs = pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries);

    foreach (var dir in dirs) {
      foreach (var ext in extensions) {
        var candidate = Path.Combine(dir, toolName + ext);
        try {
          if (File.Exists(candidate)) return candidate;
        } catch { /* ignore permission errors */ }
      }
    }
    return null;
  }

  // ── Script runner ──────────────────────────────────────────────────

  private readonly struct ScriptResult {
    public byte[] Stdout { get; init; }
    public string Stderr { get; init; }
    public int ExitCode { get; init; }
  }

  /// <summary>
  /// Writes <paramref name="script"/> to a temp file, invokes <paramref name="interpreter"/>
  /// with the script path followed by <paramref name="args"/>, and returns stdout/stderr/exit code.
  /// </summary>
  private ScriptResult RunScript(
      string interpreter,
      string scriptExtension,
      string script,
      string[] args,
      byte[]? stdinData = null,
      int timeoutMs = 30_000) {
    var scriptFile = Path.Combine(this._tmpDir, $"script_{Guid.NewGuid():N}{scriptExtension}");
    File.WriteAllText(scriptFile, script);

    var psi = new ProcessStartInfo {
      FileName = interpreter,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = stdinData != null,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    psi.ArgumentList.Add(scriptFile);
    foreach (var arg in args) psi.ArgumentList.Add(arg);

    using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {interpreter}");

    if (stdinData != null) {
      proc.StandardInput.BaseStream.Write(stdinData);
      proc.StandardInput.Close();
    }

    using var stdout = new MemoryStream();
    var stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(stdout);
    var stderr = proc.StandardError.ReadToEnd();

    if (!proc.WaitForExit(timeoutMs)) {
      try { proc.Kill(true); } catch { /* ignore */ }
      Assert.Fail($"{Path.GetFileName(interpreter)} timed out after {timeoutMs} ms\nstderr: {stderr}");
    }
    stdoutTask.GetAwaiter().GetResult();

    return new ScriptResult {
      Stdout = stdout.ToArray(),
      Stderr = stderr,
      ExitCode = proc.ExitCode,
    };
  }

  private static void AssertSuccess(ScriptResult r, string label) {
    if (r.ExitCode != 0)
      Assert.Fail($"{label} exited with code {r.ExitCode}.\nstderr: {r.Stderr}");
  }

  // ── Module availability probes ─────────────────────────────────────
  // Probes run a tiny script that exits 0 if the module is importable,
  // 77 otherwise (convention borrowed from autotools for "skipped").

  /// <summary>Returns true if the given Python module can be imported.</summary>
  private bool IsPythonModuleAvailable(string python, string module) {
    try {
      var script = $"import sys\ntry:\n    import {module}\nexcept Exception:\n    sys.exit(77)\nsys.exit(0)\n";
      var r = this.RunScript(python, ".py", script, [], null, 10_000);
      return r.ExitCode == 0;
    } catch {
      return false;
    }
  }

  /// <summary>Returns true if the given Node.js module can be required.</summary>
  private bool IsNodeModuleAvailable(string node, string module) {
    try {
      var script = $"try {{ require('{module}'); }} catch (e) {{ process.exit(77); }}";
      var r = this.RunScript(node, ".js", script, [], null, 10_000);
      return r.ExitCode == 0;
    } catch {
      return false;
    }
  }

  /// <summary>Returns true if the given Perl module is installed.</summary>
  private bool IsPerlModuleAvailable(string perl, string module) {
    try {
      var script = $"eval {{ require {module}; 1 }} or exit 77;\nexit 0;\n";
      var r = this.RunScript(perl, ".pl", script, [], null, 10_000);
      return r.ExitCode == 0;
    } catch {
      return false;
    }
  }

  /// <summary>Returns true if the given Ruby library can be required (gem or stdlib).</summary>
  private bool IsRubyModuleAvailable(string ruby, string module) {
    try {
      var script = $"begin\n  require '{module}'\nrescue LoadError\n  exit 77\nend\nexit 0\n";
      var r = this.RunScript(ruby, ".rb", script, [], null, 10_000);
      return r.ExitCode == 0;
    } catch {
      return false;
    }
  }

  // ── Compression helpers ────────────────────────────────────────────

  private string CreateOurGzip(byte[] data, string name = "ours.gz") {
    var path = Path.Combine(this._tmpDir, name);
    using var fs = File.Create(path);
    using var input = new MemoryStream(data);
    GetStreamOps("Gzip").Compress(input, fs);
    return path;
  }

  private string CreateOurZip(byte[] data, string entryName = "file1.txt", string name = "ours.zip") {
    var srcDir = Path.Combine(this._tmpDir, $"src_{Guid.NewGuid():N}");
    Directory.CreateDirectory(srcDir);
    var srcFile = Path.Combine(srcDir, entryName);
    File.WriteAllBytes(srcFile, data);
    var zipPath = Path.Combine(this._tmpDir, name);
    ArchiveOperations.Create(zipPath, [new ArchiveInput(srcFile, entryName)], new CompressionOptions());
    return zipPath;
  }

  /// <summary>
  /// Compresses <paramref name="data"/> using the given stream format and writes the
  /// result to a temp file, returning the absolute path.
  /// </summary>
  private string CreateOurStream(string formatId, byte[] data, string name) {
    var path = Path.Combine(this._tmpDir, name);
    using var fs = File.Create(path);
    using var input = new MemoryStream(data);
    GetStreamOps(formatId).Compress(input, fs);
    return path;
  }

  /// <summary>Decompresses an entire file via the given stream format and returns the bytes.</summary>
  private static byte[] DecompressOurStream(string formatId, string path) {
    using var fs = File.OpenRead(path);
    using var ms = new MemoryStream();
    GetStreamOps(formatId).Decompress(fs, ms);
    return ms.ToArray();
  }

  private string CreateOurTar(byte[] data1, byte[] data2, string name = "ours.tar") {
    var srcDir = Path.Combine(this._tmpDir, $"src_{Guid.NewGuid():N}");
    Directory.CreateDirectory(srcDir);
    var f1 = Path.Combine(srcDir, "one.txt");
    var f2 = Path.Combine(srcDir, "two.txt");
    File.WriteAllBytes(f1, data1);
    File.WriteAllBytes(f2, data2);
    var tarPath = Path.Combine(this._tmpDir, name);
    ArchiveOperations.Create(tarPath, [
      new ArchiveInput(f1, "one.txt"),
      new ArchiveInput(f2, "two.txt"),
    ], new CompressionOptions());
    return tarPath;
  }

  // ── Python tests ───────────────────────────────────────────────────

  [Test]
  public void Python_ReadsOurGzip() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");

    var data = Payload;
    var gzPath = CreateOurGzip(data);

    const string script = """
import gzip, sys
with gzip.open(sys.argv[1], 'rb') as f:
    sys.stdout.buffer.write(f.read())
""";
    var result = this.RunScript(python!, ".py", script, [gzPath]);
    AssertSuccess(result, "python gzip reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Python_GzipCreates_WeRead() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var gzPath = Path.Combine(this._tmpDir, "py.gz");

    var script = """
import gzip, sys
with open(sys.argv[1], 'rb') as src, gzip.open(sys.argv[2], 'wb') as dst:
    dst.write(src.read())
""";
    var result = this.RunScript(python!, ".py", script, [rawPath, gzPath]);
    AssertSuccess(result, "python gzip writer");

    using var fs = File.OpenRead(gzPath);
    using var ms = new MemoryStream();
    GetStreamOps("Gzip").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void Python_ReadsOurZip() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");

    var data = Payload;
    var zipPath = CreateOurZip(data, "file1.txt");

    const string script = """
import zipfile, sys
with zipfile.ZipFile(sys.argv[1]) as z:
    names = z.namelist()
    assert names == ['file1.txt'], f'unexpected names {names}'
    sys.stdout.buffer.write(z.read('file1.txt'))
""";
    var result = this.RunScript(python!, ".py", script, [zipPath]);
    AssertSuccess(result, "python zipfile reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Python_ZipCreates_WeRead() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");

    var data = Payload;
    var zipPath = Path.Combine(this._tmpDir, "py.zip");
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);

    const string script = """
import zipfile, sys
with open(sys.argv[1], 'rb') as f:
    payload = f.read()
with zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED) as z:
    z.writestr('file1.txt', payload)
    z.writestr('file2.bin', bytes(range(256)))
""";
    var result = this.RunScript(python!, ".py", script, [rawPath, zipPath]);
    AssertSuccess(result, "python zipfile writer");

    var extractDir = Path.Combine(this._tmpDir, "extract");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);

    var f1 = Path.Combine(extractDir, "file1.txt");
    Assert.That(File.Exists(f1), $"file1.txt not extracted. Contents: {string.Join(", ", Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))}");
    Assert.That(File.ReadAllBytes(f1), Is.EqualTo(data));

    var expected2 = new byte[256];
    for (var i = 0; i < 256; i++) expected2[i] = (byte)i;
    var f2 = Path.Combine(extractDir, "file2.bin");
    Assert.That(File.Exists(f2), "file2.bin not extracted");
    Assert.That(File.ReadAllBytes(f2), Is.EqualTo(expected2));
  }

  [Test]
  public void Python_ReadsOurTar() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");

    var data1 = Payload;
    var data2 = Encoding.ASCII.GetBytes("second file contents");
    var tarPath = CreateOurTar(data1, data2);

    const string script = """
import tarfile, sys
with tarfile.open(sys.argv[1]) as t:
    names = sorted(m.name for m in t.getmembers())
    print(' '.join(names))
    for name in names:
        f = t.extractfile(name)
        if f is not None:
            sys.stdout.buffer.write(f.read())
""";
    var result = this.RunScript(python!, ".py", script, [tarPath]);
    AssertSuccess(result, "python tarfile reader");

    // Expect: first line "one.txt two.txt\n" then concatenation of contents (one.txt then two.txt).
    var stdout = result.Stdout;
    var newline = Array.IndexOf(stdout, (byte)'\n');
    Assert.That(newline, Is.GreaterThan(0), "no newline after member list");
    var listLine = Encoding.ASCII.GetString(stdout, 0, newline).TrimEnd('\r');
    Assert.That(listLine, Is.EqualTo("one.txt two.txt"));

    var payload = stdout.AsSpan(newline + 1).ToArray();
    var expected = new byte[data1.Length + data2.Length];
    Buffer.BlockCopy(data1, 0, expected, 0, data1.Length);
    Buffer.BlockCopy(data2, 0, expected, data1.Length, data2.Length);
    Assert.That(payload, Is.EqualTo(expected));
  }

  // ── Node.js tests ──────────────────────────────────────────────────

  [Test]
  public void Node_ReadsOurGzip() {
    var node = FindNode();
    if (node == null) Assert.Ignore("node interpreter not found");

    var data = Payload;
    var gzPath = CreateOurGzip(data);

    const string script = """
const fs = require('fs'), zlib = require('zlib');
const decompressed = zlib.gunzipSync(fs.readFileSync(process.argv[2]));
process.stdout.write(decompressed);
""";
    var result = this.RunScript(node!, ".js", script, [gzPath]);
    AssertSuccess(result, "node zlib reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Node_GzipCreates_WeRead() {
    var node = FindNode();
    if (node == null) Assert.Ignore("node interpreter not found");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var gzPath = Path.Combine(this._tmpDir, "node.gz");

    const string script = """
const fs = require('fs'), zlib = require('zlib');
const input = fs.readFileSync(process.argv[2]);
fs.writeFileSync(process.argv[3], zlib.gzipSync(input));
""";
    var result = this.RunScript(node!, ".js", script, [rawPath, gzPath]);
    AssertSuccess(result, "node zlib writer");

    using var fs = File.OpenRead(gzPath);
    using var ms = new MemoryStream();
    GetStreamOps("Gzip").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  // ── Parameterized stress tests (varied payloads) ──────────────────

  [TestCaseSource(nameof(VariedPayloads))]
  public void Node_ReadsOurGzip_Varied(byte[] data) {
    var node = FindNode();
    if (node == null) Assert.Ignore("node interpreter not found");
    if (data.Length == 0) Assert.Ignore("empty gzip input not supported by all writers");

    var gzPath = CreateOurGzip(data);
    const string script = "const fs = require('fs'), zlib = require('zlib'); process.stdout.write(zlib.gunzipSync(fs.readFileSync(process.argv[2])));";
    var result = this.RunScript(node!, ".js", script, [gzPath]);
    AssertSuccess(result, "node zlib reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [TestCaseSource(nameof(VariedPayloads))]
  public void Node_GzipCreates_WeRead_Varied(byte[] data) {
    var node = FindNode();
    if (node == null) Assert.Ignore("node interpreter not found");
    if (data.Length == 0) Assert.Ignore("empty gzip not round-trippable");

    var rawPath = Path.Combine(this._tmpDir, $"raw_{data.Length}.bin");
    File.WriteAllBytes(rawPath, data);
    var gzPath = Path.Combine(this._tmpDir, $"node_{data.Length}.gz");

    const string script = "const fs = require('fs'), zlib = require('zlib'); fs.writeFileSync(process.argv[3], zlib.gzipSync(fs.readFileSync(process.argv[2])));";
    var result = this.RunScript(node!, ".js", script, [rawPath, gzPath]);
    AssertSuccess(result, "node zlib writer");

    using var fs = File.OpenRead(gzPath);
    using var ms = new MemoryStream();
    GetStreamOps("Gzip").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  [TestCaseSource(nameof(VariedPayloads))]
  [Ignore("Same known limitation as Node_ZlibBrotli_ReadsOurBrotli — our LZ77 Brotli has libbrotli interop issues.")]
  public void Node_ReadsOurBrotli_Varied(byte[] data) {
    var node = FindNode();
    if (node == null) Assert.Ignore("node interpreter not found");

    var brPath = this.CreateOurStream("Brotli", data, $"ours_{data.Length}.br");
    const string script = "const fs = require('fs'), zlib = require('zlib'); process.stdout.write(zlib.brotliDecompressSync(fs.readFileSync(process.argv[2])));";
    var result = this.RunScript(node!, ".js", script, [brPath]);
    AssertSuccess(result, "node brotli reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [TestCaseSource(nameof(VariedPayloads))]
  public void PowerShell_ZipRoundTrip_Varied(byte[] data) {
    if (!OperatingSystem.IsWindows()) Assert.Ignore("PowerShell test only on Windows");
    if (data.Length == 0) Assert.Ignore("empty ZIP not meaningful for this test");

    // Create our ZIP containing one entry with the test data
    var rawPath = Path.Combine(this._tmpDir, "input.bin");
    File.WriteAllBytes(rawPath, data);
    var zipPath = Path.Combine(this._tmpDir, $"ours_{data.Length}.zip");
    ArchiveOperations.Create(zipPath, [new ArchiveInput(rawPath, "file.bin")], new CompressionOptions());

    // Extract with PowerShell
    var extractDir = Path.Combine(this._tmpDir, "ps_extract");
    Directory.CreateDirectory(extractDir);
    var psScript = $"Expand-Archive -Path '{zipPath}' -DestinationPath '{extractDir}' -Force";
    var psResult = RunPowerShell(psScript);
    if (psResult.ExitCode != 0)
      Assert.Fail($"PowerShell Expand-Archive failed for size {data.Length}: {psResult.Stderr}");

    var extracted = Path.Combine(extractDir, "file.bin");
    Assert.That(File.Exists(extracted), Is.True, $"PowerShell didn't extract for size {data.Length}");
    Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(data));
  }

  [TestCaseSource(nameof(VariedPayloads))]
  public void Perl_ReadsOurGzip_Varied(byte[] data) {
    var perl = FindPerl();
    if (perl == null) Assert.Ignore("perl interpreter not found");
    if (data.Length == 0) Assert.Ignore("empty gzip not round-trippable");

    var gzPath = CreateOurGzip(data);
    const string script = """
use IO::Uncompress::Gunzip qw(gunzip $GunzipError);
binmode STDOUT;
my $out;
gunzip $ARGV[0] => \$out or die "gunzip failed: $GunzipError\n";
print $out;
""";
    var result = this.RunScript(perl!, ".pl", script, [gzPath]);
    AssertSuccess(result, "perl gunzip reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  // ── Perl tests ─────────────────────────────────────────────────────

  [Test]
  public void Perl_ReadsOurGzip() {
    var perl = FindPerl();
    if (perl == null) Assert.Ignore("perl interpreter not found");

    var data = Payload;
    var gzPath = CreateOurGzip(data);

    const string script = """
use strict;
use warnings;
eval { require IO::Uncompress::Gunzip; IO::Uncompress::Gunzip->import(qw(gunzip $GunzipError)); 1 }
    or do { print STDERR "SKIP: IO::Uncompress::Gunzip not installed\n"; exit 77 };
binmode STDOUT;
my $out;
IO::Uncompress::Gunzip::gunzip($ARGV[0] => \$out)
    or die "gunzip failed: $IO::Uncompress::Gunzip::GunzipError";
print $out;
""";
    var result = this.RunScript(perl!, ".pl", script, [gzPath]);
    if (result.ExitCode == 77) Assert.Ignore($"Perl module missing: {result.Stderr.Trim()}");
    AssertSuccess(result, "perl gunzip");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Perl_GzipCreates_WeRead() {
    var perl = FindPerl();
    if (perl == null) Assert.Ignore("perl interpreter not found");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var gzPath = Path.Combine(this._tmpDir, "perl.gz");

    const string script = """
use strict;
use warnings;
eval { require IO::Compress::Gzip; IO::Compress::Gzip->import(qw(gzip $GzipError)); 1 }
    or do { print STDERR "SKIP: IO::Compress::Gzip not installed\n"; exit 77 };
IO::Compress::Gzip::gzip($ARGV[0] => $ARGV[1])
    or die "gzip failed: $IO::Compress::Gzip::GzipError";
""";
    var result = this.RunScript(perl!, ".pl", script, [rawPath, gzPath]);
    if (result.ExitCode == 77) Assert.Ignore($"Perl module missing: {result.Stderr.Trim()}");
    AssertSuccess(result, "perl gzip");

    using var fs = File.OpenRead(gzPath);
    using var ms = new MemoryStream();
    GetStreamOps("Gzip").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  // ── Ruby tests ─────────────────────────────────────────────────────

  [Test]
  public void Ruby_ReadsOurGzip() {
    var ruby = FindRuby();
    if (ruby == null) Assert.Ignore("ruby interpreter not found");

    var data = Payload;
    var gzPath = CreateOurGzip(data);

    const string script = """
require 'zlib'
$stdout.binmode
Zlib::GzipReader.open(ARGV[0]) { |gz| $stdout.write(gz.read) }
""";
    var result = this.RunScript(ruby!, ".rb", script, [gzPath]);
    AssertSuccess(result, "ruby GzipReader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Ruby_GzipCreates_WeRead() {
    var ruby = FindRuby();
    if (ruby == null) Assert.Ignore("ruby interpreter not found");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var gzPath = Path.Combine(this._tmpDir, "ruby.gz");

    const string script = """
require 'zlib'
payload = File.binread(ARGV[0])
Zlib::GzipWriter.open(ARGV[1]) { |gz| gz.write(payload) }
""";
    var result = this.RunScript(ruby!, ".rb", script, [rawPath, gzPath]);
    AssertSuccess(result, "ruby GzipWriter");

    using var fs = File.OpenRead(gzPath);
    using var ms = new MemoryStream();
    GetStreamOps("Gzip").Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  // ── Bzip2 ──────────────────────────────────────────────────────────

  [Test]
  public void Python_Bz2_ReadsOurBzip2() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    // bz2 is part of the Python stdlib; probe anyway to catch broken builds.
    if (!this.IsPythonModuleAvailable(python!, "bz2")) Assert.Ignore("python bz2 module not available");

    var data = Payload;
    var bzPath = this.CreateOurStream("Bzip2", data, "ours.bz2");

    const string script = """
import bz2, sys
with open(sys.argv[1], 'rb') as f:
    sys.stdout.buffer.write(bz2.decompress(f.read()))
""";
    var result = this.RunScript(python!, ".py", script, [bzPath]);
    AssertSuccess(result, "python bz2 reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Python_Bz2Creates_WeRead() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "bz2")) Assert.Ignore("python bz2 module not available");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var bzPath = Path.Combine(this._tmpDir, "py.bz2");

    const string script = """
import bz2, sys
with open(sys.argv[1], 'rb') as src, open(sys.argv[2], 'wb') as dst:
    dst.write(bz2.compress(src.read()))
""";
    var result = this.RunScript(python!, ".py", script, [rawPath, bzPath]);
    AssertSuccess(result, "python bz2 writer");

    Assert.That(DecompressOurStream("Bzip2", bzPath), Is.EqualTo(data));
  }

  [Test]
  public void Perl_ReadsOurBzip2() {
    var perl = FindPerl();
    if (perl == null) Assert.Ignore("perl interpreter not found");
    if (!this.IsPerlModuleAvailable(perl!, "IO::Uncompress::Bunzip2"))
      Assert.Ignore("Perl IO::Uncompress::Bunzip2 not installed");

    var data = Payload;
    var bzPath = this.CreateOurStream("Bzip2", data, "ours.bz2");

    const string script = """
use strict;
use warnings;
use IO::Uncompress::Bunzip2 qw(bunzip2 $Bunzip2Error);
binmode STDOUT;
my $out;
bunzip2($ARGV[0] => \$out) or die "bunzip2 failed: $Bunzip2Error";
print $out;
""";
    var result = this.RunScript(perl!, ".pl", script, [bzPath]);
    AssertSuccess(result, "perl bunzip2");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  // ── XZ / LZMA ──────────────────────────────────────────────────────

  [Test]
  public void Python_Lzma_ReadsOurXz() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "lzma")) Assert.Ignore("python lzma module not available");

    var data = Payload;
    var xzPath = this.CreateOurStream("Xz", data, "ours.xz");

    const string script = """
import lzma, sys
with open(sys.argv[1], 'rb') as f:
    sys.stdout.buffer.write(lzma.decompress(f.read()))
""";
    var result = this.RunScript(python!, ".py", script, [xzPath]);
    AssertSuccess(result, "python lzma reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Python_LzmaCreates_WeRead() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "lzma")) Assert.Ignore("python lzma module not available");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var xzPath = Path.Combine(this._tmpDir, "py.xz");

    // Python defaults to FORMAT_XZ, which matches our Xz container.
    const string script = """
import lzma, sys
with open(sys.argv[1], 'rb') as src, open(sys.argv[2], 'wb') as dst:
    dst.write(lzma.compress(src.read()))
""";
    var result = this.RunScript(python!, ".py", script, [rawPath, xzPath]);
    AssertSuccess(result, "python lzma writer");

    Assert.That(DecompressOurStream("Xz", xzPath), Is.EqualTo(data));
  }

  [Test]
  public void Perl_ReadsOurXz() {
    var perl = FindPerl();
    if (perl == null) Assert.Ignore("perl interpreter not found");
    if (!this.IsPerlModuleAvailable(perl!, "IO::Uncompress::UnXz"))
      Assert.Ignore("Perl IO::Uncompress::UnXz not installed (IO-Compress-Lzma)");

    var data = Payload;
    var xzPath = this.CreateOurStream("Xz", data, "ours.xz");

    const string script = """
use strict;
use warnings;
use IO::Uncompress::UnXz qw(unxz $UnXzError);
binmode STDOUT;
my $out;
unxz($ARGV[0] => \$out) or die "unxz failed: $UnXzError";
print $out;
""";
    var result = this.RunScript(perl!, ".pl", script, [xzPath]);
    AssertSuccess(result, "perl unxz");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  // ── Zstandard ──────────────────────────────────────────────────────

  [Test]
  public void Python_Zstandard_ReadsOurZstd() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "zstandard"))
      Assert.Ignore("python zstandard not installed (pip install zstandard)");

    var data = Payload;
    var zstPath = this.CreateOurStream("Zstd", data, "ours.zst");

    const string script = """
import zstandard, sys
with open(sys.argv[1], 'rb') as f:
    dctx = zstandard.ZstdDecompressor()
    sys.stdout.buffer.write(dctx.decompress(f.read()))
""";
    var result = this.RunScript(python!, ".py", script, [zstPath]);
    AssertSuccess(result, "python zstandard reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Python_ZstandardCreates_WeRead() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "zstandard"))
      Assert.Ignore("python zstandard not installed (pip install zstandard)");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var zstPath = Path.Combine(this._tmpDir, "py.zst");

    const string script = """
import zstandard, sys
with open(sys.argv[1], 'rb') as src, open(sys.argv[2], 'wb') as dst:
    cctx = zstandard.ZstdCompressor()
    dst.write(cctx.compress(src.read()))
""";
    var result = this.RunScript(python!, ".py", script, [rawPath, zstPath]);
    AssertSuccess(result, "python zstandard writer");

    Assert.That(DecompressOurStream("Zstd", zstPath), Is.EqualTo(data));
  }

  [Test]
  public void Ruby_ZstdRuby_ReadsOurZstd() {
    var ruby = FindRuby();
    if (ruby == null) Assert.Ignore("ruby interpreter not found");
    if (!this.IsRubyModuleAvailable(ruby!, "zstd-ruby"))
      Assert.Ignore("ruby zstd-ruby gem not installed (gem install zstd-ruby)");

    var data = Payload;
    var zstPath = this.CreateOurStream("Zstd", data, "ours.zst");

    const string script = """
require 'zstd-ruby'
$stdout.binmode
$stdout.write(Zstd.decompress(File.binread(ARGV[0])))
""";
    var result = this.RunScript(ruby!, ".rb", script, [zstPath]);
    AssertSuccess(result, "ruby zstd-ruby reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  // ── Brotli ─────────────────────────────────────────────────────────

  [Test]
  public void Python_Brotli_ReadsOurBrotli() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "brotli"))
      Assert.Ignore("python brotli not installed (pip install brotli)");

    var data = Payload;
    var brPath = this.CreateOurStream("Brotli", data, "ours.br");

    const string script = """
import brotli, sys
with open(sys.argv[1], 'rb') as f:
    sys.stdout.buffer.write(brotli.decompress(f.read()))
""";
    var result = this.RunScript(python!, ".py", script, [brPath]);
    AssertSuccess(result, "python brotli reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Python_BrotliCreates_WeRead() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "brotli"))
      Assert.Ignore("python brotli not installed (pip install brotli)");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var brPath = Path.Combine(this._tmpDir, "py.br");

    const string script = """
import brotli, sys
with open(sys.argv[1], 'rb') as src, open(sys.argv[2], 'wb') as dst:
    dst.write(brotli.compress(src.read()))
""";
    var result = this.RunScript(python!, ".py", script, [rawPath, brPath]);
    AssertSuccess(result, "python brotli writer");

    Assert.That(DecompressOurStream("Brotli", brPath), Is.EqualTo(data));
  }

  [Test]
  public void Node_ZlibBrotli_ReadsOurBrotli() {
    var node = FindNode();
    if (node == null) Assert.Ignore("node interpreter not found");

    var data = Payload;
    var brPath = this.CreateOurStream("Brotli", data, "ours.br");

    const string script = """
const fs = require('fs'), zlib = require('zlib');
const out = zlib.brotliDecompressSync(fs.readFileSync(process.argv[2]));
process.stdout.write(out);
""";
    var result = this.RunScript(node!, ".js", script, [brPath]);
    AssertSuccess(result, "node zlib brotli reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  [Ignore("Known limitation: our clean-room Brotli decoder does not yet fully implement libbrotli's " +
          "compressed meta-blocks (diverges at byte 8+). Requires full RFC 7932 decoder (Huffman trees, " +
          "context models, static dictionary). Uncompressed meta-blocks we can decode. TODO Phase 31 §1b.")]
  public void Node_ZlibBrotliCreates_WeRead() {
    var node = FindNode();
    if (node == null) Assert.Ignore("node interpreter not found");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var brPath = Path.Combine(this._tmpDir, "node.br");

    const string script = """
const fs = require('fs'), zlib = require('zlib');
const input = fs.readFileSync(process.argv[2]);
fs.writeFileSync(process.argv[3], zlib.brotliCompressSync(input));
""";
    var result = this.RunScript(node!, ".js", script, [rawPath, brPath]);
    AssertSuccess(result, "node zlib brotli writer");

    Assert.That(DecompressOurStream("Brotli", brPath), Is.EqualTo(data));
  }

  // ── LZ4 (frame format) ─────────────────────────────────────────────

  [Test]
  public void Python_Lz4Frame_ReadsOurLz4() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "lz4.frame"))
      Assert.Ignore("python lz4 not installed (pip install lz4)");

    var data = Payload;
    var lz4Path = this.CreateOurStream("Lz4", data, "ours.lz4");

    const string script = """
import lz4.frame, sys
with open(sys.argv[1], 'rb') as f:
    sys.stdout.buffer.write(lz4.frame.decompress(f.read()))
""";
    var result = this.RunScript(python!, ".py", script, [lz4Path]);
    AssertSuccess(result, "python lz4.frame reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Python_Lz4FrameCreates_WeRead() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "lz4.frame"))
      Assert.Ignore("python lz4 not installed (pip install lz4)");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var lz4Path = Path.Combine(this._tmpDir, "py.lz4");

    const string script = """
import lz4.frame, sys
with open(sys.argv[1], 'rb') as src, open(sys.argv[2], 'wb') as dst:
    dst.write(lz4.frame.compress(src.read()))
""";
    var result = this.RunScript(python!, ".py", script, [rawPath, lz4Path]);
    AssertSuccess(result, "python lz4.frame writer");

    Assert.That(DecompressOurStream("Lz4", lz4Path), Is.EqualTo(data));
  }

  // ── 7z (py7zr) ─────────────────────────────────────────────────────

  [Test]
  public void Python_Py7zr_ReadsOurSevenZip() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "py7zr"))
      Assert.Ignore("python py7zr not installed (pip install py7zr)");

    var data = Payload;
    var srcDir = Path.Combine(this._tmpDir, "sz_src");
    Directory.CreateDirectory(srcDir);
    var srcFile = Path.Combine(srcDir, "file1.txt");
    File.WriteAllBytes(srcFile, data);
    var szPath = Path.Combine(this._tmpDir, "ours.7z");
    ArchiveOperations.Create(szPath, [new ArchiveInput(srcFile, "file1.txt")], new CompressionOptions());

    const string script = """
import py7zr, sys
with py7zr.SevenZipFile(sys.argv[1]) as z:
    names = z.getnames()
    assert names == ['file1.txt'], f'unexpected names {names}'
    blobs = z.readall()
    sys.stdout.buffer.write(blobs['file1.txt'].read())
""";
    var result = this.RunScript(python!, ".py", script, [szPath]);
    AssertSuccess(result, "python py7zr reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Python_Py7zrCreates_WeRead() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "py7zr"))
      Assert.Ignore("python py7zr not installed (pip install py7zr)");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var szPath = Path.Combine(this._tmpDir, "py.7z");

    const string script = """
import py7zr, sys
with open(sys.argv[1], 'rb') as f:
    payload = f.read()
with py7zr.SevenZipFile(sys.argv[2], 'w') as z:
    z.writestr(payload, 'file1.txt')
""";
    var result = this.RunScript(python!, ".py", script, [rawPath, szPath]);
    AssertSuccess(result, "python py7zr writer");

    var extractDir = Path.Combine(this._tmpDir, "sz_extract");
    ArchiveOperations.Extract(szPath, extractDir, null, null);
    var f1 = Path.Combine(extractDir, "file1.txt");
    Assert.That(File.Exists(f1), "file1.txt not extracted from py7zr-created archive");
    Assert.That(File.ReadAllBytes(f1), Is.EqualTo(data));
  }

  // ── RAR (read-only via rarfile) ────────────────────────────────────
  // Our RAR implementation is create-capable for some variants; skip if not.

  [Test]
  public void Python_Rarfile_ReadsOurRar() {
    var python = FindPython();
    if (python == null) Assert.Ignore("Python interpreter not found");
    if (!this.IsPythonModuleAvailable(python!, "rarfile"))
      Assert.Ignore("python rarfile not installed (pip install rarfile)");

    var data = Payload;
    var srcDir = Path.Combine(this._tmpDir, "rar_src");
    Directory.CreateDirectory(srcDir);
    var srcFile = Path.Combine(srcDir, "file1.txt");
    File.WriteAllBytes(srcFile, data);
    var rarPath = Path.Combine(this._tmpDir, "ours.rar");

    try {
      ArchiveOperations.Create(rarPath, [new ArchiveInput(srcFile, "file1.txt")], new CompressionOptions());
    } catch (NotSupportedException) {
      Assert.Ignore("RAR create not supported by our implementation");
    } catch (NotImplementedException) {
      Assert.Ignore("RAR create not implemented");
    }
    if (!File.Exists(rarPath))
      Assert.Ignore("RAR archive not produced");

    const string script = """
import rarfile, sys
try:
    with rarfile.RarFile(sys.argv[1]) as r:
        names = r.namelist()
        assert names == ['file1.txt'], f'unexpected names {names}'
        sys.stdout.buffer.write(r.read('file1.txt'))
except rarfile.NeedFirstVolume:
    sys.exit(77)
except rarfile.BadRarFile:
    sys.exit(77)
""";
    var result = this.RunScript(python!, ".py", script, [rarPath]);
    if (result.ExitCode == 77)
      Assert.Ignore($"rarfile could not read our RAR: {result.Stderr.Trim()}");
    AssertSuccess(result, "python rarfile reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  // ── Raw DEFLATE (zlib format) ──────────────────────────────────────

  [Test]
  public void Node_ZlibInflate_ReadsOurZlib() {
    var node = FindNode();
    if (node == null) Assert.Ignore("node interpreter not found");

    var data = Payload;
    var zPath = this.CreateOurStream("Zlib", data, "ours.zz");

    const string script = """
const fs = require('fs'), zlib = require('zlib');
const out = zlib.inflateSync(fs.readFileSync(process.argv[2]));
process.stdout.write(out);
""";
    var result = this.RunScript(node!, ".js", script, [zPath]);
    AssertSuccess(result, "node zlib inflate");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Node_ZlibDeflate_Creates_WeRead() {
    var node = FindNode();
    if (node == null) Assert.Ignore("node interpreter not found");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var zPath = Path.Combine(this._tmpDir, "node.zz");

    const string script = """
const fs = require('fs'), zlib = require('zlib');
fs.writeFileSync(process.argv[3], zlib.deflateSync(fs.readFileSync(process.argv[2])));
""";
    var result = this.RunScript(node!, ".js", script, [rawPath, zPath]);
    AssertSuccess(result, "node zlib deflate");

    Assert.That(DecompressOurStream("Zlib", zPath), Is.EqualTo(data));
  }

  // ── ZIP (additional language bindings) ─────────────────────────────

  [Test]
  public void Node_AdmZip_ReadsOurZip() {
    var node = FindNode();
    if (node == null) Assert.Ignore("node interpreter not found");
    if (!this.IsNodeModuleAvailable(node!, "adm-zip"))
      Assert.Ignore("node adm-zip not installed (npm install adm-zip)");

    var data = Payload;
    var zipPath = this.CreateOurZip(data, "file1.txt", "ours_adm.zip");

    const string script = """
const AdmZip = require('adm-zip');
const zip = new AdmZip(process.argv[2]);
const entries = zip.getEntries();
if (entries.length !== 1 || entries[0].entryName !== 'file1.txt') {
    process.stderr.write('unexpected entries: ' + JSON.stringify(entries.map(e => e.entryName)));
    process.exit(2);
}
process.stdout.write(zip.readFile('file1.txt'));
""";
    var result = this.RunScript(node!, ".js", script, [zipPath]);
    AssertSuccess(result, "node adm-zip reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Perl_ArchiveZip_ReadsOurZip() {
    var perl = FindPerl();
    if (perl == null) Assert.Ignore("perl interpreter not found");
    if (!this.IsPerlModuleAvailable(perl!, "Archive::Zip"))
      Assert.Ignore("Perl Archive::Zip not installed (cpanm Archive::Zip)");

    var data = Payload;
    var zipPath = this.CreateOurZip(data, "file1.txt", "ours_perl.zip");

    const string script = """
use strict;
use warnings;
use Archive::Zip qw(:ERROR_CODES);
binmode STDOUT;
my $zip = Archive::Zip->new();
$zip->read($ARGV[0]) == AZ_OK or die "read failed";
my @names = sort $zip->memberNames();
die "unexpected names: @names" unless "@names" eq "file1.txt";
my $contents = $zip->contents('file1.txt');
print $contents;
""";
    var result = this.RunScript(perl!, ".pl", script, [zipPath]);
    AssertSuccess(result, "perl Archive::Zip reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Ruby_Rubyzip_ReadsOurZip() {
    var ruby = FindRuby();
    if (ruby == null) Assert.Ignore("ruby interpreter not found");
    if (!this.IsRubyModuleAvailable(ruby!, "zip"))
      Assert.Ignore("ruby rubyzip not installed (gem install rubyzip)");

    var data = Payload;
    var zipPath = this.CreateOurZip(data, "file1.txt", "ours_rz.zip");

    const string script = """
require 'zip'
$stdout.binmode
Zip::File.open(ARGV[0]) do |z|
  names = z.entries.map(&:name).sort
  abort("unexpected names: #{names.inspect}") unless names == ['file1.txt']
  $stdout.write(z.find_entry('file1.txt').get_input_stream.read)
end
""";
    var result = this.RunScript(ruby!, ".rb", script, [zipPath]);
    AssertSuccess(result, "ruby rubyzip reader");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  [Test]
  public void Ruby_RubyzipCreates_WeRead() {
    var ruby = FindRuby();
    if (ruby == null) Assert.Ignore("ruby interpreter not found");
    if (!this.IsRubyModuleAvailable(ruby!, "zip"))
      Assert.Ignore("ruby rubyzip not installed (gem install rubyzip)");

    var data = Payload;
    var rawPath = Path.Combine(this._tmpDir, "raw.bin");
    File.WriteAllBytes(rawPath, data);
    var zipPath = Path.Combine(this._tmpDir, "ruby.zip");

    const string script = """
require 'zip'
payload = File.binread(ARGV[0])
Zip::File.open(ARGV[1], create: true) do |z|
  z.get_output_stream('file1.txt') { |io| io.write(payload) }
end
""";
    var result = this.RunScript(ruby!, ".rb", script, [rawPath, zipPath]);
    AssertSuccess(result, "ruby rubyzip writer");

    var extractDir = Path.Combine(this._tmpDir, "our_extract_rz");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);
    var extracted = Path.Combine(extractDir, "file1.txt");
    Assert.That(File.Exists(extracted), "file1.txt not extracted from rubyzip archive");
    Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(data));
  }

  // ── TAR (additional language bindings) ─────────────────────────────

  [Test]
  public void Node_TarStream_ReadsOurTar() {
    var node = FindNode();
    if (node == null) Assert.Ignore("node interpreter not found");
    if (!this.IsNodeModuleAvailable(node!, "tar-stream"))
      Assert.Ignore("node tar-stream not installed (npm install tar-stream)");

    var data1 = Payload;
    var data2 = Encoding.ASCII.GetBytes("second file contents");
    var tarPath = this.CreateOurTar(data1, data2);

    const string script = """
const fs = require('fs'), tar = require('tar-stream');
const extract = tar.extract();
const chunks = {};
extract.on('entry', (header, stream, next) => {
    const bufs = [];
    stream.on('data', c => bufs.push(c));
    stream.on('end', () => { chunks[header.name] = Buffer.concat(bufs); next(); });
    stream.resume();
});
extract.on('finish', () => {
    const names = Object.keys(chunks).sort();
    process.stdout.write(names.join(' ') + '\n');
    for (const n of names) process.stdout.write(chunks[n]);
});
fs.createReadStream(process.argv[2]).pipe(extract);
""";
    var result = this.RunScript(node!, ".js", script, [tarPath]);
    AssertSuccess(result, "node tar-stream reader");

    var stdout = result.Stdout;
    var newline = Array.IndexOf(stdout, (byte)'\n');
    Assert.That(newline, Is.GreaterThan(0), "no newline after member list");
    var listLine = Encoding.ASCII.GetString(stdout, 0, newline).TrimEnd('\r');
    Assert.That(listLine, Is.EqualTo("one.txt two.txt"));

    var payload = stdout.AsSpan(newline + 1).ToArray();
    var expected = new byte[data1.Length + data2.Length];
    Buffer.BlockCopy(data1, 0, expected, 0, data1.Length);
    Buffer.BlockCopy(data2, 0, expected, data1.Length, data2.Length);
    Assert.That(payload, Is.EqualTo(expected));
  }

  [Test]
  public void Perl_ArchiveTar_ReadsOurTar() {
    var perl = FindPerl();
    if (perl == null) Assert.Ignore("perl interpreter not found");
    if (!this.IsPerlModuleAvailable(perl!, "Archive::Tar"))
      Assert.Ignore("Perl Archive::Tar not installed");

    var data1 = Payload;
    var data2 = Encoding.ASCII.GetBytes("second file contents");
    var tarPath = this.CreateOurTar(data1, data2);

    const string script = """
use strict;
use warnings;
use Archive::Tar;
binmode STDOUT;
my $t = Archive::Tar->new;
$t->read($ARGV[0]) or die "read failed";
my @names = sort map { $_->name } $t->get_files;
print join(' ', @names), "\n";
for my $n (@names) {
    print $t->get_content($n);
}
""";
    var result = this.RunScript(perl!, ".pl", script, [tarPath]);
    AssertSuccess(result, "perl Archive::Tar reader");

    var stdout = result.Stdout;
    var newline = Array.IndexOf(stdout, (byte)'\n');
    Assert.That(newline, Is.GreaterThan(0), "no newline after member list");
    var listLine = Encoding.ASCII.GetString(stdout, 0, newline).TrimEnd('\r');
    Assert.That(listLine, Is.EqualTo("one.txt two.txt"));

    var payload = stdout.AsSpan(newline + 1).ToArray();
    var expected = new byte[data1.Length + data2.Length];
    Buffer.BlockCopy(data1, 0, expected, 0, data1.Length);
    Buffer.BlockCopy(data2, 0, expected, data1.Length, data2.Length);
    Assert.That(payload, Is.EqualTo(expected));
  }

  [Test]
  public void Perl_ArchiveTarCreates_WeRead() {
    var perl = FindPerl();
    if (perl == null) Assert.Ignore("perl interpreter not found");
    if (!this.IsPerlModuleAvailable(perl!, "Archive::Tar"))
      Assert.Ignore("Perl Archive::Tar not installed");

    var data = Payload;
    var srcDir = Path.Combine(this._tmpDir, "perl_tar_src");
    Directory.CreateDirectory(srcDir);
    var srcFile = Path.Combine(srcDir, "one.txt");
    File.WriteAllBytes(srcFile, data);
    var tarPath = Path.Combine(this._tmpDir, "perl.tar");

    // Archive::Tar preserves the given path verbatim; run from the source dir so the
    // archive contains just "one.txt" rather than an absolute-ish path.
    const string script = """
use strict;
use warnings;
use Archive::Tar;
chdir $ARGV[0] or die "chdir $ARGV[0]: $!";
my $t = Archive::Tar->new;
$t->add_files('one.txt');
$t->write($ARGV[1]);
""";
    var result = this.RunScript(perl!, ".pl", script, [srcDir, tarPath]);
    AssertSuccess(result, "perl Archive::Tar writer");

    var extractDir = Path.Combine(this._tmpDir, "perl_tar_extract");
    ArchiveOperations.Extract(tarPath, extractDir, null, null);
    var extracted = Path.Combine(extractDir, "one.txt");
    Assert.That(File.Exists(extracted), "one.txt not extracted");
    Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(data));
  }

  [Test]
  public void Ruby_Minitar_ReadsOurTar() {
    var ruby = FindRuby();
    if (ruby == null) Assert.Ignore("ruby interpreter not found");
    if (!this.IsRubyModuleAvailable(ruby!, "minitar"))
      Assert.Ignore("ruby minitar not installed (gem install minitar)");

    var data1 = Payload;
    var data2 = Encoding.ASCII.GetBytes("second file contents");
    var tarPath = this.CreateOurTar(data1, data2);

    const string script = """
require 'minitar'
$stdout.binmode
names = []
chunks = {}
File.open(ARGV[0], 'rb') do |f|
  Minitar::Reader.open(f) do |r|
    r.each_entry do |e|
      names << e.name
      chunks[e.name] = e.read
    end
  end
end
names.sort!
$stdout.write(names.join(' ') + "\n")
names.each { |n| $stdout.write(chunks[n]) }
""";
    var result = this.RunScript(ruby!, ".rb", script, [tarPath]);
    AssertSuccess(result, "ruby minitar reader");

    var stdout = result.Stdout;
    var newline = Array.IndexOf(stdout, (byte)'\n');
    Assert.That(newline, Is.GreaterThan(0), "no newline after member list");
    var listLine = Encoding.ASCII.GetString(stdout, 0, newline).TrimEnd('\r');
    Assert.That(listLine, Is.EqualTo("one.txt two.txt"));

    var payload = stdout.AsSpan(newline + 1).ToArray();
    var expected = new byte[data1.Length + data2.Length];
    Buffer.BlockCopy(data1, 0, expected, 0, data1.Length);
    Buffer.BlockCopy(data2, 0, expected, data1.Length, data2.Length);
    Assert.That(payload, Is.EqualTo(expected));
  }

  // ── Lzop (Perl IO::Uncompress::UnLzop) ─────────────────────────────

  [Test]
  public void Perl_ReadsOurLzop() {
    var perl = FindPerl();
    if (perl == null) Assert.Ignore("perl interpreter not found");
    if (!this.IsPerlModuleAvailable(perl!, "IO::Uncompress::UnLzop"))
      Assert.Ignore("Perl IO::Uncompress::UnLzop not installed (IO-Compress-Lzop)");

    var data = Payload;
    var lzoPath = this.CreateOurStream("Lzop", data, "ours.lzo");

    const string script = """
use strict;
use warnings;
use IO::Uncompress::UnLzop qw(unlzop $UnLzopError);
binmode STDOUT;
my $out;
unlzop($ARGV[0] => \$out) or die "unlzop failed: $UnLzopError";
print $out;
""";
    var result = this.RunScript(perl!, ".pl", script, [lzoPath]);
    AssertSuccess(result, "perl unlzop");
    Assert.That(result.Stdout, Is.EqualTo(data));
  }

  // ── PowerShell tests (Windows only) ────────────────────────────────

  [Test]
  public void PowerShell_ReadsOurZip() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      Assert.Ignore("PowerShell Expand-Archive test is Windows-only");

    var data = Payload;
    var zipPath = CreateOurZip(data, "file1.txt", "ours_ps.zip");
    var extractDir = Path.Combine(this._tmpDir, "ps_extract");

    var script = $"Expand-Archive -Path '{zipPath}' -DestinationPath '{extractDir}' -Force";
    var result = RunPowerShell(script);
    if (result.ExitCode != 0)
      Assert.Fail($"PowerShell Expand-Archive failed: {result.Stderr}");

    var extracted = Path.Combine(extractDir, "file1.txt");
    Assert.That(File.Exists(extracted), "file1.txt not extracted by PowerShell");
    Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(data));
  }

  [Test]
  public void PowerShell_ZipCreates_WeRead() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      Assert.Ignore("PowerShell Compress-Archive test is Windows-only");

    var data = Payload;
    var srcDir = Path.Combine(this._tmpDir, "ps_src");
    Directory.CreateDirectory(srcDir);
    var srcFile = Path.Combine(srcDir, "file1.txt");
    File.WriteAllBytes(srcFile, data);
    var zipPath = Path.Combine(this._tmpDir, "ps_created.zip");

    var script = $"Compress-Archive -Path '{srcFile}' -DestinationPath '{zipPath}' -Force";
    var result = RunPowerShell(script);
    if (result.ExitCode != 0)
      Assert.Fail($"PowerShell Compress-Archive failed: {result.Stderr}");

    var extractDir = Path.Combine(this._tmpDir, "our_extract");
    ArchiveOperations.Extract(zipPath, extractDir, null, null);

    var extracted = Path.Combine(extractDir, "file1.txt");
    Assert.That(File.Exists(extracted), "file1.txt not extracted by our ZIP reader");
    Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(data));
  }

  private static (int ExitCode, string Stdout, string Stderr) RunPowerShell(string script) {
    var psi = new ProcessStartInfo {
      FileName = "powershell.exe",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    psi.ArgumentList.Add("-NoProfile");
    psi.ArgumentList.Add("-ExecutionPolicy");
    psi.ArgumentList.Add("Bypass");
    psi.ArgumentList.Add("-Command");
    psi.ArgumentList.Add(script);

    using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start powershell.exe");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit(30_000);
    return (proc.ExitCode, stdout, stderr);
  }
}
