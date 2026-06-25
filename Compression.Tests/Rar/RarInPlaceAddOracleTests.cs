#pragma warning disable CS1591

using System.Collections.Generic;
using System.Diagnostics;
using Compression.Analysis.ExternalTools;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// External-tool acceptance gate for the genuine in-place RAR5 append
/// (<see cref="RarInPlaceAdder"/>): after an in-place add, the official unrar CLI
/// must test the archive clean (<c>unrar t</c> → "All OK") and the 7-Zip CLI must
/// list and verify both the pre-existing and the newly appended entries
/// (<c>7z l</c> / <c>7z t</c>). Gated under the <c>EndToEnd</c> category (excluded
/// from core CI) and skipped cleanly when neither tool is installed, so the bundled
/// reader-round-trip proof never needs an external tool.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
public class RarInPlaceAddOracleTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_rar_inplace_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static string? Unrar() => ToolDiscovery.GetToolPath("unrar");
  private static string? SevenZip() => ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");

  [Test]
  public void SingleFileArchive_AddInPlace_OracleAcceptsAndLists() => RunOracle(
    seedMethod: RarConstants.MethodStore, seedSolid: false,
    seed: [("readme.txt", Enc("seed content here"))],
    addMethod: RarConstants.MethodStore,
    add: [("appended.txt", Enc("appended in place no repack"))]);

  [Test]
  public void SeveralFiles_AddSeveral_OracleAcceptsAndLists() {
    var rng = new System.Random(7);
    var seed = new List<(string, byte[])>();
    for (var i = 0; i < 4; i++) {
      var d = new byte[400 + i * 50]; rng.NextBytes(d);
      seed.Add(($"pack/f{i:D2}.dat", d));
    }
    var add = new List<(string, byte[])>();
    for (var i = 0; i < 3; i++) {
      var d = new byte[250 + i * 30]; rng.NextBytes(d);
      add.Add(($"new/g{i:D2}.dat", d));
    }
    RunOracle(RarConstants.MethodStore, false, seed, RarConstants.MethodStore, add);
  }

  [Test]
  public void SolidArchive_AppendNonSolid_OracleAcceptsAndExistingExtract() {
    var d1 = new byte[2000];
    var d2 = new byte[2000];
    for (var i = 0; i < d1.Length; ++i) { d1[i] = (byte)(i % 10); d2[i] = (byte)(i % 10); }
    RunOracle(RarConstants.MethodNormal, true,
      [("solid/a.bin", d1), ("solid/b.bin", d2)],
      RarConstants.MethodStore,
      [("c.txt", Enc("appended non-solid after a solid run"))]);
  }

  [Test]
  public void StoreCodecArchive_AddInPlace_OracleAcceptsAndLists() => RunOracle(
    seedMethod: RarConstants.MethodStore, seedSolid: false,
    seed: [("stored1.bin", Enc("stored verbatim one")), ("stored2.bin", Enc("stored verbatim two"))],
    addMethod: RarConstants.MethodStore,
    add: [("added.bin", Enc("freshly appended block"))]);

  // ── Harness ──────────────────────────────────────────────────────────────

  private void RunOracle(int seedMethod, bool seedSolid,
      IReadOnlyList<(string Name, byte[] Data)> seed,
      int addMethod,
      IReadOnlyList<(string Name, byte[] Data)> add) {
    var unrar = Unrar();
    var sevenZip = SevenZip();
    if (unrar == null && sevenZip == null)
      throw new IgnoreException("neither unrar nor 7z found on PATH or in common locations");

    var ms = new MemoryStream();
    using (var w = new RarWriter(ms, leaveOpen: true, method: seedMethod, solid: seedSolid)) {
      foreach (var (name, data) in seed)
        w.AddFile(name, data);
      w.Finish();
    }
    var original = ms.ToArray();

    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    RarInPlaceAdder.Add(work,
      [.. add.Select(a => (a.Name, a.Data, (System.DateTimeOffset?)null))], addMethod);
    var result = work.ToArray();

    var path = Path.Combine(this._tmpDir, $"a_{Guid.NewGuid():N}.rar");
    File.WriteAllBytes(path, result);

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
      foreach (var (name, _) in seed.Concat(add)) {
        var leaf = name.Replace('/', Path.DirectorySeparatorChar);
        Assert.That(list.StdOut.Contains(name) || list.StdOut.Contains(leaf), Is.True,
          $"7z l must list '{name}'.\n{list.StdOut}");
      }
    }
  }

  private static byte[] Enc(string s) => System.Text.Encoding.UTF8.GetBytes(s);

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
      Assert.Fail($"{Path.GetFileName(toolPath)} timed out after {timeoutMs}ms.");
    }
    if (proc.ExitCode != 0)
      Assert.Fail($"{Path.GetFileName(toolPath)} exited with {proc.ExitCode}.\nArgs: {args}\nstdout: {stdout}\nstderr: {stderr}");
    return (stdout, stderr);
  }
}
