namespace Compression.Registry;

/// <summary>
/// Central registry of all format descriptors. Populated at startup via <see cref="Register"/>
/// calls (typically from source-generated code), then finalized with <see cref="Initialize"/>.
/// </summary>
public static class FormatRegistry {

  private static readonly List<IFormatDescriptor> _all = [];
  private static readonly Dictionary<string, IFormatDescriptor> _byId = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Dictionary<string, IFormatDescriptor> _byExtension = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Dictionary<string, IFormatDescriptor> _byCompoundExtension = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Dictionary<string, IStreamFormatOperations> _streamOps = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Dictionary<string, IArchiveFormatOperations> _archiveOps = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Dictionary<string, IAsyncArchiveOperations> _asyncArchiveOps = new(StringComparer.OrdinalIgnoreCase);
  private static bool _initialized;

  /// <summary>
  /// Finalize the registry by building lookup tables. Safe to call multiple times.
  /// Call this after all <see cref="Register"/> calls are complete.
  /// </summary>
  public static void Initialize() {
    if (_initialized) return;
    _initialized = true;
    BuildLookups();
  }

  /// <summary>
  /// Register a format descriptor. Called by generated code and for compound tar auto-generation.
  /// Must be called before <see cref="Initialize"/>.
  /// </summary>
  public static void Register(IFormatDescriptor descriptor) {
    _all.Add(descriptor);
    _byId[descriptor.Id] = descriptor;
    if (descriptor is IStreamFormatOperations streamOps)
      _streamOps[descriptor.Id] = streamOps;
    if (descriptor is IArchiveFormatOperations archiveOps)
      _archiveOps[descriptor.Id] = archiveOps;
    if (descriptor is IAsyncArchiveOperations asyncArchiveOps)
      _asyncArchiveOps[descriptor.Id] = asyncArchiveOps;
  }

  /// <summary>All registered descriptors.</summary>
  public static IReadOnlyList<IFormatDescriptor> All => _all;

  /// <summary>Look up a descriptor by its unique ID.</summary>
  public static IFormatDescriptor? GetById(string id)
    => _byId.GetValueOrDefault(id);

  /// <summary>Look up a descriptor by file path/extension. Checks compound extensions first (longest match).</summary>
  public static IFormatDescriptor? GetByExtension(string path) {
    var lower = path.ToLowerInvariant();

    // Check compound extensions first (e.g. .tar.gz, .tar.bz2)
    foreach (var (ext, desc) in _byCompoundExtension) {
      if (lower.EndsWith(ext))
        return desc;
    }

    // Fall back to single extension
    var singleExt = Path.GetExtension(lower);
    return string.IsNullOrEmpty(singleExt) ? null : _byExtension.GetValueOrDefault(singleExt);
  }

  /// <summary>Get all descriptors in a given category.</summary>
  public static IEnumerable<IFormatDescriptor> GetByCategory(FormatCategory category)
    => _all.Where(d => d.Category == category);

  /// <summary>Get stream operations for a format ID, or null if not a stream format.</summary>
  public static IStreamFormatOperations? GetStreamOps(string id)
    => _streamOps.GetValueOrDefault(id);

  /// <summary>Get archive operations for a format ID, or null if not an archive format.</summary>
  public static IArchiveFormatOperations? GetArchiveOps(string id)
    => _archiveOps.GetValueOrDefault(id);

  /// <summary>Get async archive operations for a format ID, or null if the format doesn't support async listing.</summary>
  public static IAsyncArchiveOperations? GetAsyncArchiveOps(string id)
    => _asyncArchiveOps.GetValueOrDefault(id);

  /// <summary>Reset the registry (for testing only).</summary>
  internal static void Reset() {
    _all.Clear();
    _byId.Clear();
    _byExtension.Clear();
    _byCompoundExtension.Clear();
    _streamOps.Clear();
    _archiveOps.Clear();
    _asyncArchiveOps.Clear();
    _initialized = false;
  }

  private static void BuildLookups() {
    foreach (var desc in _all) {
      foreach (var ext in desc.CompoundExtensions)
        _byCompoundExtension.TryAdd(ext.ToLowerInvariant(), desc);

      foreach (var ext in desc.Extensions)
        _byExtension.TryAdd(ext.ToLowerInvariant(), desc);
    }
  }
}
