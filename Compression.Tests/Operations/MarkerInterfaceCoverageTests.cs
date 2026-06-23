#pragma warning disable CS1591
using System.Reflection;
using Compression.Registry;
using Compression.Tests.Support;

namespace Compression.Tests.Operations;

/// <summary>
/// Completeness guard for the maintenance-verb marker interfaces. The verb tests
/// are driven off the registry; this proves no <em>registered</em> format supports a
/// verb invisibly. For every verb marker, every concrete <see cref="IFormatDescriptor"/>
/// type that implements it (found by raw assembly reflection, independent of the
/// source-generated registration) that is also registered in <see cref="FormatRegistry"/>
/// must expose the verb through its runtime <see cref="FormatRegistry.GetArchiveOps"/>
/// object — which is what the UI/CLI and the verb tests gate on. A failure there means a
/// shipped format silently declares a verb that no test (and no app) reaches.
/// <para>
/// Implementers that are <em>not</em> registered belong to the un-promoted filesystem
/// tier (built and unit-tested in isolation but deliberately kept out of the registered
/// bundle — they have no <c>FormatDetector</c> entry, so the registry, UI, CLI and verb
/// round-trip tests cannot reach them by construction). They are reported for visibility
/// but are not a coverage gap in the shipped surface.
/// </para>
/// <para>
/// The reflection scan is made deterministic by force-loading every format assembly up
/// front: otherwise the implementer set would depend on which other tests had already
/// dragged a given <c>FileSystem.*</c> / <c>FileFormat.*</c> assembly into the AppDomain,
/// making this fixture pass in isolation but fail in the full suite (or vice versa).
/// </para>
/// </summary>
[TestFixture]
public class MarkerInterfaceCoverageTests {

  private static readonly (string Name, Type Marker)[] VerbMarkers = [
    ("shrink", typeof(IArchiveShrinkable)),
    ("defrag", typeof(IArchiveDefragmentable)),
    ("wipe", typeof(IWipeEmpty)),
    ("purge/modify", typeof(IArchiveModifiable)),
    ("optimize-layout", typeof(ILayoutOptimizable)),
  ];

  private static IEnumerable<TestCaseData> Markers() =>
    VerbMarkers.Select(m => new TestCaseData(m.Marker).SetName($"MarkerCoverage_{m.Name}"));

  /// <summary>Force-load every format assembly next to the test binary so the reflection
  /// scan sees the same implementer set on every run, independent of test ordering.</summary>
  [OneTimeSetUp]
  public void LoadAllFormatAssemblies() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var dir = Path.GetDirectoryName(typeof(MarkerInterfaceCoverageTests).Assembly.Location)!;
    foreach (var dll in Directory.GetFiles(dir, "*.dll")) {
      var name = Path.GetFileNameWithoutExtension(dll);
      if (name.Contains("FileFormat") || name.Contains("FileSystem") || name.StartsWith("Compression"))
        try { Assembly.LoadFrom(dll); } catch { /* unmanaged / already-loaded — ignore */ }
    }
  }

  [TestCaseSource(nameof(Markers))]
  public void EveryImplementerIsRegisteredAndReachable(Type marker) {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    var implementers = CapabilityImplementers.DescriptorTypesImplementing(marker);
    Assert.That(implementers, Is.Not.Empty, $"No descriptor implements {marker.Name} — reflection wiring broken?");

    var problems = new List<string>();
    var unpromoted = new List<string>();
    foreach (var t in implementers) {
      var instance = FormatRegistry.All.FirstOrDefault(d => d.GetType() == t);
      if (instance == null) {
        // Un-promoted tier: declares the verb but is not part of the registered/detectable
        // surface, so it is unreachable by construction — informational, not a gap.
        unpromoted.Add(t.Name);
        continue;
      }
      // Registered ⇒ the runtime ops the apps gate on must also expose the verb.
      var ops = FormatRegistry.GetArchiveOps(instance.Id);
      if (ops == null || !marker.IsAssignableFrom(ops.GetType()))
        problems.Add($"{t.Name} ({instance.Id}): descriptor implements {marker.Name} but GetArchiveOps does not expose it");
    }

    if (unpromoted.Count > 0)
      TestContext.Out.WriteLine(
        $"{marker.Name}: {unpromoted.Count} un-promoted implementer(s) not in the registered bundle: "
        + string.Join(", ", unpromoted.OrderBy(n => n)));

    Assert.That(problems, Is.Empty,
      $"{marker.Name} coverage gaps (registered format hides the verb):\n  " + string.Join("\n  ", problems));
  }

  /// <summary>The registry-driven verb source (what the tests use) must be at least
  /// as complete as raw reflection: every descriptor type implementing the marker is
  /// reachable through the registry. The registry may legitimately expose MORE (a
  /// descriptor delegating to a separate ops object that carries the verb), so this
  /// is a superset check, not strict equality.</summary>
  [TestCaseSource(nameof(Markers))]
  public void RegistryDrivenSourceIsSupersetOfReflection(Type marker) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var reachableIds = CapabilityImplementers.RegisteredIdsExposing(marker).ToHashSet();
    var reflectionTypes = CapabilityImplementers.DescriptorTypesImplementing(marker);
    var unreachable = reflectionTypes
      .Select(t => FormatRegistry.All.FirstOrDefault(d => d.GetType() == t))
      .Where(d => d != null && !reachableIds.Contains(d!.Id))
      .Select(d => d!.GetType().Name)
      .ToList();
    Assert.That(unreachable, Is.Empty,
      $"{marker.Name}: these implementers aren't reachable via the registry-driven source: {string.Join(", ", unreachable)}");
    Assert.That(reachableIds, Is.Not.Empty, $"{marker.Name}: registry-driven source is empty");
  }
}
