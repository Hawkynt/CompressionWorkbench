#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.StuffItX;

/// <summary>
/// Locks StuffIt X as create-only at the modify boundary. The element-stream
/// uses proprietary compression methods (Brimstone PPMd, Darkhorse LZSS,
/// Cyanide/Iron BWT) with no public spec, so our writer emits only a single
/// embedded opaque payload at the catalog offset. Even a stored append would
/// shift catalog absolute offsets stored in the P2 length headers of every
/// later element. The descriptor advertises CanCreate but must not advertise
/// CanModify.
/// </summary>
[TestFixture]
public class StuffItXWormContractTests {

  [Test, Category("Contract")]
  public void Descriptor_AdvertisesCanCreate() {
    var desc = new FileFormat.StuffItX.StuffItXFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(desc, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("Contract")]
  public void Descriptor_DoesNotImplementIArchiveModifiable() {
    var desc = new FileFormat.StuffItX.StuffItXFormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("Contract")]
  public void Descriptor_DoesNotAdvertiseCanModify() {
    var desc = new FileFormat.StuffItX.StuffItXFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Contract")]
  public void Description_NamesTheBlockingSpecAndWriterScope() {
    var desc = new FileFormat.StuffItX.StuffItXFormatDescriptor();
    Assert.That(desc.Description, Does.Contain("element-stream").Or.Contain("element"));
    Assert.That(desc.Description, Does.Contain("P2"));
  }
}
