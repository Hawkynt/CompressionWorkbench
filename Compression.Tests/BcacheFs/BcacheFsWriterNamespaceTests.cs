using System.Text;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>Symbolic links and directories the writer lays down, read back here.</summary>
[TestFixture]
public sealed class BcacheFsWriterNamespaceTests {

  private static BcacheFsReader Read(BcacheFsWriter writer, MemoryStream image) {
    writer.WriteTo(image);
    image.Position = 0;
    return new BcacheFsReader(image);
  }

  [Test, Category("HappyPath")]
  public void GivenASymlink_WhenRead_ThenItCarriesItsTargetAndTheTerminatingNulCountsInItsSize() {
    var writer = new BcacheFsWriter();
    writer.AddFile("a.txt", "x"u8.ToArray());
    writer.AddSymlink("dir/link", "../a.txt");
    using var image = new MemoryStream();
    using var reader = Read(writer, image);

    var link = reader.Entries.Single(e => e.Name == "dir/link");
    Assert.Multiple(() => {
      Assert.That(reader.Valid, Is.True, reader.Status);
      Assert.That(link.LinkTarget, Is.EqualTo("../a.txt"));
      Assert.That(link.Size, Is.EqualTo("../a.txt".Length + 1), "page_symlink stores the target with its NUL");
      Assert.That(reader.Entries.Single(e => e.Name == "a.txt").LinkTarget, Is.Null);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnEmptyDirectory_WhenRead_ThenItIsListedWithItsParents() {
    var writer = new BcacheFsWriter();
    writer.AddDirectory("one/two/three");
    using var image = new MemoryStream();
    using var reader = Read(writer, image);

    Assert.That(reader.Directories, Is.EqualTo(new[] { "one", "one/two", "one/two/three" }));
  }

  [Test, Category("HappyPath")]
  public void GivenADirectoryInput_WhenCreatedThroughTheDescriptor_ThenItSurvivesEmpty() {
    var descriptor = new BcacheFsFormatDescriptor();
    using var image = new MemoryStream();
    descriptor.Create(image, [new ArchiveInputInfo("empty", "empty", IsDirectory: true)], new FormatCreateOptions());
    image.Position = 0;
    using var reader = new BcacheFsReader(image);

    Assert.That(reader.Directories, Is.EqualTo(new[] { "empty" }));
  }

  [Test, Category("ErrorHandling")]
  public void GivenALinkTargetWithANul_WhenAdded_ThenItIsRefused()
    => Assert.Throws<ArgumentException>(() => new BcacheFsWriter().AddSymlink("l", "a\0b"));

  [Test, Category("ErrorHandling")]
  public void GivenAnEmptyLinkTarget_WhenAdded_ThenItIsRefused()
    => Assert.Throws<ArgumentException>(() => new BcacheFsWriter().AddSymlink("l", ""));

  [Test, Category("ErrorHandling")]
  public void GivenTheSamePathTwice_WhenWritten_ThenItIsRefused() {
    var writer = new BcacheFsWriter();
    writer.AddFile("same", Encoding.ASCII.GetBytes("1"));
    writer.AddFile("same", Encoding.ASCII.GetBytes("2"));
    using var image = new MemoryStream();
    Assert.Throws<ArgumentException>(() => writer.WriteTo(image));
  }
}
