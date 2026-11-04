using Compression.Lib;

namespace Compression.Tests.Fat;

/// <summary>
/// FAT volumes keep empty folders (on create, on add and on extraction), and the disk-image
/// containers that carry a FAT volume (VHD, VHDX, VMDK, VDI, QCOW2) keep long names and
/// folders instead of flattening everything to upper-case 8.3 leaves.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser 670a0e3, c2c3508, b398466, 5b07706.</remarks>
[TestFixture]
public sealed class FatFolderKeepingTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_fatfold_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  private string Tree() {
    Directory.CreateDirectory(Path.Combine(this._dir, "Pack", "Sub Folder", "empty"));
    File.WriteAllText(Path.Combine(this._dir, "Pack", "Sub Folder", "A long Name.txt"), "deep");
    return Path.Combine(this._dir, "Pack");
  }

  [TestCase("Fat", ".img")]
  [TestCase("Vhd", ".vhd")]
  [TestCase("Vhdx", ".vhdx")]
  [TestCase("Vmdk", ".vmdk")]
  [TestCase("Vdi", ".vdi")]
  [TestCase("Qcow2", ".qcow2")]
  [Category("HappyPath")]
  public void GivenAFolderTree_WhenAnImageIsCreated_ThenLongNamesFoldersAndEmptyFoldersAreKept(string format, string extension) {
    var image = Path.Combine(this._dir, "disk" + extension);

    ArchiveOperations.Create(image, ArchiveInput.Resolve([this.Tree()]), new CompressionOptions(), Enum.Parse<FormatDetector.Format>(format));

    var names = ArchiveOperations.List(image, null).Select(e => e.Name.Replace('\\', '/').Trim('/')).ToList();
    Assert.That(names, Is.SupersetOf(new[] { "Pack", "Pack/Sub Folder", "Pack/Sub Folder/empty", "Pack/Sub Folder/A long Name.txt" }), string.Join(", ", names));
    var outDir = Path.Combine(this._dir, "x");
    ArchiveOperations.Extract(image, outDir, null, null);
    Assert.Multiple(() => {
      Assert.That(File.ReadAllText(Path.Combine(outDir, "Pack", "Sub Folder", "A long Name.txt")), Is.EqualTo("deep"));
      Assert.That(Directory.Exists(Path.Combine(outDir, "Pack", "Sub Folder", "empty")), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAFatImage_WhenAnEmptyFolderIsAdded_ThenItIsCreatedInPlace() {
    var image = Path.Combine(this._dir, "disk.img");
    ArchiveOperations.Create(image, ArchiveInput.Resolve([this.Tree()]), new CompressionOptions(), FormatDetector.Format.Fat);

    ArchiveOperations.Add(image, [new ArchiveInput("", "later/deeper/")]);

    var names = ArchiveOperations.List(image, null).Select(e => e.Name.Replace('\\', '/').Trim('/')).ToList();
    Assert.That(names, Is.SupersetOf(new[] { "later", "later/deeper", "Pack/Sub Folder/A long Name.txt" }));
  }

  [Test, Category("Exception")]
  public void GivenAFileOfThatName_WhenAFolderIsAdded_ThenItIsRefusedAndTheImageIsUnchanged() {
    var image = Path.Combine(this._dir, "disk.img");
    ArchiveOperations.Create(image, ArchiveInput.Resolve([this.Tree()]), new CompressionOptions(), FormatDetector.Format.Fat);
    var before = File.ReadAllBytes(image);

    Assert.Throws<IOException>(() => ArchiveOperations.Add(image, [new ArchiveInput("", "Pack/Sub Folder/A long Name.txt/")]));
    Assert.That(File.ReadAllBytes(image), Is.EqualTo(before));
  }

  // ── oracles ──────────────────────────────────────────────────────────────────────────────

  [Test, Category("ExternalFsInterop")]
  public void GivenOurFatImageWithAnAddedFolder_WhenFsckVfatAndMtoolsReadIt_ThenItIsCleanAndListsTheTree() {
    foreach (var tool in new[] { "fsck.vfat", "mdir" })
      if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"{tool} in WSL is needed for this oracle.");
    var image = Path.Combine(this._dir, "disk.img");
    ArchiveOperations.Create(image, ArchiveInput.Resolve([this.Tree()]), new CompressionOptions(), FormatDetector.Format.Fat);
    ArchiveOperations.Add(image, [new ArchiveInput("", "later/deeper/")]);
    var path = FsInteropToolbox.WinToWsl(image);

    var fsck = FsInteropToolbox.RunWsl($"fsck.vfat -n {path}");
    var sub = FsInteropToolbox.RunWsl($"MTOOLS_SKIP_CHECK=1 mdir -i {path} '::/Pack/Sub Folder'");
    var later = FsInteropToolbox.RunWsl($"MTOOLS_SKIP_CHECK=1 mdir -i {path} ::/later");

    Assert.Multiple(() => {
      Assert.That(fsck.ExitCode, Is.Zero, fsck.StdOut + fsck.StdErr);
      Assert.That(sub.StdOut, Does.Contain("A long Name.txt").And.Contain("empty"));
      Assert.That(later.StdOut, Does.Contain("deeper"));
    });
  }

  [TestCase("Vhd", ".vhd", "vpc")]
  [TestCase("Vhdx", ".vhdx", "vhdx")]
  [TestCase("Vmdk", ".vmdk", "vmdk")]
  [TestCase("Vdi", ".vdi", "vdi")]
  [TestCase("Qcow2", ".qcow2", "qcow2")]
  [Category("ExternalFsInterop")]
  public void GivenOurDiskImage_WhenQemuImgConvertsItAndFsckVfatChecksTheVolume_ThenTheTreeIsThere(string format, string extension, string qemuFormat) {
    if (!FsInteropToolbox.QemuImgAnywhereAvailable) Assert.Ignore("qemu-img is needed for this oracle.");
    foreach (var tool in new[] { "fsck.vfat", "mdir" })
      if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"{tool} in WSL is needed for this oracle.");
    var image = Path.Combine(this._dir, "disk" + extension);
    ArchiveOperations.Create(image, ArchiveInput.Resolve([this.Tree()]), new CompressionOptions(), Enum.Parse<FormatDetector.Format>(format));
    var raw = Path.Combine(this._dir, "disk.raw");

    var check = FsInteropToolbox.RunQemuImg($"check -f {qemuFormat} {FsInteropToolbox.WinToWsl(image)}");
    // 63: the format has no consistency check in qemu-img (vpc); the conversion still reads it.
    Assert.That(check.ExitCode, Is.Zero.Or.EqualTo(63), check.StdOut + check.StdErr);
    var convert = FsInteropToolbox.RunQemuImg($"convert -f {qemuFormat} -O raw {FsInteropToolbox.WinToWsl(image)} {FsInteropToolbox.WinToWsl(raw)}");
    Assert.That(convert.ExitCode, Is.Zero, convert.StdOut + convert.StdErr);
    var fsck = FsInteropToolbox.RunWsl($"fsck.vfat -n {FsInteropToolbox.WinToWsl(raw)}");
    var sub = FsInteropToolbox.RunWsl($"MTOOLS_SKIP_CHECK=1 mdir -i {FsInteropToolbox.WinToWsl(raw)} '::/Pack/Sub Folder'");

    Assert.Multiple(() => {
      Assert.That(fsck.ExitCode, Is.Zero, fsck.StdOut + fsck.StdErr);
      Assert.That(sub.StdOut, Does.Contain("A long Name.txt").And.Contain("empty"));
    });
  }
}
