using System.Collections.Concurrent;

namespace Compression.Registry;

/// <summary>
/// Describes what a hard-link-style deduplication transform actually means for a
/// writer. Native links have filesystem-managed link semantics; read-only shared
/// data deliberately aliases storage in a format that has no native link count.
/// </summary>
public enum HardLinkDeduplicationSemantics {
  /// <summary>No hard-link-style deduplication is implemented by this writer.</summary>
  None = 0,

  /// <summary>The filesystem natively represents several names for one file/inode.</summary>
  Native,

  /// <summary>
  /// Several read-only directory entries deliberately reference the same physical
  /// data. This is not a native hard link and writers/removers must preserve the
  /// shared allocation until the last directory entry stops referencing it.
  /// </summary>
  ReadOnlySharedData,
}

/// <summary>
/// One compression/layout parameter that is safe and useful to probe while looking
/// for a smaller filesystem representation. When <see cref="Values"/> is null or
/// empty, finite values are taken from the descriptor's option schema.
/// </summary>
public sealed record FilesystemCompressionParameter(
  string Key,
  IReadOnlyList<string>? Values = null);

/// <summary>Explicit writer-backed compression capabilities.</summary>
public sealed record FilesystemCompressionProfile(
  bool TransparentCompression,
  IReadOnlyList<FilesystemCompressionParameter> Parameters);

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

  /// <summary>Writer-specific hard-link/shared-data deduplication rebuild.</summary>
  public delegate void HardLinkDeduplicator(
    ILayoutOptimizable layout,
    Stream source,
    Stream target,
    LayoutRebuildOptions options);

  private sealed record HardLinkRegistration(
    HardLinkDeduplicationSemantics Semantics,
    HardLinkDeduplicator Rebuild);

  private static readonly ConcurrentDictionary<Type, SymbolicLinkDeduplicator> SymbolicLinkDeduplicators = new();
  private static readonly ConcurrentDictionary<Type, HardLinkRegistration> HardLinkDeduplicators = new();
  private static readonly ConcurrentDictionary<Type, FilesystemCompressionProfile> CompressionProfiles = new();

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
  /// Registers a writer-specific deduplication backend and explicitly states whether
  /// the result is a native hard link or a read-only shared-data alias.
  /// </summary>
  public static void RegisterHardLinkDeduplicator<T>(
    HardLinkDeduplicationSemantics semantics,
    HardLinkDeduplicator rebuild)
    where T : ILayoutOptimizable {
    if (semantics == HardLinkDeduplicationSemantics.None)
      throw new ArgumentOutOfRangeException(nameof(semantics), "A registered deduplicator must declare concrete semantics.");
    ArgumentNullException.ThrowIfNull(rebuild);
    HardLinkDeduplicators[typeof(T)] = new HardLinkRegistration(semantics, rebuild);
  }

  /// <summary>
  /// Registers compression capabilities only when the concrete writer is known to
  /// honour them. This deliberately replaces name-based option-schema guessing.
  /// </summary>
  public static void RegisterCompression<T>(
    bool transparentCompression,
    params FilesystemCompressionParameter[] parameters)
    where T : ILayoutOptimizable {
    ArgumentNullException.ThrowIfNull(parameters);
    CompressionProfiles[typeof(T)] = new FilesystemCompressionProfile(
      transparentCompression,
      parameters.ToArray());
  }

  /// <summary>
  /// Marks a writer whose normal rebuild already stores regular file payloads through
  /// the filesystem's transparent compression layer. For such formats the explicit
  /// compression option is idempotent rather than unsupported.
  /// </summary>
  public static void RegisterTransparentCompression<T>() where T : ILayoutOptimizable
    => RegisterCompression<T>(transparentCompression: true);

  internal static bool TryGetSymbolicLinkDeduplicator(
    ILayoutOptimizable layout,
    out SymbolicLinkDeduplicator rebuild)
    => SymbolicLinkDeduplicators.TryGetValue(layout.GetType(), out rebuild!);

  internal static bool TryGetHardLinkDeduplicator(
    ILayoutOptimizable layout,
    out HardLinkDeduplicationSemantics semantics,
    out HardLinkDeduplicator rebuild) {
    if (HardLinkDeduplicators.TryGetValue(layout.GetType(), out var registration)) {
      semantics = registration.Semantics;
      rebuild = registration.Rebuild;
      return true;
    }

    semantics = HardLinkDeduplicationSemantics.None;
    rebuild = null!;
    return false;
  }

  internal static bool TryGetCompressionProfile(
    ILayoutOptimizable layout,
    out FilesystemCompressionProfile profile)
    => CompressionProfiles.TryGetValue(layout.GetType(), out profile!);
}
