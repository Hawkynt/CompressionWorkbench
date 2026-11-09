using System.Security.Cryptography;
using System.Text;
using FileFormat.PartImage;

namespace Compression.Tests.PartImage;

[TestFixture]
public class PartImageTests {
  /// <summary>SHA-256 of the 2 MiB ext2 volume the reference vectors were saved from.</summary>
  private const string VolumeSha256 = "8fd3cc1aab87625b6fe633c986516c6e3a1b34e7d7fb82983ff8f6ac08e3777e";

  private static string VectorPath(string name)
    => Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "PartImage", "ReferenceVectors", name);

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-pi-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private string Extract(byte[] image) {
    var dir = Path.Combine(this._tmp, Guid.NewGuid().ToString("N")[..8]);
    new PartImageFormatDescriptor().Extract(new MemoryStream(image), dir, null, null);
    return dir;
  }

  [TestCase("ext2-z0.000")]
  [TestCase("ext2-z1.000")]
  [TestCase("ext2-z2.000")]
  public void GivenAPartimageImage_WhenExtracted_ThenThePartitionIsTheSavedVolume(string name) {
    var dir = this.Extract(File.ReadAllBytes(VectorPath(name)));
    var partition = File.ReadAllBytes(Path.Combine(dir, "partition.img"));
    Assert.That(partition, Has.Length.EqualTo(2 * 1024 * 1024));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(partition)), Is.EqualTo(VolumeSha256));
    var ini = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
    Assert.That(ini, Does.Contain("filesystem=ext2fs").And.Contain("label=CWBPI").And.Contain("block_size=1024").And.Contain("block_count=2048"));
  }

  [Test]
  public void GivenAnImage_WhenListed_ThenTheHeadersGiveThePartitionSize() {
    var entries = new PartImageFormatDescriptor().List(new MemoryStream(File.ReadAllBytes(VectorPath("ext2-z1.000"))), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "partition.img" }));
    Assert.That(entries[1].OriginalSize, Is.EqualTo(2 * 1024 * 1024));
    Assert.That(entries[1].Method, Is.EqualTo("gzip"));
  }

  [Test]
  public void GivenACorruptedBlock_WhenExtracted_ThenTheCrcRefusesItAndNothingIsLeftBehind() {
    var image = File.ReadAllBytes(VectorPath("ext2-z0.000"));
    var data = Encoding.ASCII.GetBytes("MAGIC-BEGIN-DATABLOCKS");
    var at = image.AsSpan().IndexOf(data) + data.Length;
    image[at + 100] ^= 0x40;
    var dir = Path.Combine(this._tmp, "bad");
    Assert.Throws<InvalidDataException>(() => new PartImageFormatDescriptor().Extract(new MemoryStream(image), dir, null, null));
    Assert.That(File.Exists(Path.Combine(dir, "partition.img")), Is.False);
  }

  [Test]
  public void GivenAChangedMainHeader_WhenListed_ThenTheChecksumRejectsItAndListFallsBack() {
    var image = File.ReadAllBytes(VectorPath("ext2-z0.000"));
    image[512 + 600] ^= 1;
    Assert.That(new PartImageFormatDescriptor().List(new MemoryStream(image), null).Single().Name, Is.EqualTo("FULL.partimg"));
  }

  [Test]
  public void GivenATruncatedImage_WhenExtracted_ThenInvalidData() {
    var image = File.ReadAllBytes(VectorPath("ext2-z0.000"))[..^20000];
    Assert.Throws<InvalidDataException>(() => new PartImageFormatDescriptor().Extract(new MemoryStream(image), Path.Combine(this._tmp, "t"), null, null));
  }

  [Test]
  public void GivenAChangedTailSum_WhenExtracted_ThenInvalidData() {
    var image = File.ReadAllBytes(VectorPath("ext2-z0.000"));
    var tail = image.AsSpan().LastIndexOf("MAGIC-BEGIN-TAIL"u8) + 16;
    image[tail] ^= 1;
    Assert.Throws<InvalidDataException>(() => new PartImageFormatDescriptor().Extract(new MemoryStream(image), Path.Combine(this._tmp, "s"), null, null));
  }

  [Test]
  public void GivenALaterVolume_WhenListed_ThenItIsNotTakenForAWholeImage() {
    var image = File.ReadAllBytes(VectorPath("ext2-z0.000"));
    image[96] = 1;
    Assert.That(new PartImageFormatDescriptor().List(new MemoryStream(image), null).Single().Name, Is.EqualTo("FULL.partimg"));
  }

  [Test]
  public void GivenNoVolumeMagic_WhenListed_ThenListFallsBack()
    => Assert.That(new PartImageFormatDescriptor().List(new MemoryStream(new byte[2048]), null).Single().Name, Is.EqualTo("FULL.partimg"));
}

