using Compression.Lib;
using Compression.NativeUI.Views;

namespace Compression.Tests.SevenZip;

/// <summary>
/// Split 7z archives (<c>name.7z.001</c>, <c>.002</c>, …): created through the library, read
/// back from any volume, a missing volume named, and — where 7-Zip is installed — 7-Zip extracts
/// our volumes and we extract its.
/// </summary>
[TestFixture]
public sealed class SevenZipVolumeTests {
  private string _root = null!;
  private string _source = null!;
  private SortedDictionary<string, byte[]?> _tree = null!;

  [SetUp]
  public void Setup() {
    this._root = Path.Combine(Path.GetTempPath(), $"cwb_7zvol_{Guid.NewGuid():N}");
    this._source = Path.Combine(this._root, "src");
    this._tree = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal) {
      ["noise.bin"] = SevenZipOracle.Noise(150_000, 1),
      ["text.txt"] = SevenZipOracle.Text(60_000, 2),
      ["dir"] = null,
      ["dir/small.txt"] = "small"u8.ToArray(),
    };
    SevenZipOracle.Materialize(this._source, this._tree);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
  }

  private string CreateSplit(long volumeSize, string? password = null, string name = "split.7z") {
    var archive = Path.Combine(this._root, name);
    ArchiveOperations.Create(archive, ArchiveInput.Resolve([.. Directory.GetFileSystemEntries(this._source)]),
      new CompressionOptions { VolumeSize = volumeSize, Password = password, Method = MethodSpec.Parse("copy") });
    return archive;
  }

  private SortedDictionary<string, byte[]?> ExtractWithLib(string path, string? password = null) {
    var output = Path.Combine(this._root, $"out_{Guid.NewGuid():N}");
    ArchiveOperations.Extract(path, output, password, null);
    return SevenZipOracle.ReadTree(output);
  }

  [Test]
  [Category("HappyPath")]
  public void Given_AVolumeSize_When_Created_Then_TheArchiveIsCutInto7ZipNamedVolumes() {
    var archive = this.CreateSplit(64_000);

    Assert.That(File.Exists(archive), Is.False, "the single file is replaced by its volumes");
    var volumes = ArchiveVolumes.VolumesOf(ArchiveVolumes.VolumePath(archive, 1));
    Assert.That(volumes, Has.Count.EqualTo(4));
    Assert.That(volumes.Take(3).Select(static v => new FileInfo(v).Length), Is.All.EqualTo(64_000));
    Assert.That(Path.GetFileName(volumes[0]), Is.EqualTo("split.7z.001"));
  }

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(4)]
  [Category("HappyPath")]
  public void Given_ASplitArchive_When_OpenedFromAnyVolume_Then_EveryEntryMatches(int volume) {
    var archive = this.CreateSplit(64_000);
    var path = ArchiveVolumes.VolumePath(archive, volume);

    Assert.That(ArchiveOperations.List(path, null).Select(static e => e.Name.Replace('\\', '/')), Is.SupersetOf(new[] { "noise.bin", "text.txt", "dir/small.txt" }));
    SevenZipOracle.AssertSameTree(this._tree, this.ExtractWithLib(path), $"from volume {volume}");
    Assert.That(ArchiveOperations.TestDetailed(path, null), Is.EqualTo(ArchiveTestResult.Ok));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ASplitArchiveMissingAMiddleVolume_When_Opened_Then_TheMissingVolumeIsNamed() {
    var archive = this.CreateSplit(64_000);
    File.Delete(ArchiveVolumes.VolumePath(archive, 2));

    Assert.That(() => ArchiveOperations.List(ArchiveVolumes.VolumePath(archive, 1), null),
      Throws.TypeOf<FileNotFoundException>().With.Message.Contains("split.7z.002"));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ASplitArchiveMissingItsLastVolume_When_Opened_Then_ItSaysAVolumeIsMissing() {
    var archive = this.CreateSplit(64_000);
    File.Delete(ArchiveVolumes.VolumePath(archive, 4));

    Assert.That(() => ArchiveOperations.List(ArchiveVolumes.VolumePath(archive, 1), null),
      Throws.TypeOf<InvalidDataException>().With.Message.Contains("volume is missing"));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AnEarlierSplitWithMoreVolumes_When_CreatedAgain_Then_StaleVolumesAreRemoved() {
    var archive = this.CreateSplit(20_000);
    Assert.That(File.Exists(ArchiveVolumes.VolumePath(archive, 10)), Is.True);

    this.CreateSplit(100_000);

    Assert.That(File.Exists(ArchiveVolumes.VolumePath(archive, 4)), Is.False);
    Assert.That(File.Exists(ArchiveVolumes.VolumePath(archive, 10)), Is.False);
    SevenZipOracle.AssertSameTree(this._tree, this.ExtractWithLib(ArchiveVolumes.VolumePath(archive, 1)), "re-split");
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ANonVolumePath_When_Parsed_Then_ItIsNotAVolume() {
    Assert.That(ArchiveVolumes.TryParseVolume("backup.7z", out _, out _), Is.False);
    Assert.That(ArchiveVolumes.TryParseVolume("backup.7z.01", out _, out _), Is.False);
    Assert.That(ArchiveVolumes.TryParseVolume("backup.7z.000", out _, out _), Is.False);
    Assert.That(ArchiveVolumes.TryParseVolume("backup.7z.012", out var basePath, out var n), Is.True);
    Assert.That((basePath, n), Is.EqualTo(("backup.7z", 12)));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_TheCreateDialogSplitChoice_When_TurnedIntoOptions_Then_TheVolumeSizeIsInBytes() {
    var options = new CreateOptionsViewModel(FormatDetector.Format.SevenZip) { SelectedVolumeSize = "650 MB (CD)" };
    Assert.That(options.ShowVolumeSize, Is.True);
    Assert.That(options.ToOptions().VolumeSize, Is.EqualTo(650L << 20));

    options.SelectedVolumeSize = "No split";
    Assert.That(options.ToOptions().VolumeSize, Is.Zero);

    options.SelectedVolumeSize = "100 MB";
    options.MakeSfx = true;
    Assert.That(options.ShowVolumeSize, Is.False, "a self-extractor is one file");
    Assert.That(options.ToOptions().VolumeSize, Is.Zero);
  }

  [Test]
  [Category("EndToEnd")]
  public void Given_OurSplitArchive_When_7ZipExtractsTheFirstVolume_Then_EverythingMatches() {
    SevenZipOracle.Require();
    var archive = this.CreateSplit(64_000, password: "S3cret!", name: "ours.7z");
    var output = Path.Combine(this._root, "7z-out");
    SevenZipOracle.RunOk(this._root, "x", "-y", "-pS3cret!", $"-o{output}", ArchiveVolumes.VolumePath(archive, 1));
    SevenZipOracle.AssertSameTree(this._tree, SevenZipOracle.ReadTree(output), "7-Zip from our volumes");
  }

  [TestCase(false)]
  [TestCase(true)]
  [Category("EndToEnd")]
  public void Given_7ZipSplitArchive_When_ExtractedThroughTheLibrary_Then_EverythingMatches(bool encryptHeaders) {
    SevenZipOracle.Require();
    var archive = Path.Combine(this._root, "theirs.7z");
    string[] switches = encryptHeaders ? ["-pS3cret!", "-mhe=on"] : [];
    SevenZipOracle.RunOk(this._source, ["a", "-y", "-r", "-v40k", archive, .. switches, "*"]);
    Assert.That(File.Exists(ArchiveVolumes.VolumePath(archive, 3)), Is.True, "7-Zip wrote several volumes");

    SevenZipOracle.AssertSameTree(this._tree, this.ExtractWithLib(ArchiveVolumes.VolumePath(archive, 1), encryptHeaders ? "S3cret!" : null), "ours from 7-Zip's volumes");
  }
}
