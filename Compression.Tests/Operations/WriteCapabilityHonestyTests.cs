#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Honesty guards for write and remux capability claims. Archive R/W remains reserved for a genuine
/// add/replace/remove path, while packet-preserving container remux is an independent capability that
/// requires <see cref="IContainerRemuxable"/> and does not imply archive mutation or fresh creation.
/// </summary>
[TestFixture]
public class WriteCapabilityHonestyTests {

  private static IEnumerable<TestCaseData> CanModifyFormats() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    foreach (var d in FormatRegistry.All.OrderBy(x => x.Id))
      if (d.Capabilities.HasFlag(FormatCapabilities.CanModify))
        yield return new TestCaseData(d.Id).SetName($"RwClaimIsBacked_{d.Id}");
  }

  private static IEnumerable<TestCaseData> CanRemuxFormats() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    foreach (var d in FormatRegistry.All.OrderBy(x => x.Id))
      if (d.Capabilities.HasFlag(FormatCapabilities.CanRemux))
        yield return new TestCaseData(d.Id).SetName($"RemuxClaimIsBacked_{d.Id}");
  }

  [TestCaseSource(nameof(CanModifyFormats))]
  public void EveryCanModifyClaimIsBackedByAModifyPath(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId);
    Assert.That(ops, Is.Not.Null, $"{formatId}: registered but exposes no ops object.");

    Assert.That(ops, Is.InstanceOf<IArchiveModifiable>(),
      $"{formatId} advertises R/W (CanModify) but its ops does not implement IArchiveModifiable — "
      + "the claim is unbacked. Implement a working existing-instance edit path "
      + "or downgrade to WORM (CanCreate only).");
  }

  [TestCaseSource(nameof(CanRemuxFormats))]
  public void EveryCanRemuxClaimIsBackedByARemuxPath(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId);
    Assert.That(ops, Is.Not.Null, $"{formatId}: registered but exposes no archive/pseudo-archive ops object.");

    Assert.That(ops, Is.InstanceOf<IContainerRemuxable>(),
      $"{formatId} advertises CanRemux but its ops does not implement IContainerRemuxable — "
      + "packet-preserving remux must be an explicit source-container operation, not inferred from "
      + "IArchiveModifiable, IFileInternalChunkMover or IAudioMuxTarget.");
  }
}