#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// dar itself as the oracle, both ways: archives we write — every compression, tape marks or not,
/// one slice or many, streamed or not, symlinks and inode metadata — must pass <c>dar -t</c> (and
/// <c>dar -t --sequential-read</c> when they carry tape marks) and come back byte for byte from
/// <c>dar -x</c>; archives dar writes must come back byte for byte from our reader. Skipped when dar
/// is absent (<c>sudo apt-get install -y dar</c> in WSL or on the Linux host). <c>CWB_DAR</c> names
/// another dar binary to judge with, such as one built from a newer release.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class DarExternalConformanceTests {
  private string _tmp = null!;

  private static readonly string DarBinary = Environment.GetEnvironmentVariable("CWB_DAR") is { Length: > 0 } custom ? custom : "dar";

  [SetUp]
  public void SetUp() => this._tmp = DarFixtures.TempDirectory("ext");

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireDar() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed.");
    if (!FsInteropToolbox.WslHasTool(DarBinary))
      Assert.Ignore($"'{DarBinary}' is not installed. Run inside WSL (or on the Linux host): `sudo apt-get install -y dar`.");
  }

  private static (string StdOut, string StdErr, int ExitCode) Dar(string arguments) {
    var r = FsInteropToolbox.RunWsl($"{DarBinary} -Q {arguments}");
    TestContext.Out.WriteLine($"{DarBinary} -Q {arguments} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  private static (string StdOut, string StdErr, int ExitCode) Shell(string command) {
    var r = FsInteropToolbox.RunWsl(command);
    TestContext.Out.WriteLine($"{command} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
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

  private static IEnumerable<TestCaseData> MethodsWithAndWithoutTapeMarks()
    => DarWriterTests.Methods.SelectMany(m => new[] {
      new TestCaseData(m, false).SetName($"GivenOur{m}Archive_WhenDarTestsListsAndExtractsIt_ThenItIsAcceptedAndByteIdentical"),
      new TestCaseData(m, true).SetName($"GivenOur{m}ArchiveWithTapeMarks_WhenDarReadsItDirectlyAndSequentially_ThenItIsAcceptedAndByteIdentical"),
    });

  /// <summary>Asks dar to test, list and restore the archive at <paramref name="basePath"/> and
  /// compares the restored tree with <paramref name="src"/>. Sequential reading is asked for
  /// archives with tape marks in one slice: on a slice set dar's sequential mode asks for a slice
  /// after the last one and gives up under <c>-Q</c>, for sets dar wrote itself just the same
  /// (measured on <c>multi.*.dar</c> and <c>dar-2.8.6-sliced.*.dar</c>, dar 2.7.13).</summary>
  private void AssertDarAccepts(string basePath, string src, bool tapeMarks, string what, bool sliced = false) {
    var darBase = FsInteropToolbox.WinToWsl(basePath);
    var test = Dar($"-t {darBase}");
    Assert.That(test.ExitCode, Is.EqualTo(0), $"dar -t refused {what}:\n{test.StdOut}\n{test.StdErr}");
    tapeMarks &= !sliced;
    if (tapeMarks) {
      var sequential = Dar($"-t {darBase} --sequential-read");
      Assert.That(sequential.ExitCode, Is.EqualTo(0), $"dar -t --sequential-read refused {what}:\n{sequential.StdOut}\n{sequential.StdErr}");
    }

    var list = Dar($"-l {darBase}");
    Assert.That(list.ExitCode, Is.EqualTo(0));
    foreach (var name in DarFixtures.TreeFiles.Keys.Select(k => k.Split('/')[^1]).Append("emptydir"))
      Assert.That(list.StdOut, Does.Contain(name));

    foreach (var sequential in tapeMarks ? new[] { false, true } : [false]) {
      var outDir = Path.Combine(this._tmp, "out" + (sequential ? "-seq" : ""));
      Directory.CreateDirectory(outDir);
      // -O: restoring ownership needs root; dar asks about it even for its own archives otherwise.
      var extract = Dar($"-O -x {darBase} -R {FsInteropToolbox.WinToWsl(outDir)}{(sequential ? " --sequential-read" : "")}");
      Assert.That(extract.ExitCode, Is.EqualTo(0), $"dar -x{(sequential ? " --sequential-read" : "")} failed on {what}:\n{extract.StdOut}\n{extract.StdErr}");
      AssertSameTree(src, outDir);
    }
  }

  private List<ArchiveInputInfo> SourceInputs(out string src) {
    src = Path.Combine(this._tmp, "src");
    WriteTree(src);
    // Incompressible, so that small slice sizes really span several slices whatever the method.
    var noise = new byte[20_000];
    new Random(7).NextBytes(noise);
    File.WriteAllBytes(Path.Combine(src, "dir", "noise20k.bin"), noise);
    var root = src;
    return Directory.EnumerateFileSystemEntries(src, "*", SearchOption.AllDirectories)
      .Select(p => new ArchiveInputInfo(p, Path.GetRelativePath(root, p).Replace('\\', '/'), Directory.Exists(p)))
      .ToList();
  }

  // ── Ours -> dar ─────────────────────────────────────────────────────────────

  [TestCaseSource(nameof(MethodsWithAndWithoutTapeMarks))]
  public void OurArchive(string method, bool tapeMarks) {
    RequireDar();
    var inputs = this.SourceInputs(out var src);
    var basePath = Path.Combine(this._tmp, "ours");
    var options = new FormatCreateOptions { MethodName = method, FormatSpecific = { [DarFormatDescriptor.TapeMarksOption] = tapeMarks ? "true" : "false" } };
    using (var fs = File.Create(basePath + ".1.dar"))
      new DarFormatDescriptor().Create(fs, inputs, options);
    this.AssertDarAccepts(basePath, src, tapeMarks, $"the {method} archive");
  }

  [TestCaseSource(typeof(DarWriterTests), nameof(DarWriterTests.Methods))]
  public void GivenOurBlockModeArchive_WhenDarReadsIt_ThenItIsAcceptedAndByteIdentical(string method) {
    RequireDar();
    var inputs = this.SourceInputs(out var src);
    var basePath = Path.Combine(this._tmp, "blocks");
    var options = new FormatCreateOptions {
      MethodName = method,
      FormatSpecific = { [DarFormatDescriptor.CompressionBlockSizeOption] = "16k", [DarFormatDescriptor.TapeMarksOption] = "true" },
    };
    using (var fs = File.Create(basePath + ".1.dar"))
      new DarFormatDescriptor().Create(fs, inputs, options);
    this.AssertDarAccepts(basePath, src, tapeMarks: true, $"the {method} block-mode archive");
  }

  [Test]
  public void GivenOurArchiveOfNothing_WhenDarTestsIt_ThenItIsAccepted() {
    RequireDar();
    foreach (var tape in new[] { false, true }) {
      var basePath = Path.Combine(this._tmp, "nothing" + tape);
      using (var fs = File.Create(basePath + ".1.dar"))
        new DarFormatDescriptor().Create(fs, [], new FormatCreateOptions { FormatSpecific = { [DarFormatDescriptor.TapeMarksOption] = tape.ToString() } });
      Assert.That(Dar($"-t {FsInteropToolbox.WinToWsl(basePath)}").ExitCode, Is.EqualTo(0));
    }
  }

  [TestCase("stored", "1k", "", false, TestName = "GivenOurSlicedArchive_WhenDarReadsTheSet_ThenItIsAcceptedAndByteIdentical")]
  [TestCase("gzip", "1k", "3k", false, TestName = "GivenOurSlicedArchiveWithABiggerFirstSlice_WhenDarReadsTheSet_ThenItIsAcceptedAndByteIdentical")]
  [TestCase("zstd", "2k", "700", true, TestName = "GivenOurSlicedTapeMarkedArchiveWithASmallerFirstSlice_WhenDarReadsTheSet_ThenItIsAcceptedAndByteIdentical")]
  [TestCase("xz", "1500", "", true, TestName = "GivenOurSlicedTapeMarkedXzArchive_WhenDarReadsTheSet_ThenItIsAcceptedAndByteIdentical")]
  public void OurSlicedArchive(string method, string slice, string first, bool tapeMarks) {
    RequireDar();
    var inputs = this.SourceInputs(out var src);
    var basePath = Path.Combine(this._tmp, "sliced");
    var options = new FormatCreateOptions {
      MethodName = method,
      FormatSpecific = {
        [DarFormatDescriptor.SliceSizeOption] = slice,
        [DarFormatDescriptor.FirstSliceSizeOption] = first,
        [DarFormatDescriptor.TapeMarksOption] = tapeMarks.ToString(),
      },
    };
    using (var fs = File.Create(basePath + ".1.dar"))
      new DarFormatDescriptor().Create(fs, inputs, options);
    var slices = Enumerable.Range(1, 1000).Select(n => $"{basePath}.{n}.dar").TakeWhile(File.Exists).ToArray();
    Assert.That(slices, Has.Length.GreaterThan(2), "the case should span several slices");
    this.AssertDarAccepts(basePath, src, tapeMarks, $"the sliced {method} archive", sliced: true);
  }

  [TestCaseSource(typeof(DarWriterTests), nameof(DarWriterTests.Methods))]
  public void GivenOurStreamedArchive_WhenDarReadsIt_ThenItIsAcceptedAndByteIdentical(string method) {
    RequireDar();
    // Everything above 1 KiB takes the on-the-fly path, compressed whatever the outcome.
    var items = DarWriterFeatureTests.TreeItems();
    var big = new byte[600_000];
    for (var i = 0; i < big.Length; ++i)
      big[i] = (byte)(i % 1000 < 700 ? i % 253 : (i * 2654435761u) >> 24);
    items.Add(DarWriter.Item.FromBytes("dir/big.bin", big, 0x1A4, DateTime.UtcNow, DateTime.UtcNow));
    var src = Path.Combine(this._tmp, "src");
    WriteTree(src);
    File.WriteAllBytes(Path.Combine(src, "dir", "big.bin"), big);
    var basePath = Path.Combine(this._tmp, "streamed");
    using (var fs = File.Create(basePath + ".1.dar"))
      DarWriter.Write(fs, items, new DarWriter.Settings(DarWriter.AlgorithmFor(method), TapeMarks: true) { InMemoryLimit = 1024 });
    this.AssertDarAccepts(basePath, src, tapeMarks: true, $"the streamed {method} archive");
  }

  // The xz and zstd encoders close a stream / frame every 64 MiB of input and open the next one;
  // dar has to read the whole run as one compression unit.
  [TestCase("xz")]
  [TestCase("zstd")]
  public void GivenAFileSpanningSeveralCodecStreams_WhenDarRestoresIt_ThenItIsByteIdentical(string method) {
    RequireDar();
    var src = Path.Combine(this._tmp, "src");
    Directory.CreateDirectory(src);
    var data = new byte[70 << 20];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)((i >> 12) * 31 + (i & 0xFF));
    File.WriteAllBytes(Path.Combine(src, "multi.bin"), data);
    var basePath = Path.Combine(this._tmp, "frames");
    using (var fs = File.Create(basePath + ".1.dar"))
      new DarFormatDescriptor().Create(fs, [new ArchiveInputInfo(Path.Combine(src, "multi.bin"), "multi.bin", false)],
        new FormatCreateOptions { MethodName = method });
    var darBase = FsInteropToolbox.WinToWsl(basePath);
    Assert.That(Dar($"-t {darBase}").ExitCode, Is.EqualTo(0));
    var outDir = Path.Combine(this._tmp, "out");
    Directory.CreateDirectory(outDir);
    Assert.That(Dar($"-O -x {darBase} -R {FsInteropToolbox.WinToWsl(outDir)}").ExitCode, Is.EqualTo(0));
    Assert.That(File.ReadAllBytes(Path.Combine(outDir, "multi.bin")), Is.EqualTo(data));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void GivenSymlinksOwnershipModesAndTimes_WhenDarListsAndRestoresOurArchive_ThenItSeesThemAsWritten(bool tapeMarks) {
    RequireDar();
    var modified = new DateTime(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc);
    var items = new List<DarWriter.Item> {
      new("d", DarWriter.ItemKind.Directory, 0x1C0, modified, modified) { Uid = 4321, Gid = 8765 }, // 0700
      DarWriter.Item.FromBytes("d/run.sh", "#!/bin/sh\necho hi\n"u8.ToArray(), 0x1E4, modified, modified) with { Uid = 4321, Gid = 8765 }, // 0744
      new("d/link", DarWriter.ItemKind.Symlink, 0x1FF, modified, modified) { LinkTarget = "run.sh", Uid = 4321, Gid = 8765 },
      new("dangling", DarWriter.ItemKind.Symlink, 0x1FF, modified, modified) { LinkTarget = "../no where" },
    };
    var basePath = Path.Combine(this._tmp, "meta");
    using (var fs = File.Create(basePath + ".1.dar"))
      DarWriter.Write(fs, items, new DarWriter.Settings('n', TapeMarks: tapeMarks));
    var darBase = FsInteropToolbox.WinToWsl(basePath);
    Assert.That(Dar($"-t {darBase}").ExitCode, Is.EqualTo(0));

    var list = Dar($"-l {darBase}");
    Assert.That(list.ExitCode, Is.EqualTo(0));
    var lines = list.StdOut.Split('\n');
    string Line(string name) => lines.Single(l => l.TrimEnd().EndsWith(name, StringComparison.Ordinal) || l.Contains(name + " -> ", StringComparison.Ordinal));
    Assert.That(Line("run.sh"), Does.Contain("-rwxr--r--").And.Contain("4321").And.Contain("8765"));
    Assert.That(Line("d"), Does.Contain("drwx------"));
    // dar -l shows a symlink's mode and owner, not its target; readlink below checks the target.
    Assert.That(Line("d/link"), Does.Contain("lrwxrwxrwx").And.Contain("4321"));
    Assert.That(Line("dangling"), Does.Contain("lrwxrwxrwx"));

    // Restore onto a Linux file system: a Windows drive mounted in WSL keeps no modes or owners.
    var wslOut = $"/tmp/cwb-dar-meta-{Guid.NewGuid():N}";
    var root = FsInteropToolbox.WslHasPasswordlessSudo;
    try {
      var extract = Shell($"mkdir -p {wslOut} && {(root ? "sudo " : "")}{DarBinary} -Q {(root ? "" : "-O ")}-x {darBase} -R {wslOut}");
      Assert.That(extract.ExitCode, Is.EqualTo(0));
      var stat = Shell($"cd {wslOut} && stat -c '%n %A %u %g %Y' d d/run.sh && readlink d/link dangling");
      Assert.That(stat.ExitCode, Is.EqualTo(0));
      var mtime = new DateTimeOffset(modified).ToUnixTimeSeconds();
      // Ownership is only restored by root; without passwordless sudo the listing above is the proof.
      Assert.That(stat.StdOut, Does.Contain($"d/run.sh -rwxr--r--{(root ? " 4321 8765" : "")}"));
      Assert.That(stat.StdOut, Does.Contain($" {mtime}\n"));
      Assert.That(stat.StdOut, Does.Contain("d drwx------"));
      Assert.That(stat.StdOut, Does.Contain("run.sh\n../no where"));
    } finally {
      Shell($"{(root ? "sudo " : "")}rm -rf {wslOut}");
    }
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
  [TestCase("-S 5k -s 9k -zxz", TestName = "GivenDarSlicedCompressedArchiveWithASmallerFirstSlice_WhenWeExtractIt_ThenItIsByteIdentical")]
  public void DarArchivesExtract(string options) {
    RequireDar();
    var src = Path.Combine(this._tmp, "src");
    WriteTree(src);
    // A bigger incompressible file so that -s 16k really spans several slices.
    var noise = new byte[40_000];
    new Random(3).NextBytes(noise);
    File.WriteAllBytes(Path.Combine(src, "dir", "noise40k.bin"), noise);
    Assert.That(Shell($"ln -s hello.txt {FsInteropToolbox.WinToWsl(Path.Combine(src, "link"))}").ExitCode, Is.EqualTo(0));

    var basePath = Path.Combine(this._tmp, "theirs");
    var create = Dar($"-c {FsInteropToolbox.WinToWsl(basePath)} {options} -R {FsInteropToolbox.WinToWsl(src)}");
    Assert.That(create.ExitCode, Is.EqualTo(0), $"dar -c failed:\n{create.StdOut}\n{create.StdErr}");
    var slices = Enumerable.Range(1, 1000).Select(n => $"{basePath}.{n}.dar").TakeWhile(File.Exists).ToArray();
    Assert.That(slices, Is.Not.Empty);
    if (options.Contains("-s "))
      Assert.That(slices, Has.Length.GreaterThan(1), "the fixture should span several slices");

    // Open the LAST slice: that is where dar keeps the catalogue, and the reader has to find the rest.
    using (var fs = File.OpenRead(slices[^1])) {
      var link = new DarFormatDescriptor().List(fs, null).Single(e => e.Name == "link");
      Assert.That(link.IsSymlink, Is.True);
      Assert.That(link.LinkTarget, Is.EqualTo("hello.txt"));
    }
    File.Delete(Path.Combine(src, "link")); // listed, not materialised
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
      if (slicing.Length > 0) {
        Assert.That(h.SliceSize, Is.EqualTo(16384));
        Assert.That(h.FirstSliceSize, Is.Null, "dar records the first slice size only when -S differs");
      }
    }
  }
}
