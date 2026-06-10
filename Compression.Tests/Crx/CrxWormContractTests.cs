#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Crx;

/// <summary>
/// Locks Chrome CRX as create-only at the modify boundary. The CRX3
/// SignedData protobuf wraps RSA/ECDSA signatures over signed_header_data
/// plus the ZIP body; any in-place mutation of the trailing ZIP invalidates
/// every signature. CRX advertises CanCreate (empty SignedData → not
/// browser-loadable but valid container) and must not advertise CanModify.
/// </summary>
[TestFixture]
public class CrxWormContractTests {

  [Test, Category("Contract")]
  public void Descriptor_AdvertisesCanCreate() {
    var desc = new FileFormat.Crx.CrxFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(desc, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("Contract")]
  public void Descriptor_DoesNotImplementIArchiveModifiable() {
    var desc = new FileFormat.Crx.CrxFormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("Contract")]
  public void Descriptor_DoesNotAdvertiseCanModify() {
    var desc = new FileFormat.Crx.CrxFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Contract")]
  public void Description_NamesTheBlockingSpecField() {
    var desc = new FileFormat.Crx.CrxFormatDescriptor();
    Assert.That(desc.Description, Does.Contain("SignedData").IgnoreCase);
    Assert.That(desc.Description, Does.Contain("signature").IgnoreCase);
  }
}
