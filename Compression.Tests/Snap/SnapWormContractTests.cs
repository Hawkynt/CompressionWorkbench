#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Snap;

/// <summary>
/// Locks Canonical Snap as read-only at the modify boundary. Snap is a
/// SquashFS image whose inode/directory/fragment tables are indexed by
/// absolute on-disk offset; any in-place mutation would require rewriting
/// every shifted table, and snapd separately verifies the per-snap SHA-3-384
/// hash in the assertions chain. The descriptor must not implement
/// IArchiveModifiable and must not advertise CanModify.
/// </summary>
[TestFixture]
public class SnapWormContractTests {

  [Test, Category("Contract")]
  public void Descriptor_DoesNotImplementIArchiveModifiable() {
    var desc = new FileFormat.Snap.SnapFormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("Contract")]
  public void Descriptor_DoesNotAdvertiseCanModify() {
    var desc = new FileFormat.Snap.SnapFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Contract")]
  public void Description_NamesTheBlockingContainerProperty() {
    var desc = new FileFormat.Snap.SnapFormatDescriptor();
    Assert.That(desc.Description, Does.Contain("SquashFS"));
    Assert.That(desc.Description, Does.Contain("write-once").IgnoreCase);
  }
}
