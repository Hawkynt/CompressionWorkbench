#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.AppImage;

/// <summary>
/// Locks AppImage as read-only at the modify boundary. AppImage is an ELF
/// stub + appended SquashFS image; SquashFS tables are indexed by absolute
/// on-disk offset, and signed AppImages carry a detached GnuPG signature in
/// an ELF section covering the entire payload. The descriptor must not
/// implement IArchiveModifiable and must not advertise CanModify.
/// </summary>
[TestFixture]
public class AppImageWormContractTests {

  [Test, Category("Contract")]
  public void Descriptor_DoesNotImplementIArchiveModifiable() {
    var desc = new FileFormat.AppImage.AppImageFormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("Contract")]
  public void Descriptor_DoesNotAdvertiseCanModify() {
    var desc = new FileFormat.AppImage.AppImageFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Contract")]
  public void Description_NamesTheBlockingContainerProperty() {
    var desc = new FileFormat.AppImage.AppImageFormatDescriptor();
    Assert.That(desc.Description, Does.Contain("SquashFS"));
    Assert.That(desc.Description, Does.Contain("write-once").IgnoreCase);
  }
}
