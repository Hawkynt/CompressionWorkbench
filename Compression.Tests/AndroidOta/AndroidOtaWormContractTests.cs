#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.AndroidOta;

/// <summary>
/// Locks Android OTA payload as read-only at the modify boundary. The CrAU
/// metadata_signature field signs every byte after offset 24, and the
/// DeltaArchiveManifest protobuf embeds per-operation SHA-256 hashes over
/// the data blob — any in-place mutation invalidates both. The descriptor
/// therefore must not implement IArchiveModifiable and must not advertise
/// CanModify, and Description must spell out the spec field that blocks it.
/// </summary>
[TestFixture]
public class AndroidOtaWormContractTests {

  [Test, Category("Contract")]
  public void Descriptor_DoesNotImplementIArchiveModifiable() {
    var desc = new FileFormat.AndroidOta.AndroidOtaFormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("Contract")]
  public void Descriptor_DoesNotAdvertiseCanModify() {
    var desc = new FileFormat.AndroidOta.AndroidOtaFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Contract")]
  public void Description_NamesTheBlockingSpecField() {
    var desc = new FileFormat.AndroidOta.AndroidOtaFormatDescriptor();
    Assert.That(desc.Description, Does.Contain("metadata_signature").IgnoreCase);
    Assert.That(desc.Description, Does.Contain("DeltaArchiveManifest").IgnoreCase);
  }
}
