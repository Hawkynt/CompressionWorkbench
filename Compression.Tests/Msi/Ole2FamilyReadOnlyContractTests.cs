#pragma warning disable CS1591
using System.Reflection;
using Compression.Registry;

namespace Compression.Tests.Msi;

/// <summary>
/// Locks the modify contract of the OLE2 / Compound File Binary descriptor
/// family. These formats list + extract OLE2 streams via <c>MsiReader</c> /
/// CFB walk. No <c>Ole2Modifier</c> exists — sector-rewrite at fixed FAT
/// offsets only preserves <c>[0, oldLength)</c> when an existing stream is
/// mutated in place, and our reader has no such primitive. Their R/W claim is
/// therefore served exclusively by the verified extract → edit → re-create
/// rebuild (the default <see cref="IArchiveModifiable"/>), which rewrites the
/// whole compound file through the paired writer and is honest R/W under the
/// relayout-allowed policy (see <c>WriteCapabilityHonestyTests</c>). So:
/// <list type="bullet">
///   <item>Each descriptor advertises <see cref="FormatCapabilities.CanModify"/>
///   backed by <see cref="IArchiveModifiable"/> (the claim is never unbacked).</item>
///   <item>None of them declares a bespoke in-place <c>Add</c>/<c>Remove</c> —
///   an in-place path requires an <c>Ole2Modifier</c> first. A future agent that
///   overrides the default rebuild without shipping one breaks this contract
///   and the test will tell them why.</item>
/// </list>
/// The behavioural round-trip (create → Add → Remove → byte-identical extract)
/// is covered by <c>ArchiveModifyRoundTripTests</c>.
/// </summary>
[TestFixture]
public class Ole2FamilyReadOnlyContractTests {

  private static IEnumerable<TestCaseData> Ole2Descriptors() {
    yield return new TestCaseData(new FileFormat.Msi.MsiFormatDescriptor()).SetName("Msi");
    yield return new TestCaseData(new FileFormat.Doc.DocFormatDescriptor()).SetName("Doc");
    yield return new TestCaseData(new FileFormat.Xls.XlsFormatDescriptor()).SetName("Xls");
    yield return new TestCaseData(new FileFormat.Ppt.PptFormatDescriptor()).SetName("Ppt");
    yield return new TestCaseData(new FileFormat.Msg.MsgFormatDescriptor()).SetName("Msg");
    yield return new TestCaseData(new FileFormat.ThumbsDb.ThumbsDbFormatDescriptor()).SetName("ThumbsDb");
  }

  [Test, Category("Contract"), TestCaseSource(nameof(Ole2Descriptors))]
  public void Descriptor_Backs_CanModify_With_IArchiveModifiable(IFormatDescriptor descriptor) {
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True,
      $"{descriptor.Id}.Capabilities must include CanModify — the rebuild-backed modify path is honest R/W");
    Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>(),
      $"{descriptor.Id} advertises CanModify, so it must implement IArchiveModifiable");
  }

  [Test, Category("Contract"), TestCaseSource(nameof(Ole2Descriptors))]
  public void Descriptor_DoesNot_Claim_InPlace_Modify_Without_Ole2Modifier(IFormatDescriptor descriptor) {
    var t = descriptor.GetType();
    var ownAdd = t.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
      [typeof(Stream), typeof(IReadOnlyList<ArchiveInputInfo>)]);
    var ownRemove = t.GetMethod("Remove", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
      [typeof(Stream), typeof(string[])]);
    Assert.That(ownAdd, Is.Null,
      $"{descriptor.Id} declares its own Add — an in-place OLE2 edit requires an Ole2Modifier " +
      "(sector-rewrite at fixed FAT offsets is not implemented); keep the default verified rebuild.");
    Assert.That(ownRemove, Is.Null,
      $"{descriptor.Id} declares its own Remove — an in-place OLE2 edit requires an Ole2Modifier " +
      "(sector-rewrite at fixed FAT offsets is not implemented); keep the default verified rebuild.");
  }
}