/// <summary>
/// partimage 0.6.9 as the oracle: it saves ext2, FAT16 and FAT32 volumes (plain, gzip and bzip2) from
/// loop devices and restores each image onto a zeroed device; the partition we extract from the same
/// image equals what partimage restores. Needs root for the loop devices: passwordless sudo, or
/// <c>CWB_WSL_SUDO_PASSWORD</c> for a local WSL. Skipped without partimage unless
/// <c>CWB_REQUIRE_PARTIMAGE=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class PartImageExternalConformanceTests {
  private static string Sudo() {
    var password = Environment.GetEnvironmentVariable("CWB_WSL_SUDO_PASSWORD");
    return string.IsNullOrEmpty(password) ? "sudo -n" : $"echo '{password}' | sudo -S -p ''";
  }

  private static void Require() {
    var ok = FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("partimage") && FsInteropToolbox.WslHasTool("mkfs.fat")
             && FsInteropToolbox.RunWsl($"{Sudo()} true").ExitCode == 0;
    if (ok) return;
    const string hint = "partimage, dosfstools or root are missing: `sudo apt-get install -y partimage dosfstools`, passwordless sudo.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_PARTIMAGE") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-pix-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  [TestCase("ext2", "4M", 0)]
  [TestCase("ext2", "4M", 1)]
  [TestCase("ext2", "4M", 2)]
  [TestCase("fat16", "16M", 1)]
  [TestCase("fat32", "40M", 0)]
  public void GivenPartimageSavedAVolume_WhenWeExtract_ThenItEqualsPartimagesRestore(string fs, string size, int z) {
    Require();
    var sudo = Sudo();
    var mkfs = fs switch {
      "ext2" => "mke2fs -q -t ext2 -b 1024 -F",
      "fat16" => "mkfs.fat -F 16",
      _ => "mkfs.fat -F 32",
    };
    // The volumes and loop devices live in a native Linux directory; the results are copied out.
    var script = string.Join("; ",
      "set -e",
      "w=$(mktemp -d)",
      $"truncate -s {size} $w/fs.img",
      $"{mkfs} $w/fs.img >/dev/null",
      "mkdir $w/m",
      $"L=$({sudo} losetup -f --show $w/fs.img)",
      $"{sudo} mount $L $w/m",
      $"{sudo} sh -c \"head -c 300000 /dev/urandom > $w/m/RANDOM.BIN; seq 1 5000 > $w/m/NUMBERS.TXT\"",
      $"{sudo} umount $w/m",
      $"{sudo} partimage -b -z{z} -o -d save $L $w/img >/dev/null 2>&1",
      $"{sudo} losetup -d $L",
      $"truncate -s {size} $w/back.img",
      $"L=$({sudo} losetup -f --show $w/back.img)",
      $"{sudo} partimage -b restore $L $w/img.000 >/dev/null 2>&1",
      $"{sudo} losetup -d $L",
      $"{sudo} cp $w/img.000 $w/back.img {FsInteropToolbox.WinToWsl(this._tmp)}/",
      $"{sudo} rm -rf $w");
    var r = FsInteropToolbox.RunWsl(script);
    TestContext.Out.WriteLine($"{r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    Assert.That(r.ExitCode, Is.EqualTo(0));

    var dir = Path.Combine(this._tmp, "ours");
    using (var image = File.OpenRead(Path.Combine(this._tmp, "img.000")))
      new PartImageFormatDescriptor().Extract(image, dir, null, null);
    Assert.That(File.ReadAllBytes(Path.Combine(dir, "partition.img")), Is.EqualTo(File.ReadAllBytes(Path.Combine(this._tmp, "back.img"))));
  }
}
