#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Lib;
using FileSystem.Vmfs;

namespace Compression.Tests.Vmfs;

[TestFixture]
public sealed class VmfsTests {

  // A sparse stream long enough to hold the volume info at 1 MiB.
  private static MemoryStream Header(uint magic, uint version, uint extents = 1) {
    var image = new byte[0x100000 + 0x400];
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x100000), magic);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x100004), version);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x100000 + 0x200 + 0x90), extents);
    return new MemoryStream(image);
  }

  [Test]
  public void GivenNoVolumeInfoMagic_WhenRead_ThenItIsRejected()
    => Assert.Throws<InvalidDataException>(() => _ = new VmfsReader(Header(0x12345678, 3)));

  [TestCase(5u)]
  [TestCase(6u)]
  public void GivenAVmfs5Or6Volume_WhenRead_ThenItIsRefusedAsUnproven(uint version) {
    var ex = Assert.Throws<NotSupportedException>(() => _ = new VmfsReader(Header(VmfsReader.VolumeInfoMagic, version)));
    Assert.That(ex!.Message, Does.Contain("VMFS3"));
  }

  [Test]
  public void GivenASpannedVolume_WhenRead_ThenItIsRefused()
    => Assert.Throws<NotSupportedException>(() => _ = new VmfsReader(Header(VmfsReader.VolumeInfoMagic, 3, extents: 2)));

  [Test]
  public void GivenAVolumeWithoutFileSystemInfo_WhenRead_ThenItIsRejected() {
    // Volume info present, but the logical address space (16 MiB further on) is missing.
    Assert.Throws<InvalidDataException>(() => _ = new VmfsReader(Header(VmfsReader.VolumeInfoMagic, 3)));
  }

  [Test]
  public void GivenTheVolumeInfoMagic_WhenDetected_ThenItIsVmfs() {
    using var ms = Header(VmfsReader.VolumeInfoMagic, 3);
    Assert.That(FormatDetector.DetectByMagic(ms.ToArray()).ToString(), Is.EqualTo("Vmfs"));
  }

  /// <summary>
  /// A VMFS3 volume VMware ESX wrote in 2009: <c>test.img</c> of github.com/weafon/vmfs6-tool
  /// (a GPL-2.0 repository, so not checked in), restored with that project's
  /// <c>imager -x</c>. Its manifest is what vmfs-tools' <c>debugvmfs cat</c> returned for
  /// every file. Set <c>CWB_VMFS3_IMAGE</c> and <c>CWB_VMFS3_MANIFEST</c> to run it.
  /// </summary>
  [Test, Category("ThirdParty")]
  public void GivenAnEsxVmfs3Volume_WhenRead_ThenEveryFileMatchesVmfsTools() {
    var imagePath = Environment.GetEnvironmentVariable("CWB_VMFS3_IMAGE");
    var manifestPath = Environment.GetEnvironmentVariable("CWB_VMFS3_MANIFEST");
    if (imagePath is null || manifestPath is null || !File.Exists(imagePath) || !File.Exists(manifestPath))
      Assert.Ignore("set CWB_VMFS3_IMAGE and CWB_VMFS3_MANIFEST to the restored vmfs6-tool test volume and its debugvmfs manifest");

    using var fs = File.OpenRead(imagePath);
    using var r = new VmfsReader(fs);
    Assert.That(r.BlockSize, Is.EqualTo(1 << 20));
    var byName = r.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    var expected = File.ReadAllLines(manifestPath).Where(l => l.Length > 65).ToDictionary(l => l[65..], l => l[..64]);
    Assert.That(byName.Keys, Is.EquivalentTo(expected.Keys));
    foreach (var (name, sha) in expected) {
      using var hash = SHA256.Create();
      using var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
      r.ExtractTo(byName[name], sink);
      sink.FlushFinalBlock();
      Assert.That(Convert.ToHexStringLower(hash.Hash!), Is.EqualTo(sha), name);
    }
    Assert.That(byName["thin-flat.vmdk"].Size, Is.EqualTo(209_715_200));
  }
}
