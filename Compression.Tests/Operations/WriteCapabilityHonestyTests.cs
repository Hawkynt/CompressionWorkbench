#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Honesty guard for the WORM-vs-R/W capability claim. The four-level write scale
/// (Unsupported → Read-Only → WORM → R/W) is only meaningful if <see cref="FormatCapabilities.CanModify"/>
/// is reserved for formats that genuinely modify an existing container <em>in place</em>.
/// <para>
/// A format may freely back the maintenance verbs (add / remove / purge / defragment)
/// with the verified extract → re-create rebuild (<see cref="RebuildVerb"/> /
/// <c>ModifyRebuilder</c>, surfaced through the default <see cref="IArchiveModifiable"/>
/// members or a thin wrapper). That makes the verb <em>work</em> — but it is a full
/// rewrite, i.e. WORM, not R/W. Such a format advertises <see cref="FormatCapabilities.CanCreate"/>
/// only and must NOT advertise <see cref="FormatCapabilities.CanModify"/>.
/// </para>
/// <para>
/// This test enforces the two deterministic halves of that rule for every registered
/// format that claims <see cref="FormatCapabilities.CanModify"/>:
/// </para>
/// <list type="number">
///   <item><description>its runtime ops object actually implements <see cref="IArchiveModifiable"/>
///     (otherwise the R/W claim is entirely unbacked — there is no modify path at all); and</description></item>
///   <item><description>it provides its <em>own</em> <see cref="IArchiveModifiable.Add"/> and
///     <see cref="IArchiveModifiable.Remove"/> — i.e. a hand-written in-place writer, not merely the
///     rebuild-only default interface members. (Whether that own implementation is itself genuinely
///     in-place rather than a rebuild wrapper is a code-review concern documented on the interface.)</description></item>
/// </list>
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
  public void EveryCanModifyClaimIsBackedByAGenuineInPlaceWriter(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId);
    Assert.That(ops, Is.Not.Null, $"{formatId}: registered but exposes no ops object.");

    Assert.That(ops, Is.InstanceOf<IArchiveModifiable>(),
      $"{formatId} advertises R/W (CanModify) but its ops does not implement IArchiveModifiable — "
      + "the claim is unbacked. Either implement an in-place modifier or downgrade to WORM (CanCreate only).");

    var t = ops!.GetType();
    Assert.That(ProvidesOwn(t, "Add"), Is.True,
      $"{formatId} advertises R/W (CanModify) but inherits the rebuild-only default IArchiveModifiable.Add — "
      + "that is a full-rewrite WORM path, not in-place R/W. Provide a genuine in-place Add or downgrade to WORM.");
    Assert.That(ProvidesOwn(t, "Remove"), Is.True,
      $"{formatId} advertises R/W (CanModify) but inherits the rebuild-only default IArchiveModifiable.Remove — "
      + "that is a full-rewrite WORM path, not in-place R/W. Provide a genuine in-place Remove or downgrade to WORM.");
  }

  /// <summary>True when <paramref name="opsType"/> supplies its own implementation of
  /// <c>IArchiveModifiable.<paramref name="methodName"/></c> — implicit or explicit —
  /// rather than inheriting the interface's rebuild-based default member. Uses the
  /// interface map so explicit implementations (e.g. <c>void IArchiveModifiable.Add(...)</c>)
  /// are correctly recognised as the type's own.</summary>
  private static bool ProvidesOwn(Type opsType, string methodName) {
    var map = opsType.GetInterfaceMap(typeof(IArchiveModifiable));
    for (var i = 0; i < map.InterfaceMethods.Length; i++)
      if (map.InterfaceMethods[i].Name == methodName)
        return map.TargetMethods[i].DeclaringType == opsType;
    return false;
  }
}
