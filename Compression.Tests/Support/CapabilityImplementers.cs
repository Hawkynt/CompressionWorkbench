#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Support;

/// <summary>
/// Reflection-based discovery of every format that implements a maintenance-verb
/// marker interface, so coverage tracks the code automatically — a new format
/// that implements <c>IArchiveShrinkable</c> / <c>IArchiveDefragmentable</c> /
/// <c>IWipeEmpty</c> / <c>IArchiveModifiable</c> / <c>ILayoutOptimizable</c> is
/// picked up by the verb tests with no edit, and one that implements a verb but
/// escapes the registry is caught by the completeness guard.
/// </summary>
public static class CapabilityImplementers {

  /// <summary>
  /// Every concrete (non-abstract) <see cref="IFormatDescriptor"/> type, across
  /// all loaded format assemblies, that is assignable to <paramref name="marker"/>.
  /// Pure assembly reflection — independent of the registry/source-generator.
  /// </summary>
  public static IReadOnlyList<Type> DescriptorTypesImplementing(Type marker) {
    Compression.Lib.FormatRegistration.EnsureInitialized(); // forces the format assemblies to load
    var result = new List<Type>();
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
      var name = asm.GetName().Name ?? "";
      // Format assemblies are variously named "FileFormat.X", "FileSystem.X",
      // "CompressionWorkbench.FileFormat.X", "Compression.Registry", etc. Match on
      // the substrings rather than a leading prefix so none are missed.
      if (!name.Contains("FileFormat", StringComparison.Ordinal)
          && !name.Contains("FileSystem", StringComparison.Ordinal)
          && !name.StartsWith("Compression", StringComparison.Ordinal)) continue;
      Type[] types;
      try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
      foreach (var t in types) {
        if (t is null || t.IsAbstract || t.IsInterface) continue;
        if (typeof(IFormatDescriptor).IsAssignableFrom(t) && marker.IsAssignableFrom(t))
          result.Add(t);
      }
    }
    return result;
  }

  /// <summary>
  /// Registered format ids whose <see cref="FormatRegistry.GetArchiveOps"/> object
  /// implements <paramref name="marker"/> — i.e. the verb is actually reachable at
  /// runtime (which is what the UI/CLI gate on). This is the canonical source for
  /// the verb test cases.
  /// </summary>
  public static IEnumerable<string> RegisteredIdsExposing(Type marker) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    foreach (var d in FormatRegistry.All.OrderBy(x => x.Id)) {
      var ops = FormatRegistry.GetArchiveOps(d.Id);
      if (ops != null && marker.IsAssignableFrom(ops.GetType()))
        yield return d.Id;
    }
  }

  /// <summary>True when the format id's registry ops implements its own (declared) verb method
  /// rather than inheriting the default — used to scope tests to the default mechanism.</summary>
  public static bool DeclaresOwn(string formatId, string methodName, params Type[] paramTypes) {
    var ops = FormatRegistry.GetArchiveOps(formatId);
    if (ops == null) return false;
    var t = ops.GetType();
    var m = t.GetMethod(methodName, paramTypes);
    return m != null && m.DeclaringType == t;
  }
}
