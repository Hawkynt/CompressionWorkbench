#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Operations.ChecksumRecord;

/// <summary>
/// Locks the honestly-read-only scope of archive formats whose record layout
/// embeds per-block CRC32 (or equivalent) checksums that cross-reference each
/// other. The universal "append a new local file header + payload at
/// end-of-stream" in-place trick that works for ZIP / ZOO / LHA does not work
/// here. These formats therefore stay create-only (WORM) rather than advertise
/// an in-place editor that would corrupt the checksum chain.
/// <para>Formats locked R-only by this contract:</para>
/// <list type="bullet">
///   <item><b>Sqx</b> — per-entry MethodHash + archive trailer checksum.</item>
///   <item><b>Wim</b> — SHA-1 per resource + integrity table.</item>
///   <item><b>Swm</b> — same as WIM, split-volume variant.</item>
///   <item><b>Ace</b> — per-record CRC32, HEAD block checksum spans subsequent metadata.</item>
/// </list>
/// <para>Note: <b>Rar</b> was promoted to R/W — its modify re-creates the archive via
/// <c>RarWriter</c> (a full repack that recomputes every CRC), so the cross-referencing
/// concern does not apply to the rebuild path. Adding an in-place (append-style) editor to
/// any format above without a checksum-aware modifier will trip this contract.</para>
/// </summary>
[TestFixture]
public class ChecksumRecordArchiveReadOnlyContractTests {

  private static IEnumerable<TestCaseData> ChecksumRecordDescriptors() {
    yield return new TestCaseData(new FileFormat.Sqx.SqxFormatDescriptor()).SetName("Sqx");
    yield return new TestCaseData(new FileFormat.Wim.WimFormatDescriptor()).SetName("Wim");
    yield return new TestCaseData(new FileFormat.Swm.SwmFormatDescriptor()).SetName("Swm");
    yield return new TestCaseData(new FileFormat.Ace.AceFormatDescriptor()).SetName("Ace");
  }

  [Test, Category("Contract"), TestCaseSource(nameof(ChecksumRecordDescriptors))]
  public void Descriptor_DoesNot_Implement_IArchiveModifiable(IFormatDescriptor descriptor) {
    Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>(),
      $"{descriptor.Id} must not advertise IArchiveModifiable — its on-disk record " +
      "layout embeds CRC/SHA checksums that cross-reference each other, and no " +
      "format-specific modifier ships yet.");
  }

  [Test, Category("Contract"), TestCaseSource(nameof(ChecksumRecordDescriptors))]
  public void Descriptor_DoesNot_Advertise_CanModify(IFormatDescriptor descriptor) {
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      $"{descriptor.Id}.Capabilities must not include CanModify — the descriptor would " +
      "be promising an Add/Remove API that does not exist.");
  }
}
