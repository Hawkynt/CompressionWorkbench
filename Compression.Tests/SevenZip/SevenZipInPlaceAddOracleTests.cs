#pragma warning disable CS1591

using System.Collections.Generic;
using System.Diagnostics;
using Compression.Analysis.ExternalTools;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// External-tool acceptance gate for the genuine in-place 7z append
/// (<see cref="SevenZipInPlaceAdder"/>): after an in-place add, the official 7-Zip
/// CLI must accept the archive (<c>7z t</c> → "Everything is Ok") and list both the
/// pre-existing and the newly appended entries (<c>7z l</c>). Gated under the
/// <c>EndToEnd</c> category (excluded from core CI) and skipped cleanly when 7z is
/// not installed, so the bundled reader-round-trip proof never needs the tool.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
public class SevenZipInPlaceAddOracleTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_7z_inplace_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static string Require7z()
    => ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za")
       ?? throw new IgnoreException("7z not found on PATH or in common locations");

  [Test]
  public void SingleFileArchive_AddInPlace_OracleAcceptsAndLists() => RunOracle(
    SevenZipCodec.Lzma2,
    seed: [("readme.txt", Enc("seed content here"), false)],
    add: [("appended.txt", Enc("appended in place no repack"), false)]);

  [Test]
  public void SolidBlock_AddInPlace_OracleAcceptsAndLists() {
    var d1 = new byte[4000]; System.Array.Fill(d1, (byte)'X');
    var d2 = new byte[6000]; new System.Random(3).NextBytes(d2);
    RunOracle(SevenZipCodec.Lzma2,
      seed: [("solid/a.dat", d1, false), ("solid/b.dat", d2, false)],
      add: [("more/c.bin", RandomBytes(2500, 4), false), ("more/d.txt", Enc("new text"), false)]);
  }

  [Test]
  public void CopyCodec_AddInPlace_OracleAcceptsAndLists() => RunOracle(
    SevenZipCodec.Copy,
    seed: [("stored.bin", Enc("stored verbatim"), false)],
    add: [("added.bin", Enc("freshly appended block"), false)]);

  [Test]
  public void WithDirectories_AddInPlace_OracleAcceptsAndLists() => RunOracle(
    SevenZipCodec.Lzma2,
    seed: [("d1", [], true), ("d1/inside.txt", Enc("inside d1"), false)],
    add: [("d2", [], true), ("d2/new.txt", Enc("new under d2"), false)]);

  [Test]
  public void ManyFileSolidBlock_AddInPlace_OracleAcceptsAndLists() {
    var rng = new System.Random(55);
    var seed = new List<(string, byte[], bool)>();
    for (var i = 0; i < 10; i++)
      seed.Add(($"pack/f{i:D2}.dat", RandomBytes(400 + i * 20, i + 1), false));
    var add = new List<(string, byte[], bool)>();
    for (var i = 0; i < 6; i++)
      add.Add(($"new/g{i:D2}.dat", RandomBytes(250 + i * 10, 100 + i), false));
    RunOracle(SevenZipCodec.Lzma2, seed, add);
  }

  // ── Harness ──────────────────────────────────────────────────────────────

  private void RunOracle(SevenZipCodec codec,
      IReadOnlyList<(string Name, byte[] Data, bool Dir)> seed,
      IReadOnlyList<(string Name, byte[] Data, bool Dir)> add) {
    var sevenZip = Require7z();

    var ms = new MemoryStream();
    using (var w = new SevenZipWriter(ms, codec, leaveOpen: true)) {
      foreach (var (name, data, dir) in seed) {
        if (dir) w.AddDirectory(name);
        else w.AddEntry(new SevenZipEntry { Name = name, Size = data.Length }, data);
      }
      w.Finish();
    }
    var original = ms.ToArray();

    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    SevenZipInPlaceAdder.Add(work, [.. add.Select(a => (a.Name, a.Data, a.Dir))]);
    var result = work.ToArray();

    var path = Path.Combine(this._tmpDir, $"a_{Guid.NewGuid():N}.7z");
    File.WriteAllBytes(path, result);

    // ── 7z t: integrity ──
    var test = RunTool(sevenZip, $"t \"{path}\"");
    Assert.That(test.StdOut, Does.Contain("Everything is Ok"),
      $"7z t must report success.\n{test.StdOut}\n{test.StdErr}");

    // ── 7z l: must list every expected file (old + new) ──
    var list = RunTool(sevenZip, $"l \"{path}\"");
    foreach (var (name, _, dir) in seed.Concat(add)) {
      if (dir) continue;
      var leaf = name.Replace('/', Path.DirectorySeparatorChar);
      Assert.That(list.StdOut.Contains(name) || list.StdOut.Contains(leaf), Is.True,
        $"7z l must list '{name}'.\n{list.StdOut}");
    }
  }

  private static byte[] Enc(string s) => System.Text.Encoding.UTF8.GetBytes(s);

  private static byte[] RandomBytes(int len, int seed) {
    var b = new byte[len];
    new System.Random(seed).NextBytes(b);
    return b;
  }

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
