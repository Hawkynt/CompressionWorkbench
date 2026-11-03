using Compression.Lib;

namespace Compression.Tests.SevenZip;

/// <summary>
/// 7z dictionaries are offered down to 64 KiB; below 1 MiB the PPMd memory derived from the
/// dictionary was refused by the writer, so no 7z could be written at all, whatever the method.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser 97b06f1, 489e359.</remarks>
[TestFixture]
public sealed class SevenZipSmallDictionaryTests {
  [TestCase("lzma2")]
  [TestCase("ppmd")]
  [TestCase("copy")]
  [Category("Boundary")]
  public void GivenA64KiBDictionary_WhenA7zIsCreated_ThenItReadsBack(string method) {
    var dir = Directory.CreateTempSubdirectory("cwb_7zsmall_").FullName;
    try {
      var file = Path.Combine(dir, "a.txt");
      File.WriteAllText(file, "alpha alpha alpha");
      var target = Path.Combine(dir, $"small-{method}.7z");
      ArchiveOperations.Create(target, [new ArchiveInput(file, "a.txt")],
        new CompressionOptions { Method = MethodSpec.Parse(method), DictSize = 65536 }, FormatDetector.Format.SevenZip);
      Assert.That(ArchiveOperations.List(target, null).Select(e => e.Name), Is.EqualTo(new[] { "a.txt" }));
      Assert.That(ArchiveOperations.ExtractEntry(target, "a.txt", null), Is.EqualTo("alpha alpha alpha"u8.ToArray()));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  // PPMd is left out here: main's 7z PPMd coder is not yet the one 7-Zip reads (#623).
  [TestCase("lzma2")]
  [TestCase("copy")]
  [Category("ArchiveExternalInterop")]
  public void GivenA64KiBDictionary_When7ZipTestsTheArchive_ThenItIsOk(string method) {
    FsInteropToolbox.Require7z();
    var dir = Directory.CreateTempSubdirectory("cwb_7zsmall_").FullName;
    try {
      var file = Path.Combine(dir, "a.txt");
      File.WriteAllText(file, string.Concat(Enumerable.Repeat("alpha beta gamma ", 500)));
      var target = Path.Combine(dir, $"small-{method}.7z");
      ArchiveOperations.Create(target, [new ArchiveInput(file, "a.txt")],
        new CompressionOptions { Method = MethodSpec.Parse(method), DictSize = 65536 }, FormatDetector.Format.SevenZip);

      var (stdOut, stdErr, exitCode) = FsInteropToolbox.Run7z($"t \"{target}\"");

      Assert.That(exitCode, Is.Zero, stdOut + stdErr);
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
