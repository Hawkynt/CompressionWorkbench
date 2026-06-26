#pragma warning disable CS1591

using System.Collections.Generic;
using System.Diagnostics;
using Compression.Analysis.ExternalTools;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// External-tool acceptance gate for the genuine in-place RAR5 removal
/// (<see cref="RarInPlaceRemover"/>): after an in-place remove, the official unrar
/// CLI must test the archive clean (<c>unrar t</c> → "All OK") and the 7-Zip CLI
/// must list exactly the survivors. A removal targeting a solid run is also
/// exercised: it must fall back to the verified rebuild and still produce a
/// tool-valid archive. Gated under the <c>EndToEnd</c> category (excluded from core
/// CI) and skipped cleanly when neither tool is installed.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
public class RarInPlaceRemoveOracleTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_rar_rm_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static string? Unrar() => ToolDiscovery.GetToolPath("unrar");
  private static string? SevenZip() => ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");

  [Test]
  public void RemoveOnlyFile_InPlace_OracleAccepts() {
    var (unrar, sevenZip) = RequireTool();
    var archive = BuildArchive(RarConstants.MethodStore, solid: false,
      [("solo.txt", Enc("the only file"))]);
    var result = RemoveInPlace(archive, "solo.txt");
    var path = Write(result);

    // The result is a valid but empty archive. unrar exits 10 with "No files to
    // extract" on an empty archive — that is acceptance of a well-formed container,
    // not a corruption — so assert that outcome rather than "All OK".
    if (unrar != null) {
      var test = RunToolRaw(unrar, $"t \"{path}\"");
      Assert.That(test.StdOut, Does.Contain("No files to extract").Or.Contain("All OK"),
        $"unrar must accept the empty archive.\n{test.StdOut}\n{test.StdErr}");
    }
    if (sevenZip != null) {
      var test = RunTool(sevenZip, $"t \"{path}\"");
      Assert.That(test.StdOut, Does.Contain("Everything is Ok"),
        $"7z t must accept the empty archive.\n{test.StdOut}\n{test.StdErr}");
    }
  }

  [Test]
  public void RemoveOneOfSeveral_NonSolid_OracleListsSurvivors() {
    var (unrar, sevenZip) = RequireTool();
    var rng = new System.Random(11);
    var a = new byte[1000]; rng.NextBytes(a);
    var b = new byte[1200]; rng.NextBytes(b);
    var c = new byte[900]; rng.NextBytes(c);
    var archive = BuildArchive(RarConstants.MethodStore, solid: false,
      [("a.dat", a), ("b.dat", b), ("c.dat", c)]);
    var result = RemoveInPlace(archive, "b.dat");
    var path = Write(result);
    AssertOk(unrar, sevenZip, path, survivors: ["a.dat", "c.dat"], removed: ["b.dat"]);
  }

  [Test]
  public void RemoveOneOfSeveral_NonSolidCompressed_OracleListsSurvivors() {
    var (unrar, sevenZip) = RequireTool();
    var a = new byte[4000];
    for (var i = 0; i < a.Length; ++i) a[i] = (byte)(i % 26 + 'A');
    var b = new byte[3000];
    for (var i = 0; i < b.Length; ++i) b[i] = (byte)(i % 7);
    var archive = BuildArchive(RarConstants.MethodNormal, solid: false,
      [("packed/a.bin", a), ("packed/b.bin", b)]);
    var result = RemoveInPlace(archive, "packed/a.bin");
    var path = Write(result);
    AssertOk(unrar, sevenZip, path, survivors: ["b.bin"], removed: ["a.bin"]);
  }

  [Test]
  public void RemoveSolidFile_FallsBackToRebuild_StillOracleValid() {
    var (unrar, sevenZip) = RequireTool();
    var d1 = new byte[2000];
    var d2 = new byte[2000];
    for (var i = 0; i < d1.Length; ++i) { d1[i] = (byte)(i % 10); d2[i] = (byte)(i % 10); }
    var archive = BuildArchive(RarConstants.MethodNormal, solid: true,
      [("solid/a.bin", d1), ("solid/b.bin", d2)]);

    using var stream = new MemoryStream();
    stream.Write(archive, 0, archive.Length);
    stream.Position = 0;
    new RarFormatDescriptor().Remove(stream, ["solid/a.bin"]);
    var path = Write(stream.ToArray());
    AssertOk(unrar, sevenZip, path, survivors: ["b.bin"], removed: ["a.bin"]);
  }

  // ── Harness ──────────────────────────────────────────────────────────────

  private static (string? Unrar, string? SevenZip) RequireTool() {
    var unrar = Unrar();
    var sevenZip = SevenZip();
    if (unrar == null && sevenZip == null)
      throw new IgnoreException("neither unrar nor 7z found on PATH or in common locations");
    return (unrar, sevenZip);
  }

  private static byte[] BuildArchive(int method, bool solid,
      IReadOnlyList<(string Name, byte[] Data)> entries) {
    var ms = new MemoryStream();
    using (var w = new RarWriter(ms, leaveOpen: true, method: method, solid: solid)) {
      foreach (var (name, data) in entries)
        w.AddFile(name, data);
      w.Finish();
    }
    return ms.ToArray();
  }

  private static byte[] RemoveInPlace(byte[] original, params string[] names) {
    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    RarInPlaceRemover.Remove(work, names);
    return work.ToArray();
  }

  private string Write(byte[] bytes) {
    var path = Path.Combine(this._tmpDir, $"a_{Guid.NewGuid():N}.rar");
    File.WriteAllBytes(path, bytes);
    return path;
  }

  private static void AssertOk(string? unrar, string? sevenZip, string path,
      string[] survivors, string[] removed) {
    if (unrar != null) {
      var test = RunTool(unrar, $"t \"{path}\"");
      Assert.That(test.StdOut, Does.Contain("All OK"),
        $"unrar t must report 'All OK'.\n{test.StdOut}\n{test.StdErr}");
    }
    if (sevenZip != null) {
      var test = RunTool(sevenZip, $"t \"{path}\"");
      Assert.That(test.StdOut, Does.Contain("Everything is Ok"),
        $"7z t must report success.\n{test.StdOut}\n{test.StdErr}");
      var list = RunTool(sevenZip, $"l \"{path}\"");
      foreach (var name in survivors)
        Assert.That(list.StdOut, Does.Contain(name), $"7z l must list survivor '{name}'.\n{list.StdOut}");
      foreach (var name in removed)
        Assert.That(list.StdOut, Does.Not.Contain(name), $"7z l must not list removed '{name}'.\n{list.StdOut}");
    }
  }

  private static byte[] Enc(string s) => System.Text.Encoding.UTF8.GetBytes(s);

  private static (string StdOut, string StdErr) RunTool(string toolPath, string args, int timeoutMs = 60_000) {
    var (stdout, stderr, exit) = RunToolRaw(toolPath, args, timeoutMs);
    if (exit != 0)
      Assert.Fail($"{Path.GetFileName(toolPath)} exited with {exit}.\nArgs: {args}\nstdout: {stdout}\nstderr: {stderr}");
    return (stdout, stderr);
  }

  /// <summary>Runs a tool and returns its output without asserting on the exit code.</summary>
  private static (string StdOut, string StdErr, int Exit) RunToolRaw(string toolPath, string args, int timeoutMs = 60_000) {
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
      Assert.Fail($"{Path.GetFileName(toolPath)} timed out after {timeoutMs}ms.");
    }
    return (stdout, stderr, proc.ExitCode);
  }
}
