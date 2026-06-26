#pragma warning disable CS1591

using System.Collections.Generic;
using System.Diagnostics;
using Compression.Analysis.ExternalTools;
using Compression.Registry;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// External-tool acceptance gate for the genuine in-place 7z removal
/// (<see cref="SevenZipInPlaceRemover"/>): after an in-place remove, the official
/// 7-Zip CLI must accept the archive (<c>7z t</c> → "Everything is Ok") and list
/// exactly the survivors (<c>7z l</c>). A proper-subset-of-a-solid-block removal is
/// also exercised: it must fall back to the verified rebuild and still produce a
/// tool-valid archive. Gated under the <c>EndToEnd</c> category (excluded from core
/// CI) and skipped cleanly when 7z is not installed.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
public class SevenZipInPlaceRemoveOracleTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_7z_rm_{Guid.NewGuid():N}");
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
  public void RemoveOnlyFile_InPlace_OracleAccepts() {
    var sevenZip = Require7z();
    var archive = BuildArchive(0, [("solo.txt", Enc("the only file"), false)]);
    var result = RemoveInPlace(archive, "solo.txt");
    var path = Write(result);
    AssertOk(sevenZip, path);
  }

  [Test]
  public void RemoveOneWholeFolderOfSeveral_InPlace_OracleListsSurvivors() {
    var sevenZip = Require7z();
    var a = RandomBytes(3000, 1);
    var b = RandomBytes(4000, 2);
    var c = RandomBytes(3500, 3);
    // One folder per file (maxBlockSize=1).
    var archive = BuildArchive(1,
      [("a.dat", a, false), ("b.dat", b, false), ("c.dat", c, false)]);
    var result = RemoveInPlace(archive, "b.dat");
    var path = Write(result);

    AssertOk(sevenZip, path);
    var list = RunTool(sevenZip, $"l \"{path}\"");
    Assert.That(list.StdOut, Does.Contain("a.dat"));
    Assert.That(list.StdOut, Does.Contain("c.dat"));
    Assert.That(list.StdOut, Does.Not.Contain("b.dat"), "removed file must not be listed");
  }

  [Test]
  public void RemoveWholeMultiFileFolder_InPlace_OracleListsSurvivors() {
    var sevenZip = Require7z();
    var x = RandomBytes(1500, 4);
    var y = RandomBytes(1500, 5);
    var z = RandomBytes(5000, 6);
    // {x,y} grouped into one folder (block 3000 ≤ 4000), z alone.
    var archive = BuildArchive(4000,
      [("g/x.dat", x, false), ("g/y.dat", y, false), ("h/z.dat", z, false)]);
    var result = RemoveInPlace(archive, "g/x.dat", "g/y.dat");
    var path = Write(result);

    AssertOk(sevenZip, path);
    var list = RunTool(sevenZip, $"l \"{path}\"");
    Assert.That(list.StdOut, Does.Contain("z.dat"));
    Assert.That(list.StdOut, Does.Not.Contain("x.dat"));
    Assert.That(list.StdOut, Does.Not.Contain("y.dat"));
  }

  [Test]
  public void RemoveSubsetOfSolidBlock_FallsBackToRebuild_StillOracleValid() {
    var sevenZip = Require7z();
    var x = RandomBytes(2000, 7);
    var y = RandomBytes(2000, 8);
    // Single solid folder; removing one member forces the rebuild fallback.
    var archive = BuildArchive(0,
      [("solid/x.dat", x, false), ("solid/y.dat", y, false)]);

    using var stream = new MemoryStream();
    stream.Write(archive, 0, archive.Length);
    stream.Position = 0;
    new SevenZipFormatDescriptor().Remove(stream, ["solid/x.dat"]);
    var path = Write(stream.ToArray());

    AssertOk(sevenZip, path);
    var list = RunTool(sevenZip, $"l \"{path}\"");
    Assert.That(list.StdOut, Does.Contain("y.dat"));
    Assert.That(list.StdOut, Does.Not.Contain("x.dat"));
  }

  // ── Harness ──────────────────────────────────────────────────────────────

  private static byte[] BuildArchive(long maxBlockSize,
      IReadOnlyList<(string Name, byte[] Data, bool Dir)> entries) {
    var ms = new MemoryStream();
    using (var w = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true)) {
      foreach (var (name, data, dir) in entries) {
        if (dir) w.AddDirectory(name);
        else w.AddEntry(new SevenZipEntry { Name = name, Size = data.Length }, data);
      }
      w.Finish(maxThreads: 1, maxBlockSize: maxBlockSize);
    }
    return ms.ToArray();
  }

  private static byte[] RemoveInPlace(byte[] original, params string[] names) {
    using var work = new MemoryStream();
    work.Write(original, 0, original.Length);
    work.Position = 0;
    SevenZipInPlaceRemover.Remove(work, names);
    return work.ToArray();
  }

  private string Write(byte[] bytes) {
    var path = Path.Combine(this._tmpDir, $"a_{Guid.NewGuid():N}.7z");
    File.WriteAllBytes(path, bytes);
    return path;
  }

  private static void AssertOk(string sevenZip, string path) {
    var test = RunTool(sevenZip, $"t \"{path}\"");
    Assert.That(test.StdOut, Does.Contain("Everything is Ok"),
      $"7z t must report success.\n{test.StdOut}\n{test.StdErr}");
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
