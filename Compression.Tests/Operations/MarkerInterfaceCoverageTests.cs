#pragma warning disable CS1591
using Compression.Registry;
using Compression.Tests.Support;

namespace Compression.Tests.Operations;

/// <summary>
/// Completeness guard for the maintenance-verb marker interfaces. The verb tests
/// are driven off the registry; this proves the registry is the COMPLETE set of
/// implementers, so nothing supports a verb invisibly. For every verb marker, every
/// concrete <see cref="IFormatDescriptor"/> type that implements it (found by raw
/// assembly reflection, independent of the source-generated registration) must:
///   (a) be registered in <see cref="FormatRegistry"/>, and
///   (b) expose the verb through its runtime <see cref="FormatRegistry.GetArchiveOps"/>
///       object — which is what the UI/CLI and the verb tests gate on.
/// A failure means a format silently declares a verb that no test (and no app) reaches.
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

  [TestCaseSource(nameof(Markers))]
  public void EveryImplementerIsRegisteredAndReachable(Type marker) {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    var registeredTypes = FormatRegistry.All.Select(d => d.GetType()).ToHashSet();
    var implementers = CapabilityImplementers.DescriptorTypesImplementing(marker);
    Assert.That(implementers, Is.Not.Empty, $"No descriptor implements {marker.Name} — reflection wiring broken?");

    var problems = new List<string>();
    foreach (var t in implementers) {
      // (a) the descriptor type must be registered
      var instance = FormatRegistry.All.FirstOrDefault(d => d.GetType() == t);
      if (instance == null) {
        problems.Add($"{t.Name} implements {marker.Name} but is NOT registered (verb invisible to UI/CLI/tests)");
        continue;
      }
      // (b) the runtime ops the apps gate on must also expose the verb
      var ops = FormatRegistry.GetArchiveOps(instance.Id);
      if (ops == null || !marker.IsAssignableFrom(ops.GetType()))
        problems.Add($"{t.Name} ({instance.Id}): descriptor implements {marker.Name} but GetArchiveOps does not expose it");
    }

    Assert.That(problems, Is.Empty,
      $"{marker.Name} coverage gaps:\n  " + string.Join("\n  ", problems));
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
