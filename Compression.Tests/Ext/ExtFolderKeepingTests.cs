using Compression.Lib;

namespace Compression.Tests.Ext;

/// <summary>
/// ext2/3/4 images keep folders: new images used to flatten every name to its leaf, and empty
/// folders were lost on create, on add and on extraction.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser d215a4d, 852a8a5, 8be5bfa, a60e9e3.</remarks>
[TestFixture]
public sealed class ExtFolderKeepingTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_extfold_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  private static List<string> Names(string image)
    => [.. ArchiveOperations.List(image, null).Select(e => e.Name.Replace('\\', '/').Trim('/'))];

  private string BuildEditedImage() {
    Directory.CreateDirectory(Path.Combine(this._dir, "pack", "sub", "empty"));
    File.WriteAllText(Path.Combine(this._dir, "pack", "sub", "Long Name.txt"), "deep");
    File.WriteAllText(Path.Combine(this._dir, "top.txt"), "top");
    var image = Path.Combine(this._dir, "n.ext4");
    ArchiveOperations.Create(image, [
      .. ArchiveInput.Resolve([Path.Combine(this._dir, "pack")]),
      new ArchiveInput(Path.Combine(this._dir, "top.txt"), "top.txt"),
    ], new CompressionOptions(), FormatDetector.Format.Ext);
    ArchiveOperations.Add(image, [new ArchiveInput("", "later/deeper/")]);
    ArchiveOperations.Add(image, [new ArchiveInput(Path.Combine(this._dir, "top.txt"), "pack/sub/added.txt")]);
    ArchiveOperations.Remove(image, ["top.txt"]);
    return image;
  }

  [Test, Category("HappyPath")]
  public void GivenFolders_WhenCreatedAddedAndRemoved_ThenTheTreeIsKept() {
    var image = this.BuildEditedImage();

    var names = Names(image);
    Assert.Multiple(() => {
      Assert.That(names, Is.SupersetOf(new[] { "pack", "pack/sub", "pack/sub/empty", "pack/sub/Long Name.txt", "pack/sub/added.txt", "later", "later/deeper" }));
      Assert.That(names, Does.Not.Contain("top.txt"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnImageWithEmptyFolders_WhenExtracted_ThenTheFoldersExist() {
    var image = this.BuildEditedImage();
    var outDir = Path.Combine(this._dir, "x");

    ArchiveOperations.Extract(image, outDir, null, null);

    Assert.Multiple(() => {
      Assert.That(File.ReadAllText(Path.Combine(outDir, "pack", "sub", "Long Name.txt")), Is.EqualTo("deep"));
      Assert.That(Directory.Exists(Path.Combine(outDir, "later", "deeper")), Is.True);
      Assert.That(Directory.Exists(Path.Combine(outDir, "pack", "sub", "empty")), Is.True);
    });
  }

  [Test, Category("EdgeCase")]
  public void GivenAnExistingFolder_WhenAddedAgain_ThenNothingChanges() {
    var image = this.BuildEditedImage();
    var before = File.ReadAllBytes(image);

    ArchiveOperations.Add(image, [new ArchiveInput("", "later/deeper/")]);

    Assert.That(File.ReadAllBytes(image), Is.EqualTo(before));
  }

  [Test, Category("Exception")]
  public void GivenAFileOfThatName_WhenAFolderIsAdded_ThenItIsRefusedAndTheImageIsUnchanged() {
    var image = this.BuildEditedImage();
    var before = File.ReadAllBytes(image);

    Assert.Catch<Exception>(() => ArchiveOperations.Add(image, [new ArchiveInput("", "pack/sub/added.txt/")]));
    Assert.That(File.ReadAllBytes(image), Is.EqualTo(before));
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenTheEditedImage_WhenE2fsckAndDebugfsReadIt_ThenItIsCleanAndHasTheFolders() {
    foreach (var tool in new[] { "e2fsck", "debugfs" })
      if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"{tool} (e2fsprogs) in WSL is needed for this oracle.");
    var image = this.BuildEditedImage();
    var path = FsInteropToolbox.WinToWsl(image);

    var fsck = FsInteropToolbox.RunWsl($"e2fsck -fn {path}");
    var later = FsInteropToolbox.RunWsl($"debugfs -R 'ls -l /later' {path}");
    var sub = FsInteropToolbox.RunWsl($"debugfs -R 'ls -l /pack/sub' {path}");

    Assert.Multiple(() => {
      Assert.That(fsck.ExitCode, Is.Zero, fsck.StdOut + fsck.StdErr);
      Assert.That(later.StdOut, Does.Contain("deeper"));
      Assert.That(sub.StdOut, Does.Contain("empty").And.Contain("Long Name.txt").And.Contain("added.txt"));
    });
  }
}
