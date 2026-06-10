#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Wbn;

/// <summary>
/// Locks Web Bundle (.wbn) as read-only at the modify boundary. The CBOR
/// `sections-lengths` byte string and `index` map record absolute byte
/// offsets and lengths of every section body; any in-place mutation of a
/// section shifts every later offset and would require re-encoding both
/// indices. Signed Web Bundles additionally carry an Ed25519 signature in
/// the `authorities` section over the manifest. The descriptor must not
/// implement IArchiveModifiable and must not advertise CanModify.
/// </summary>
[TestFixture]
public class WbnWormContractTests {

  [Test, Category("Contract")]
  public void Descriptor_DoesNotImplementIArchiveModifiable() {
    var desc = new FileFormat.Wbn.WbnFormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("Contract")]
  public void Descriptor_DoesNotAdvertiseCanModify() {
    var desc = new FileFormat.Wbn.WbnFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Contract")]
  public void Description_NamesTheBlockingSpecField() {
    var desc = new FileFormat.Wbn.WbnFormatDescriptor();
    Assert.That(desc.Description, Does.Contain("sections-lengths"));
    Assert.That(desc.Description, Does.Contain("index"));
  }
}
