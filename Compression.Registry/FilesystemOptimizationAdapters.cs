using System.Collections.Concurrent;

namespace Compression.Registry;

/// <summary>
/// Registration point for format assemblies whose optimization transform needs
/// writer-specific semantics that do not belong in the generic registry.
/// </summary>
public static class FilesystemOptimizationAdapters {
  /// <summary>Writer-specific symbolic-link deduplication rebuild.</summary>
  public delegate void SymbolicLinkDeduplicator(
    ILayoutOptimizable layout,
    Stream source,
    Stream target,
    LayoutRebuildOptions options);

  private static readonly ConcurrentDictionary<Type, SymbolicLinkDeduplicator> SymbolicLinkDeduplicators = new();
  private static readonly ConcurrentDictionary<Type, byte> TransparentCompressionWriters = new();

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

  /// <summary>
  /// Marks a writer whose normal rebuild already stores regular file payloads through
  /// the filesystem's transparent compression layer. For such formats the explicit
  /// compression option is idempotent rather than unsupported.
  /// </summary>
  public static void RegisterTransparentCompression<T>() where T : ILayoutOptimizable
    => TransparentCompressionWriters[typeof(T)] = 0;

  internal static bool TryGetSymbolicLinkDeduplicator(
    ILayoutOptimizable layout,
    out SymbolicLinkDeduplicator rebuild)
    => SymbolicLinkDeduplicators.TryGetValue(layout.GetType(), out rebuild!);

  internal static bool HasTransparentCompression(ILayoutOptimizable layout)
    => TransparentCompressionWriters.ContainsKey(layout.GetType());
}
