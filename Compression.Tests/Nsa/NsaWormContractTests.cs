#pragma warning disable CS1591
using System.Reflection;
using Compression.Registry;

namespace Compression.Tests.Nsa;

/// <summary>
/// Locks the modify contract of NScripter NSA. The 6-byte header carries a
/// `uint32 BE data_offset` pointing past the variable-length index; appending an
/// index entry shifts data_offset and every byte after it, so a genuine in-place
/// edit is impossible by header design. The R/W claim is therefore served
/// exclusively by the verified extract → edit → re-create rebuild (the default
/// <see cref="IArchiveModifiable"/>), which is honest R/W under the
/// relayout-allowed policy (see <c>WriteCapabilityHonestyTests</c>). Creation is
/// stored only — the LZSS/NBZ decoders have no paired encoders.
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
  public void Descriptor_Backs_CanModify_With_IArchiveModifiable() {
    var desc = new FileFormat.Nsa.NsaFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True,
      "NSA.Capabilities must include CanModify — the rebuild-backed modify path is honest R/W");
    Assert.That(desc, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("Contract")]
  public void Descriptor_DoesNot_Claim_InPlace_Modify() {
    // data_offset shifts on every index change, so an in-place editor cannot
    // exist without rewriting the whole data area; the default verified rebuild
    // must not be overridden by a pretend-in-place path.
    var t = typeof(FileFormat.Nsa.NsaFormatDescriptor);
    Assert.That(t.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
      [typeof(Stream), typeof(IReadOnlyList<ArchiveInputInfo>)]), Is.Null);
    Assert.That(t.GetMethod("Remove", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
      [typeof(Stream), typeof(string[])]), Is.Null);
  }

  [Test, Category("Contract")]
  public void Description_NamesTheBlockingHeaderField() {
    var desc = new FileFormat.Nsa.NsaFormatDescriptor();
    Assert.That(desc.Description, Does.Contain("data_offset"));
  }
}
