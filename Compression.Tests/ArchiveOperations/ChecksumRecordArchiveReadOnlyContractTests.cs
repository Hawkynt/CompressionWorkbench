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
///   <item><b>Wim</b> — SHA-1 per resource + integrity table; the listing is
///   content-addressed (resource_N), so even a rebuild cannot round-trip names.</item>
///   <item><b>Swm</b> — same as WIM, split-volume variant.</item>
/// </list>
/// <para>Note: <b>Rar</b>, <b>Sqx</b> and <b>Ace</b> were promoted to R/W — their modify
/// re-creates the archive via the paired writer (a full repack that recomputes every
/// CRC/hash), so the cross-referencing concern does not apply to the rebuild path; the
/// round-trip is verified by <c>ArchiveModifyRoundTripTests</c>. Adding an in-place
/// (append-style) editor to any of them without a checksum-aware modifier is still wrong —
/// only the rebuild path is blessed. The formats below remain locked because their
/// listings do not even name-round-trip.</para>
/// </summary>
[TestFixture]
public class ChecksumRecordArchiveReadOnlyContractTests {

  private static IEnumerable<TestCaseData> ChecksumRecordDescriptors() {
    yield return new TestCaseData(new FileFormat.Wim.WimFormatDescriptor()).SetName("Wim");
    yield return new TestCaseData(new FileFormat.Swm.SwmFormatDescriptor()).SetName("Swm");
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
