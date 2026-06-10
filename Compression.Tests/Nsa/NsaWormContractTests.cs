#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Nsa;

/// <summary>
/// Locks NScripter NSA as create-only at the modify boundary. The 6-byte
/// header carries a `uint32 BE data_offset` pointing past the variable-length
/// index; appending an index entry shifts data_offset and every byte after
/// it, so pre-existing entry bytes cannot stay at their original on-disk
/// offsets. NSA advertises CanCreate (stored only — LZSS/NBZ decoders have
/// no paired encoders) and must not advertise CanModify.
/// </summary>
[TestFixture]
public class NsaWormContractTests {

  [Test, Category("Contract")]
  public void Descriptor_AdvertisesCanCreate() {
    var desc = new FileFormat.Nsa.NsaFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(desc, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("Contract")]
  public void Descriptor_DoesNotImplementIArchiveModifiable() {
    var desc = new FileFormat.Nsa.NsaFormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("Contract")]
  public void Descriptor_DoesNotAdvertiseCanModify() {
    var desc = new FileFormat.Nsa.NsaFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Contract")]
  public void Description_NamesTheBlockingHeaderField() {
    var desc = new FileFormat.Nsa.NsaFormatDescriptor();
    Assert.That(desc.Description, Does.Contain("data_offset"));
  }
}
