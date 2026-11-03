using Compression.Registry;
using FileFormat.Bkf;

namespace Compression.Tests.Bkf;

/// <summary>
/// BKF edits keep folders apart: an added file used to land in whichever folder the backup
/// ended with, a removal matched the file name alone in every folder, and a new backup wrote
/// top-level files after a folder's DIRB, so they read back inside that folder.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser 24e7871, 4e0017f, e409053.</remarks>
[TestFixture]
public sealed class BkfFolderEditTests {
  private static MemoryStream Backup(params ArchiveInputInfo[] inputs) {
    var stream = new MemoryStream();
    new BkfFormatDescriptor().Create(stream, inputs, new FormatCreateOptions());
    var expandable = new MemoryStream();
    expandable.Write(stream.ToArray());
    expandable.Position = 0;
    return expandable;
  }

  private static string[] Files(Stream bkf) {
    bkf.Position = 0;
    return [.. new BkfFormatDescriptor().List(bkf, null).Where(e => !e.IsDirectory).Select(e => e.Name).Order(StringComparer.Ordinal)];
  }

  private static string[] Folders(Stream bkf) {
    bkf.Position = 0;
    return [.. new BkfFormatDescriptor().List(bkf, null).Where(e => e.IsDirectory).Select(e => e.Name.TrimEnd('/'))];
  }

  private static string Content(Stream bkf, string name) {
    bkf.Position = 0;
    var r = new BkfReader(bkf, leaveOpen: true);
    return System.Text.Encoding.ASCII.GetString(r.Extract(r.Entries.Single(e => e.Name == name)));
  }

  [Test, Category("HappyPath")]
  public void GivenAFolderBeforeATopLevelFile_WhenCreated_ThenTheTopLevelFileStaysAtTheTop() {
    using var bkf = Backup(ArchiveInputInfo.InMemory("d/a.txt", "inner"u8), ArchiveInputInfo.InMemory("a.txt", "outer"u8));

    Assert.Multiple(() => {
      Assert.That(Files(bkf), Is.EqualTo(new[] { "a.txt", "d/a.txt" }));
      Assert.That(Content(bkf, "a.txt"), Is.EqualTo("outer"));
      Assert.That(Content(bkf, "d/a.txt"), Is.EqualTo("inner"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenABackupEndingInAFolder_WhenATopLevelFileIsAdded_ThenItLandsAtTheTop() {
    using var bkf = Backup(ArchiveInputInfo.InMemory("a.txt", "outer"u8), ArchiveInputInfo.InMemory("d/a.txt", "inner"u8), new ArchiveInputInfo("", "d/e/", true));

    new BkfFormatDescriptor().Add(bkf, [ArchiveInputInfo.InMemory("c.txt", "gamma"u8)]);

    Assert.Multiple(() => {
      Assert.That(Files(bkf), Is.EqualTo(new[] { "a.txt", "c.txt", "d/a.txt" }));
      Assert.That(Content(bkf, "c.txt"), Is.EqualTo("gamma"));
      Assert.That(Folders(bkf), Does.Contain("d/e"), "the empty folder is kept");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAFileInAFolder_WhenAdded_ThenItLandsInThatFolder() {
    using var bkf = Backup(ArchiveInputInfo.InMemory("a.txt", "outer"u8), ArchiveInputInfo.InMemory("d/a.txt", "inner"u8));

    new BkfFormatDescriptor().Add(bkf, [ArchiveInputInfo.InMemory("x/y/new.txt", "new"u8), new ArchiveInputInfo("", "empty/", true)]);

    Assert.Multiple(() => {
      Assert.That(Files(bkf), Is.EqualTo(new[] { "a.txt", "d/a.txt", "x/y/new.txt" }));
      Assert.That(Content(bkf, "x/y/new.txt"), Is.EqualTo("new"));
      Assert.That(Folders(bkf), Does.Contain("empty"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheSameNameAtTheTopAndInAFolder_WhenTheTopOneIsRemoved_ThenTheFolderOneStays() {
    using var bkf = Backup(ArchiveInputInfo.InMemory("a.txt", "outer"u8), ArchiveInputInfo.InMemory("d/a.txt", "inner"u8));

    new BkfFormatDescriptor().Remove(bkf, ["a.txt"]);

    Assert.Multiple(() => {
      Assert.That(Files(bkf), Is.EqualTo(new[] { "d/a.txt" }));
      Assert.That(Content(bkf, "d/a.txt"), Is.EqualTo("inner"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAFolder_WhenRemoved_ThenEverythingInItGoes() {
    using var bkf = Backup(ArchiveInputInfo.InMemory("a.txt", "outer"u8), ArchiveInputInfo.InMemory("d/a.txt", "inner"u8), ArchiveInputInfo.InMemory("d/e/b.txt", "deep"u8));

    new BkfFormatDescriptor().Remove(bkf, ["d"]);

    Assert.Multiple(() => {
      Assert.That(Files(bkf), Is.EqualTo(new[] { "a.txt" }));
      Assert.That(Folders(bkf).Where(f => f == "d" || f.StartsWith("d/", StringComparison.Ordinal)), Is.Empty);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAFile_WhenReplaced_ThenThereIsOneCopyWithTheNewContent() {
    using var bkf = Backup(ArchiveInputInfo.InMemory("d/a.txt", "old"u8));

    new BkfFormatDescriptor().Add(bkf, [ArchiveInputInfo.InMemory("d/a.txt", "new"u8)]);

    Assert.Multiple(() => {
      Assert.That(Files(bkf), Is.EqualTo(new[] { "d/a.txt" }));
      Assert.That(Content(bkf, "d/a.txt"), Is.EqualTo("new"));
    });
  }
}
