using System.Diagnostics;
using System.Text;
using FileFormat.FreeArc;

namespace Compression.Tests.FreeArc;

/// <summary>
/// Live round trips against FreeArc 0.67 itself: its <c>unarc</c> and <c>arc</c> must test and
/// extract what our writer makes byte-identically, and we must extract what <c>arc</c> makes.
/// </summary>
/// <remarks>
/// The tools are located as <c>$FREEARC_UNARC</c> / <c>$FREEARC_ARC</c> (paths), else as
/// <c>unarc.exe</c> / <c>Arc.exe</c> on the Windows PATH. Both ship in the <c>bin</c> folder of
/// FreeArc 0.67 (2014-03-15), e.g. the self-extracting <c>FreeArc-console-0.67-alpha-win32.exe</c>
/// (run it with <c>-x -y -d&lt;dir&gt;</c>). FreeArc is freeware without published sources for
/// 0.67, so it is used only as a black box. Without the tools every test here is ignored.
/// </remarks>
[TestFixture]
[Category("ExternalInterop")]
public sealed class FreeArcOracleTests {
  private static readonly Lazy<string?> _unarc = new(() => Locate("FREEARC_UNARC", "unarc.exe"));
  private static readonly Lazy<string?> _arc = new(() => Locate("FREEARC_ARC", "Arc.exe"));
  private string _tmp = null!;

  [SetUp]
  public void Setup() {
    this._tmp = Path.Combine(Path.GetTempPath(), $"cwb_freearc_oracle_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static (string Name, byte[] Data)[] Files() {
    var big = new byte[300_000];
    for (var i = 0; i < big.Length; ++i)
      big[i] = (byte)((i * 2654435761u >> 13) % 7 == 0 ? i : i >> 4);
    return [
      ("hello.txt", FreeArcTests.Hello),
      ("sub/lines.txt", FreeArcTests.Lines()),
      ("empty.bin", []),
      ("dир/файл.txt", "abc"u8.ToArray()),
      ("a/b/big.bin", big),
    ];
  }

  [TestCase("storing")]
  [TestCase("lzma")]
  [TestCase("lzma:1mb:lc4:lp1:pb1")]
  public void OurArchive_UnarcTestsAndExtractsByteIdentical(string method) {
    var unarc = Require(_unarc, "unarc");
    var archive = this.WriteOurs(method);

    var test = Run(unarc, ["t", archive]);
    Assert.That(test.ExitCode, Is.Zero, test.Output);
    Assert.That(test.Output, Does.Contain("All OK").Or.Contain("Testing"), test.Output);

    var outDir = Path.Combine(this._tmp, "x");
    var extract = Run(unarc, ["x", "-o+", $"-dp{outDir}", archive]);
    Assert.That(extract.ExitCode, Is.Zero, extract.Output);
    this.AssertExtracted(outDir);
  }

  [TestCase("storing")]
  [TestCase("lzma")]
  public void OurArchive_ArcListsTestsAndExtractsByteIdentical(string method) {
    var arc = Require(_arc, "Arc");
    var archive = this.WriteOurs(method);

    var test = Run(arc, ["t", "-cfg-", archive]);
    Assert.That(test.ExitCode, Is.Zero, test.Output);
    var list = Run(arc, ["lb", "-cfg-", archive]);
    Assert.That(list.ExitCode, Is.Zero, list.Output);
    Assert.That(list.Output, Does.Contain("big.bin"));

    var outDir = Path.Combine(this._tmp, "x");
    var extract = Run(arc, ["x", "-cfg-", "-y", $"-dp{outDir}", archive]);
    Assert.That(extract.ExitCode, Is.Zero, extract.Output);
    this.AssertExtracted(outDir);
  }

  [TestCase("-m0")]
  [TestCase("-mlzma")]
  [TestCase("-mlzma:lc4:lp1:pb1")]
  [TestCase("-m5x")]
  public void ArcArchive_WeExtractByteIdentical(string method) {
    var arc = Require(_arc, "Arc");
    var src = Path.Combine(this._tmp, "src");
    foreach (var (name, data) in Files()) {
      var path = Path.Combine(src, name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, data);
    }
    Directory.CreateDirectory(Path.Combine(src, "emptydir"));

    var archive = Path.Combine(this._tmp, "theirs.arc");
    var create = Run(arc, ["a", "-cfg-", "-r", method, archive, "*"], src);
    Assert.That(create.ExitCode, Is.Zero, create.Output);

    using var r = new FreeArcReader(File.OpenRead(archive));
    if (r.Entries.Any(e => !e.IsDirectory && !FreeArcMethods.IsSupported(e.Method)))
      Assert.Inconclusive($"{method} chose methods we do not decode: {string.Join(", ", r.Entries.Select(e => e.Method).Distinct())}");
    foreach (var (name, data) in Files())
      Assert.That(r.Extract(r.Entries.Single(e => e.Name == name)), Is.EqualTo(data), name);
    Assert.That(r.Entries.Single(e => e.Name == "emptydir").IsDirectory, Is.True);
  }

  private string WriteOurs(string method) {
    var w = new FreeArcWriter(method);
    w.AddDirectory("emptydir");
    w.AddDirectory("dир");
    foreach (var (name, data) in Files())
      w.AddFile(name, data);
    var path = Path.Combine(this._tmp, "ours.arc");
    File.WriteAllBytes(path, w.Build());
    return path;
  }

  private void AssertExtracted(string outDir) {
    Assert.Multiple(() => {
      foreach (var (name, data) in Files())
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(data), name);
      Assert.That(Directory.Exists(Path.Combine(outDir, "emptydir")), Is.True);
    });
  }

  private static string Require(Lazy<string?> tool, string name) {
    if (tool.Value is null)
      Assert.Ignore($"FreeArc's {name} not found: set FREEARC_{name.ToUpperInvariant()} or put it on PATH (see the class remarks).");
    return tool.Value!;
  }

  private static string? Locate(string variable, string executable) {
    var configured = Environment.GetEnvironmentVariable(variable);
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
      return configured;
    if (!OperatingSystem.IsWindows())
      return null;
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
      var candidate = Path.Combine(dir.Trim(), executable);
      if (File.Exists(candidate))
        return candidate;
    }
    return null;
  }

  private static (int ExitCode, string Output) Run(string executable, string[] arguments, string? workingDirectory = null) {
    var start = new ProcessStartInfo(executable) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable)!,
      StandardOutputEncoding = Encoding.UTF8,
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdOut = process.StandardOutput.ReadToEndAsync();
    var stdErr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(120_000)) {
      process.Kill(entireProcessTree: true);
      return (-1, "timed out");
    }
    return (process.ExitCode, stdOut.Result + stdErr.Result);
  }
}
