using System.Collections.Concurrent;

namespace Compression.Registry;

/// <summary>
/// Registration point for format assemblies whose optimization transform needs
/// writer-specific entry semantics that do not belong in the generic registry.
/// </summary>
public static class FilesystemOptimizationAdapters {
  /// <summary>Writer-specific symbolic-link deduplication rebuild.</summary>
  public delegate void SymbolicLinkDeduplicator(
    ILayoutOptimizable layout,
    Stream source,
    Stream target,
    LayoutRebuildOptions options);

  private static readonly ConcurrentDictionary<Type, SymbolicLinkDeduplicator> SymbolicLinkDeduplicators = new();

  /// <summary>
  /// Registers symbolic-link deduplication for one concrete descriptor type.
  /// Registration is idempotent; a later registration for the same type replaces
  /// the previous delegate, which keeps hot-reload/test assembly scenarios deterministic.
  /// </summary>
  public static void RegisterSymbolicLinkDeduplicator<T>(SymbolicLinkDeduplicator rebuild)
    where T : ILayoutOptimizable {
    ArgumentNullException.ThrowIfNull(rebuild);
    SymbolicLinkDeduplicators[typeof(T)] = rebuild;
  }

  internal static bool TryGetSymbolicLinkDeduplicator(
    ILayoutOptimizable layout,
    out SymbolicLinkDeduplicator rebuild)
    => SymbolicLinkDeduplicators.TryGetValue(layout.GetType(), out rebuild!);
}
