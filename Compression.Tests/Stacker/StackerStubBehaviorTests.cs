#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// Pins the capability surface for <see cref="StackerFormatDescriptor"/>. Stacker
/// STACVOL is a functional read/create/edit tier: banner + Stacker Control Block
/// parsing, inner FAT12 directory walking, and STORED/Stac-LZS cluster I/O. These
/// tests guard the advertised capabilities and the genuine-empty-volume surface.
/// </summary>
[TestFixture]
public class StackerStubBehaviorTests {

  private static byte[] BuildEmptyBannerVolume() {
    // Banner-only image (no SCB) used to confirm the surface degrades gracefully
    // when there is no parseable inner volume.
    var image = new byte[4 * 512];
    var banner = "STACKER  version  3    volume:  C:\\STACVOL.DSK";
    System.Text.Encoding.ASCII.GetBytes(banner).CopyTo(image, 0);
    for (var i = banner.Length; i < 0x4d; i++) image[i] = (byte)' ';
    image[0x4d] = 0x0D; image[0x4e] = 0x0A; image[0x4f] = 0x1A;
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesReadCreateAndModify() {
    var d = new StackerFormatDescriptor();

    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True,
      "Stacker emits valid STACVOL volumes — must advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True,
      "Flavor-preserving existing-image Add/Replace/Remove is R/W even when implemented by rebuild.");
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("Sad")]
  public void BannerOnly_NoScb_IsInvalid() {
    var image = BuildEmptyBannerVolume();
    using var r = new StackerReader(new MemoryStream(image));
    // Banner present but no Stacker Control Block at sector 2 => not a full volume.
    Assert.That(r.ValidHeader, Is.False);
    Assert.That(r.Entries, Is.Empty);
  }
}
