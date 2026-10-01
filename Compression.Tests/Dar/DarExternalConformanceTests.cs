#pragma warning disable CS1591
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// dar itself as the oracle: archives dar writes — every compression, tape marks or
/// not, block mode, slices — must come back byte for byte from our reader. Skipped when dar is
/// absent (<c>sudo apt-get install -y dar</c> in WSL or on the Linux host).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class DarExternalConformanceTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() => this._tmp = DarFixtures.TempDirectory("ext");

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireDar() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed.");
    if (!FsInteropToolbox.WslHasTool("dar"))
      Assert.Ignore("'dar' is not installed. Run inside WSL (or on the Linux host): `sudo apt-get install -y dar`.");
  }

  private static (string StdOut, string StdErr, int ExitCode) Dar(string arguments) {
    var r = FsInteropToolbox.RunWsl("dar -Q " + arguments);
    TestContext.Out.WriteLine($"dar -Q {arguments} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  /// <summary>Writes the fixture tree (plus an empty directory) under <paramref name="root"/>.</summary>
  private static void WriteTree(string root) {
    foreach (var (path, content) in DarFixtures.TreeFiles) {
      var full = Path.Combine(root, path);
      Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      File.WriteAllBytes(full, content);
    }
    Directory.CreateDirectory(Path.Combine(root, "emptydir"));
  }

  private static void AssertSameTree(string expectedRoot, string actualRoot) {
    var expected = Directory.EnumerateFiles(expectedRoot, "*", SearchOption.AllDirectories)
      .Select(f => Path.GetRelativePath(expectedRoot, f)).Order(StringComparer.Ordinal).ToArray();
    var actual = Directory.EnumerateFiles(actualRoot, "*", SearchOption.AllDirectories)
      .Select(f => Path.GetRelativePath(actualRoot, f)).Order(StringComparer.Ordinal).ToArray();
    Assert.That(actual, Is.EqualTo(expected));
    foreach (var rel in expected)
      Assert.That(File.ReadAllBytes(Path.Combine(actualRoot, rel)), Is.EqualTo(File.ReadAllBytes(Path.Combine(expectedRoot, rel))), rel);
    Assert.That(Directory.Exists(Path.Combine(actualRoot, "emptydir")), Is.True, "the empty directory");
  }

  // ── dar -> ours ─────────────────────────────────────────────────────────────

  [TestCase("", TestName = "GivenDarsDefaultArchive_WhenWeExtractIt_ThenItIsByteIdentical")]
  [TestCase("-at", TestName = "GivenDarArchiveWithoutTapeMarks_WhenWeExtractIt_ThenItIsByteIdentical")]
  [TestCase("-zgzip", TestName = "GivenDarGzipArchive_WhenWeExtractIt_ThenItIsByteIdentical")]
  [TestCase("-zbzip2", TestName = "GivenDarBzip2Archive_WhenWeExtractIt_ThenItIsByteIdentical")]
  [TestCase("-zxz", TestName = "GivenDarXzArchive_WhenWeExtractIt_ThenItIsByteIdentical")]
  [TestCase("-zzstd", TestName = "GivenDarZstdArchive_WhenWeExtractIt_ThenItIsByteIdentical")]
  [TestCase("-zlz4", TestName = "GivenDarLz4Archive_WhenWeExtractIt_ThenItIsByteIdentical")]
  [TestCase("-zlzo", TestName = "GivenDarLzoArchive_WhenWeExtractIt_ThenItIsByteIdentical")]
  [TestCase("-zzstd:6:16k", TestName = "GivenDarBlockCompressedArchive_WhenWeExtractIt_ThenItIsByteIdentical")]
  [TestCase("-zgzip -m 0", TestName = "GivenDarArchiveCompressingEvenTinyFiles_WhenWeExtractIt_ThenItIsByteIdentical")]
  [TestCase("-s 16k", TestName = "GivenDarSlicedArchive_WhenWeExtractIt_ThenItIsByteIdentical")]
  [TestCase("-S 20k -s 9k -zgzip", TestName = "GivenDarSlicedCompressedArchiveWithABiggerFirstSlice_WhenWeExtractIt_ThenItIsByteIdentical")]
  public void DarArchivesExtract(string options) {
    RequireDar();
    var src = Path.Combine(this._tmp, "src");
    WriteTree(src);
    // A bigger incompressible file so that -s 16k really spans several slices.
    var noise = new byte[40_000];
    new Random(3).NextBytes(noise);
    File.WriteAllBytes(Path.Combine(src, "dir", "noise40k.bin"), noise);

    var basePath = Path.Combine(this._tmp, "theirs");
    var create = Dar($"-c {FsInteropToolbox.WinToWsl(basePath)} {options} -R {FsInteropToolbox.WinToWsl(src)}");
    Assert.That(create.ExitCode, Is.EqualTo(0), $"dar -c failed:\n{create.StdOut}\n{create.StdErr}");
    var slices = Enumerable.Range(1, 1000).Select(n => $"{basePath}.{n}.dar").TakeWhile(File.Exists).ToArray();
    Assert.That(slices, Is.Not.Empty);
    if (options.Contains("-s "))
      Assert.That(slices, Has.Length.GreaterThan(1), "the fixture should span several slices");

    // Open the LAST slice: that is where dar keeps the catalogue, and the reader has to find the rest.
    var outDir = Path.Combine(this._tmp, "out");
    using (var fs = File.OpenRead(slices[^1]))
      new DarFormatDescriptor().Extract(fs, outDir, null, null);
    AssertSameTree(src, outDir);
  }

  [TestCase("", TestName = "GivenOneSliceFromDar_WhenItsHeaderIsParsed_ThenItIsTheLastSlice")]
  [TestCase("-s 16k", TestName = "GivenSeveralSlicesFromDar_WhenEachHeaderIsParsed_ThenOnlyTheFinalOneIsLast")]
  public void DarSliceHeadersParse(string slicing) {
    RequireDar();
    var src = Path.Combine(this._tmp, "src");
    Directory.CreateDirectory(src);
    var noise = new byte[40_000];
    new Random(3).NextBytes(noise);
    File.WriteAllBytes(Path.Combine(src, "noise.bin"), noise);

    var basePath = Path.Combine(this._tmp, "set");
    Assert.That(Dar($"-c {FsInteropToolbox.WinToWsl(basePath)} {slicing} -R {FsInteropToolbox.WinToWsl(src)}").ExitCode, Is.EqualTo(0));
    var slices = Enumerable.Range(1, 1000).Select(n => $"{basePath}.{n}.dar").TakeWhile(File.Exists).ToArray();
    Assert.That(slices, Is.Not.Empty);
    for (var i = 0; i < slices.Length; ++i) {
      using var fs = File.OpenRead(slices[i]);
      var h = DarSliceSet.ReadHeader(fs);
      Assert.That(h.IsValid, Is.True, $"{Path.GetFileName(slices[i])}: {h.Problem}");
      Assert.That(h.IsLastSlice, Is.EqualTo(i == slices.Length - 1), Path.GetFileName(slices[i]));
      if (slicing.Length > 0)
        Assert.That(h.SliceSize, Is.EqualTo(16384));
    }
  }
}
