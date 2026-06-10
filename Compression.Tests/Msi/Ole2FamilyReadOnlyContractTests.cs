#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Msi;

/// <summary>
/// Locks the honestly-read-only scope of the OLE2 / Compound File Binary
/// descriptor family. These formats list + extract OLE2 streams via
/// <c>MsiReader</c> / CFB walk but no <c>Ole2Modifier</c> exists — sector-rewrite
/// at fixed FAT offsets only preserves <c>[0, oldLength)</c> when an existing
/// stream is mutated in place, and our reader has no such primitive. So:
/// <list type="bullet">
///   <item>None of them advertise <see cref="FormatCapabilities.CanModify"/>.</item>
///   <item>None of them implement <see cref="IArchiveModifiable"/>.</item>
/// </list>
/// A future agent that wires an Add path without first shipping an
/// <c>Ole2Modifier</c> will break this contract and the test will tell them why.
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
  public void Descriptor_DoesNot_Implement_IArchiveModifiable(IFormatDescriptor descriptor) {
    Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>(),
      $"{descriptor.Id} must not advertise IArchiveModifiable without an Ole2Modifier — " +
      "OLE2 sector-rewrite is not implemented and structural growth (new directory entries, " +
      "FAT chain extension) would break byte-identity at fixed offsets.");
  }

  [Test, Category("Contract"), TestCaseSource(nameof(Ole2Descriptors))]
  public void Descriptor_DoesNot_Advertise_CanModify(IFormatDescriptor descriptor) {
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      $"{descriptor.Id}.Capabilities must not include CanModify — no Ole2Modifier exists yet");
  }
}
