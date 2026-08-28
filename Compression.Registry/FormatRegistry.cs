namespace Compression.Registry;

/// <summary>
/// Central registry of all format descriptors and their optional driver sidecars.
/// Populated at startup by generated registration code, then finalized with
/// <see cref="Initialize"/>.
/// </summary>
public static class FormatRegistry {

  private static readonly List<IFormatDescriptor> _all = [];
  private static readonly Dictionary<string, IFormatDescriptor> _byId = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Dictionary<string, IFormatDescriptor> _byExtension = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Dictionary<string, IFormatDescriptor> _byCompoundExtension = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Dictionary<string, IStreamFormatOperations> _streamOps = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Dictionary<string, IArchiveFormatOperations> _archiveOps = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Dictionary<string, IAsyncArchiveOperations> _asyncArchiveOps = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Dictionary<string, IFilesystemDriverAdapter> _filesystemDrivers = new(StringComparer.OrdinalIgnoreCase);
  private static bool _initialized;

  public static void Initialize() {
    if (_initialized) return;
    _initialized = true;
    BuildLookups();

    foreach (var (id, _) in _filesystemDrivers)
      if (!_byId.ContainsKey(id))
        throw new InvalidOperationException($"Filesystem driver adapter '{id}' has no registered format descriptor.");
  }

  public static void Register(IFormatDescriptor descriptor) {
    ArgumentNullException.ThrowIfNull(descriptor);
    if (_initialized) throw new InvalidOperationException("FormatRegistry is already initialized.");
    _all.Add(descriptor);
    _byId[descriptor.Id] = descriptor;
    if (descriptor is IStreamFormatOperations streamOps)
      _streamOps[descriptor.Id] = streamOps;
    if (descriptor is IArchiveFormatOperations archiveOps)
      _archiveOps[descriptor.Id] = archiveOps;
    if (descriptor is IAsyncArchiveOperations asyncArchiveOps)
      _asyncArchiveOps[descriptor.Id] = asyncArchiveOps;
  }

  /// <summary>
  /// Registers one source-generated native filesystem-driver sidecar. Duplicate
  /// adapters for the same format ID are a build/runtime contract error rather
  /// than whichever registration happened to win.
  /// </summary>
  public static void RegisterFilesystemDriver(IFilesystemDriverAdapter driver) {
    ArgumentNullException.ThrowIfNull(driver);
    if (_initialized) throw new InvalidOperationException("FormatRegistry is already initialized.");
    if (string.IsNullOrWhiteSpace(driver.FormatId))
      throw new ArgumentException("Filesystem driver adapter must provide a format ID.", nameof(driver));
    if (!_filesystemDrivers.TryAdd(driver.FormatId, driver))
      throw new InvalidOperationException($"A filesystem driver adapter for '{driver.FormatId}' is already registered.");
  }

  public static IReadOnlyList<IFormatDescriptor> All => _all;

  public static IFormatDescriptor? GetById(string id)
    => _byId.GetValueOrDefault(id);

  public static IFormatDescriptor? GetByExtension(string path) {
    var lower = path.ToLowerInvariant();
    foreach (var (ext, desc) in _byCompoundExtension)
      if (lower.EndsWith(ext)) return desc;
    var singleExt = Path.GetExtension(lower);
    return string.IsNullOrEmpty(singleExt) ? null : _byExtension.GetValueOrDefault(singleExt);
  }

  public static IEnumerable<IFormatDescriptor> GetByCategory(FormatCategory category)
    => _all.Where(d => d.Category == category);

  public static IStreamFormatOperations? GetStreamOps(string id)
    => _streamOps.GetValueOrDefault(id);

  public static IArchiveFormatOperations? GetArchiveOps(string id)
    => _archiveOps.GetValueOrDefault(id);

  /// <summary>Returns a generated native driver sidecar for the format, when one exists.</summary>
  public static IFilesystemDriverAdapter? GetFilesystemDriver(string id)
    => _filesystemDrivers.GetValueOrDefault(id);

  public static FilesystemDriverProfile ProbeFilesystem(
      string id,
      Stream image,
      string? password = null) {
    var descriptor = GetById(id)
      ?? throw new KeyNotFoundException($"Unknown format id '{id}'.");
    if (descriptor is IFilesystemDriverProvider native)
      return native.ProbeFilesystem(image);
    if (_filesystemDrivers.TryGetValue(id, out var adapter))
      return adapter.ProbeFilesystem(image);
    return FilesystemDriverDerivation.Probe(descriptor, image, password);
  }

  public static IFilesystemSession OpenFilesystem(
      string id,
      Stream image,
      FilesystemOpenOptions options,
      string? password = null) {
    var descriptor = GetById(id)
      ?? throw new KeyNotFoundException($"Unknown format id '{id}'.");
    if (descriptor is IFilesystemDriverProvider native)
      return native.OpenFilesystem(image, options);
    if (_filesystemDrivers.TryGetValue(id, out var adapter))
      return adapter.OpenFilesystem(image, options);
    return FilesystemDriverDerivation.Open(descriptor, image, options, password);
  }

  public static FilesystemDriverReadinessReport AssessFilesystemDriver(
      string id,
      Stream image,
      FilesystemDriverTarget target,
      string? password = null) {
    var descriptor = GetById(id)
      ?? throw new KeyNotFoundException($"Unknown format id '{id}'.");
    if (descriptor is IFilesystemDriverReadinessProvider native)
      return native.DescribeFilesystemDriverReadiness(image, target);
    if (_filesystemDrivers.TryGetValue(id, out var adapter))
      return adapter.DescribeFilesystemDriverReadiness(image, target);
    return FilesystemDriverDerivation.Assess(descriptor, image, target, password);
  }

  public static IAsyncArchiveOperations? GetAsyncArchiveOps(string id)
    => _asyncArchiveOps.GetValueOrDefault(id);

  internal static void Reset() {
    _all.Clear();
    _byId.Clear();
    _byExtension.Clear();
    _byCompoundExtension.Clear();
    _streamOps.Clear();
    _archiveOps.Clear();
    _asyncArchiveOps.Clear();
    _filesystemDrivers.Clear();
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
