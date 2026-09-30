#pragma warning disable CS1591
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// The slice header parser has to agree with whatever <c>dar</c> is installed, not only with the
/// 2.8.6 slices checked in under <c>ReferenceVectors</c>. Skipped when <c>dar</c> is absent.
/// </summary>
[TestFixture]
[Category("ExternalTool")]
public class DarExternalConformanceTests {
  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_dar_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireDar() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed.");
    if (!FsInteropToolbox.WslHasTool("dar"))
      Assert.Ignore("'dar' is not installed. Run inside WSL (or on the Linux host): `sudo apt install -y dar`.");
  }

  private static string Metadata(string slice, string outDir) {
    using var s = File.OpenRead(slice);
    new DarFormatDescriptor().Extract(s, outDir, null, ["metadata.ini"]);
    return File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
  }

  [TestCase("", TestName = "GivenDarWritesOneSlice_WhenParsed_ThenItIsTheLastSlice")]
  [TestCase("-s 16k", TestName = "GivenDarWritesSeveralSlices_WhenEachIsParsed_ThenOnlyTheFinalOneIsLast")]
  public void DarSlicesParse(string slicing) {
    RequireDar();
    var src = Path.Combine(this._tmpDir, "src");
    Directory.CreateDirectory(src);
    var noise = new byte[40_000];
    new Random(3).NextBytes(noise);
    File.WriteAllBytes(Path.Combine(src, "noise.bin"), noise);
    File.WriteAllText(Path.Combine(src, "hello.txt"), "hello dar\n");

    var basePath = FsInteropToolbox.WinToWsl(Path.Combine(this._tmpDir, "set"));
    var r = FsInteropToolbox.RunWsl($"dar -Q -c {basePath} {slicing} -R {FsInteropToolbox.WinToWsl(src)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"dar failed:\n{r.StdOut}\n{r.StdErr}");

    var slices = Enumerable.Range(1, int.MaxValue)
      .Select(n => Path.Combine(this._tmpDir, $"set.{n}.dar"))
      .TakeWhile(File.Exists)
      .ToArray();
    Assert.That(slices, Is.Not.Empty);
    if (slicing.Length > 0)
      Assert.That(slices, Has.Length.GreaterThan(1), "the fixture should span several slices");

    for (var i = 0; i < slices.Length; ++i) {
      var meta = Metadata(slices[i], Path.Combine(this._tmpDir, "x" + i));
      Assert.That(meta, Does.Contain("parse_status=ok"), $"{Path.GetFileName(slices[i])}:\n{meta}");
      Assert.That(meta, Does.Contain($"last_slice={(i == slices.Length - 1 ? "yes" : "no")}"), $"{Path.GetFileName(slices[i])}:\n{meta}");
      if (slicing.Length > 0)
        Assert.That(meta, Does.Contain("slice_size=16384"));
    }
  }
}
