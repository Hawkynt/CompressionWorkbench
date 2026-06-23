#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Honesty guard for the WORM-vs-R/W capability claim. The write scale
/// (Unsupported → Read-Only → WORM → R/W) is only meaningful if
/// <see cref="FormatCapabilities.CanModify"/> is reserved for formats that genuinely
/// support modifying an <em>existing</em> container.
/// <para>
/// <b>R/W means a working add / replace / remove on an existing instance that yields a
/// valid result.</b> The edit may be byte-preserving in place <em>or</em> may relayout /
/// re-pack the container (moving existing data) — both are honest R/W for a conceptually
/// read-write format. What is NOT honest is advertising <see cref="FormatCapabilities.CanModify"/>
/// with no working modify path at all. Read-only-by-design formats (CramFS, SquashFS) and
/// create-only formats stay WORM (<see cref="FormatCapabilities.CanCreate"/>) even though a
/// rebuild could synthesise a modified copy — they do not advertise R/W.
/// </para>
/// <para>
/// This test enforces the deterministic half of that rule for every registered format that
/// claims <see cref="FormatCapabilities.CanModify"/>: its runtime ops object must actually
/// implement <see cref="IArchiveModifiable"/> (otherwise the R/W claim is entirely unbacked —
/// there is no modify path). That the modify <em>works</em> (round-trips) is verified
/// separately by the registry-driven <c>Generic{Purge,Defrag}RoundTripTests</c>.
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
      + "the claim is unbacked. Implement IArchiveModifiable (in-place or relayout/rebuild) "
      + "or downgrade to WORM (CanCreate only).");
  }
}
