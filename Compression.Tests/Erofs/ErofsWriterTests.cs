#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Erofs;

namespace Compression.Tests.Erofs;

[TestFixture]
public class ErofsWriterTests {

  private static byte[] BuildImage(params (string Path, string Content)[] files) {
    var writer = new ErofsWriter();
    foreach (var (path, content) in files)
      writer.AddFile(path, Encoding.UTF8.GetBytes(content));
    return writer.Build();
  }

  private static Dictionary<string, byte[]> ReadAll(byte[] image) {
    var reader = new ErofsReader(image);
    var map = new Dictionary<string, byte[]>();
    foreach (var entry in reader.Entries)
      if (!entry.IsDirectory)
        map[entry.Path] = reader.ExtractFile(entry);
    return map;
  }

  [Test]
  public void Given_SingleRootFile_When_RoundTripped_Then_ContentSurvives() {
    var image = BuildImage(("readme.txt", "hello world\n"));

    var files = ReadAll(image);

    Assert.That(files, Does.ContainKey("readme.txt"));
    Assert.That(Encoding.UTF8.GetString(files["readme.txt"]), Is.EqualTo("hello world\n"));
  }

  [Test]
  public void Given_NestedFile_When_RoundTripped_Then_FoundAtNestedPathWithContent() {
    var image = BuildImage(("docs/guide/intro.md", "# Intro\nbody\n"));

    var files = ReadAll(image);

    Assert.That(files, Does.ContainKey("docs/guide/intro.md"));
    Assert.That(Encoding.UTF8.GetString(files["docs/guide/intro.md"]), Is.EqualTo("# Intro\nbody\n"));
  }

  [Test]
  public void Given_MultipleFilesAcrossSubdirectories_When_RoundTripped_Then_EachFoundWithContent() {
    var image = BuildImage(
      ("root.txt", "at the top"),
      ("a/one.txt", "first"),
      ("a/two.txt", "second"),
      ("a/b/deep.txt", "buried"),
      ("c/other.txt", "elsewhere"));

    var files = ReadAll(image);

    Assert.That(files, Has.Count.EqualTo(5));
    Assert.That(Encoding.UTF8.GetString(files["root.txt"]), Is.EqualTo("at the top"));
    Assert.That(Encoding.UTF8.GetString(files["a/one.txt"]), Is.EqualTo("first"));
    Assert.That(Encoding.UTF8.GetString(files["a/two.txt"]), Is.EqualTo("second"));
    Assert.That(Encoding.UTF8.GetString(files["a/b/deep.txt"]), Is.EqualTo("buried"));
    Assert.That(Encoding.UTF8.GetString(files["c/other.txt"]), Is.EqualTo("elsewhere"));
  }

  [Test]
  public void Given_FileLargerThanOneBlock_When_RoundTripped_Then_ContentSurvives() {
    var big = string.Concat(Enumerable.Repeat("0123456789ABCDEF", 1000)); // 16000 bytes > 4096
    var image = BuildImage(("big.bin", big));

    var files = ReadAll(image);

    Assert.That(Encoding.UTF8.GetString(files["big.bin"]), Is.EqualTo(big));
  }

  [Test]
  public void Given_EmptyFile_When_RoundTripped_Then_PresentWithZeroLength() {
    var image = BuildImage(("empty.dat", ""));

    var files = ReadAll(image);

    Assert.That(files, Does.ContainKey("empty.dat"));
    Assert.That(files["empty.dat"], Is.Empty);
  }

  [Test]
  public void Given_BuiltImage_When_HeaderInspected_Then_MagicMatchesReader() {
    var image = BuildImage(("x.txt", "y"));

    var magic = BitConverter.ToUInt32(image, 1024);

    Assert.That(magic, Is.EqualTo(ErofsReader.Magic));
  }

  [Test]
  public void Given_DescriptorCreate_When_RoundTrippedThroughReader_Then_PathsAndContentSurvive() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("notes/today.txt", "meeting at noon"u8.ToArray()),
      ArchiveInputInfo.InMemory("top.txt", "root level"u8.ToArray()),
    };

    using var output = new MemoryStream();
    new ErofsFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    var files = ReadAll(output.ToArray());
    Assert.That(Encoding.UTF8.GetString(files["notes/today.txt"]), Is.EqualTo("meeting at noon"));
    Assert.That(Encoding.UTF8.GetString(files["top.txt"]), Is.EqualTo("root level"));
  }
}
