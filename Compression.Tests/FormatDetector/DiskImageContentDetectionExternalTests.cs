#pragma warning disable CS1591
using Compression.Lib;
using Det = Compression.Lib.FormatDetector;

namespace Compression.Tests.FormatDetectorTests;

/// <summary>
/// Content-first disk-image detection against images the reference Linux tools build:
/// <c>sfdisk</c> partition tables, <c>mkfs.vfat</c>/<c>mkfs.ext4</c> volumes placed at partition
/// offsets, <c>xorriso</c> hybrid ISOs (MBR and GPT flavours) and <c>mkudffs</c> volumes. Skipped
/// when WSL (or, off Windows, the tool itself) is not available.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public class DiskImageContentDetectionExternalTests {

  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    FormatRegistration.EnsureInitialized();
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_diskdetect_ext_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireTools(params string[] tools) {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL is not available.");
    foreach (var tool in tools)
      if (!FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"'{tool}' is not installed in the WSL distro.");
  }

  /// <summary>Runs a bash script with the fixture directory as the working directory.</summary>
  private void Bash(string script) {
    var dir = FsInteropToolbox.WinToWsl(this._dir);
    var result = FsInteropToolbox.RunWsl($"set -e; cd {dir}; {script}");
    Assert.That(result.ExitCode, Is.EqualTo(0), $"script failed:\n{script}\n{result.StdErr}\n{result.StdOut}");
  }

  private string PathOf(string name) => Path.Combine(this._dir, name);

  [Test]
  public void Given_SfdiskGptWithVfatAndExt4_When_Detected_Then_PartitionedDiskListsBothFilesystems() {
    RequireTools("sfdisk", "mkfs.vfat", "mkfs.ext4", "mcopy");
    this.Bash(
      "mkdir -p rootfs && echo linux > rootfs/linux.txt && echo dos > dos.txt; " +
      "truncate -s 24M disk.img; " +
      "printf 'label: gpt\\nstart=2048, size=16384, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7\\nstart=18432, size=28672, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4\\n' | sfdisk -q disk.img; " +
      "mkfs.vfat --offset=2048 disk.img 8192 >/dev/null; " +
      "mcopy -i disk.img@@1M dos.txt ::/DOS.TXT; " +
      "mkfs.ext4 -q -F -d rootfs -E offset=9437184 disk.img 14M");

    var path = this.PathOf("disk.img");
    Assert.That(Det.Detect(path).ToString(), Is.EqualTo("PartitionedDisk"));
    var names = ArchiveOperations.List(path, null).Select(e => e.Name).ToList();
    Assert.That(names, Has.Some.Match("^Partition1_.*DOS\\.TXT$"), string.Join(", ", names));
    Assert.That(names, Has.Some.Match("^Partition2_.*linux\\.txt$"), string.Join(", ", names));
  }

  [Test]
  public void Given_SfdiskMbrDiskWithoutExtension_When_Detected_Then_PartitionedDisk() {
    RequireTools("sfdisk", "mkfs.vfat", "mcopy");
    this.Bash(
      "echo dos > dos.txt; truncate -s 8M sda; " +
      "printf 'label: dos\\nstart=2048, type=c\\n' | sfdisk -q sda; " +
      "mkfs.vfat --offset=2048 sda 7168 >/dev/null; mcopy -i sda@@1M dos.txt ::/DOS.TXT");

    var path = this.PathOf("sda");
    Assert.That(Det.Detect(path).ToString(), Is.EqualTo("PartitionedDisk"));
    Assert.That(ArchiveOperations.List(path, null).Select(e => e.Name), Has.Some.Match("^Partition1_.*DOS\\.TXT$"));
  }

  [Test]
  public void Given_Mkfsext4ImageNamedBin_When_Detected_Then_IsExt() {
    RequireTools("mkfs.ext4");
    this.Bash("mkdir -p rootfs && echo x > rootfs/x.txt; mkfs.ext4 -q -F -d rootfs rootfs.bin 8M");

    Assert.That(Det.Detect(this.PathOf("rootfs.bin")).ToString(), Is.EqualTo("Ext"));
  }

  [Test]
  public void Given_MkfsvfatSuperfloppyNamedRaw_When_Detected_Then_IsFat() {
    RequireTools("mkfs.vfat");
    this.Bash("truncate -s 4M floppy.raw; mkfs.vfat floppy.raw >/dev/null");

    Assert.That(Det.Detect(this.PathOf("floppy.raw")).ToString(), Is.EqualTo("Fat"));
  }

  [TestCase("")]
  [TestCase("-appended_part_as_gpt")]
  public void Given_XorrisoHybridIsoNamedImg_When_Detected_Then_IsIso(string gptFlag) {
    RequireTools("xorriso", "mkfs.vfat");
    this.Bash(
      "mkdir -p tree && echo iso > tree/iso.txt; truncate -s 2M efi.img; mkfs.vfat efi.img >/dev/null; " +
      $"xorriso -as mkisofs -quiet -o hybrid.img -partition_offset 16 -append_partition 2 0xef efi.img {gptFlag} tree");

    var path = this.PathOf("hybrid.img");
    Assert.That(Det.Detect(path).ToString(), Is.EqualTo("Iso"));
    Assert.That(ArchiveOperations.List(path, null).Select(e => e.Name), Has.Some.Match("(?i)iso\\.txt"));
  }

  [Test]
  public void Given_MkudffsVolumeNamedIso_When_Detected_Then_IsUdf() {
    RequireTools("mkudffs");
    this.Bash("truncate -s 8M dvd.iso; mkudffs --media-type=hd dvd.iso >/dev/null");

    Assert.That(Det.Detect(this.PathOf("dvd.iso")).ToString(), Is.EqualTo("Udf"));
  }
}
