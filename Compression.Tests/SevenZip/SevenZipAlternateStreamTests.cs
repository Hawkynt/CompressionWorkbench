using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// NTFS alternate data streams and the 7z format, measured on a real NTFS volume (the test output
/// folder, not the temporary folder): 7-Zip does not store alternate streams in a 7z archive, and
/// an entry whose name carries a stream suffix (<c>file.txt:stream</c>) is extracted by 7-Zip — with
/// or without <c>-sns</c> — and by us as an ordinary file with <c>:</c> turned into <c>_</c>.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[NonParallelizable]
public sealed class SevenZipAlternateStreamTests {
  private string _root = null!;

  [SetUp]
  public void Setup() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("NTFS alternate data streams.");
    SevenZipOracle.Require();
    this._root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"cwb_7zads_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._root);
    if (new DriveInfo(Path.GetPathRoot(this._root)!).DriveFormat != "NTFS")
      Assert.Ignore("The test output folder is not on NTFS.");
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
  }

  [Test]
  public void Given_AFileWithAnAlternateStream_When_7ZipArchivesItAs7zWithSns_Then_ItRefusesAndWritesNothing() {
    var source = Path.Combine(this._root, "src");
    Directory.CreateDirectory(source);
    var file = Path.Combine(source, "f.txt");
    File.WriteAllText(file, "main");
    File.WriteAllText(file + ":stream1", "ads data");
    Assert.That(File.ReadAllText(file + ":stream1"), Is.EqualTo("ads data"), "precondition: a real alternate stream");

    var archive = Path.Combine(this._root, "theirs.7z");
    var (exit, output) = SevenZipOracle.Run(source, "a", "-t7z", "-sns", archive, "f.txt");

    Assert.That(exit, Is.Not.Zero, output);
    Assert.That(File.Exists(archive), Is.False);
  }

  [TestCase(null)]
  [TestCase("-sns")]
  public void Given_OurEntryNamedLikeAnAlternateStream_When_7ZipAndWeExtractIt_Then_BothWriteThePlainFileName(string? sns) {
    var archive = Path.Combine(this._root, "ours.7z");
    using (var stream = File.Create(archive))
    using (var writer = new SevenZipWriter(stream, SevenZipCodec.Copy)) {
      writer.AddEntry(new SevenZipEntry { Name = "f.txt" }, "main"u8);
      writer.AddEntry(new SevenZipEntry { Name = "f.txt:stream1" }, "ads data"u8);
      writer.Finish();
    }

    var theirs = Path.Combine(this._root, "theirs-out");
    string[] args = sns == null ? ["x", "-y", $"-o{theirs}", archive] : ["x", "-y", sns, $"-o{theirs}", archive];
    SevenZipOracle.RunOk(this._root, args);
    var ours = Path.Combine(this._root, "ours-out");
    using (var stream = File.OpenRead(archive))
      new SevenZipFormatDescriptor().Extract(stream, ours, null, null);

    foreach (var output in new[] { theirs, ours }) {
      Assert.That(File.ReadAllText(Path.Combine(output, "f.txt_stream1")), Is.EqualTo("ads data"), output);
      Assert.That(File.ReadAllText(Path.Combine(output, "f.txt")), Is.EqualTo("main"), output);
      Assert.That(File.Exists(Path.Combine(output, "f.txt") + ":stream1"), Is.False, $"{output}: no alternate stream is created");
    }
  }
}
