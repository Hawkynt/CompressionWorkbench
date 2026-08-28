#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Honesty guard for the WORM-vs-R/W capability claim. The write scale
/// (Unsupported → Read-Only → WORM → R/W) is only meaningful if
/// <see cref="FormatCapabilities.CanModify"/> is reserved for formats that genuinely
/// support modifying an <em>existing</em> container/image.
/// <para>
/// <b>R/W means a working add / replace / remove on an existing instance that yields a
/// valid result.</b> The edit may be byte-preserving in place, append replacement state,
/// relayout members, or rebuild the image. Those are implementation choices. What is not
/// honest is advertising <see cref="FormatCapabilities.CanModify"/> with no working edit path.
/// A format that can only create a fresh instance remains WORM.
/// </para>
/// <para>
/// This test enforces the deterministic half of that rule for every registered claimant:
/// its runtime ops object must implement <see cref="IArchiveModifiable"/>. Behavioural
/// round-trip tests verify that individual modify paths actually work.
/// </para>
/// </summary>
[TestFixture]
public class WriteCapabilityHonestyTests {

  private static IEnumerable<TestCaseData> CanModifyFormats() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    foreach (var d in FormatRegistry.All.OrderBy(x => x.Id))
      if (d.Capabilities.HasFlag(FormatCapabilities.CanModify))
        yield return new TestCaseData(d.Id).SetName($"RwClaimIsBacked_{d.Id}");
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
}