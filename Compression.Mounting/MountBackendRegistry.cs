using Compression.Registry;

namespace Compression.Mounting;

public sealed class MountBackendRegistry {
  private readonly IReadOnlyList<IFilesystemMountBackend> _backends;
  private readonly IReadOnlyDictionary<string, IFilesystemMountBackend> _backendsById;

  public MountBackendRegistry(IEnumerable<IFilesystemMountBackend> backends) {
    ArgumentNullException.ThrowIfNull(backends);

    var materialized = backends.ToArray();
    var byId = new Dictionary<string, IFilesystemMountBackend>(StringComparer.OrdinalIgnoreCase);
    foreach (var backend in materialized) {
      ArgumentNullException.ThrowIfNull(backend);
      var profile = backend.GetProfile();
      if (!byId.TryAdd(profile.Id, backend))
        throw new ArgumentException($"Duplicate mount backend id '{profile.Id}'.", nameof(backends));
    }

    this._backends = materialized;
    this._backendsById = byId;
  }

  public IReadOnlyList<IFilesystemMountBackend> Backends => this._backends;

  public IFilesystemMountBackend GetBackend(string backendId) {
    ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
    return this._backendsById.TryGetValue(backendId, out var backend)
      ? backend
      : throw new KeyNotFoundException($"Unknown mount backend '{backendId}'.");
  }

  public MountAccessOptions ResolveFilesystem(FilesystemDriverProfile driverProfile, bool sourceCanWrite) {
    ArgumentNullException.ThrowIfNull(driverProfile);

    var readOnly = new MountPlan[this._backends.Count];
    var readWrite = new MountPlan[this._backends.Count];
    for (var i = 0; i < this._backends.Count; ++i) {
      var profile = this._backends[i].GetProfile();
      readOnly[i] = FilesystemMountCapabilityResolver.Resolve(driverProfile, profile, MountAccessMode.ReadOnly, sourceCanWrite);
      readWrite[i] = FilesystemMountCapabilityResolver.Resolve(driverProfile, profile, MountAccessMode.ReadWrite, sourceCanWrite);
    }

    return new(readOnly, readWrite);
  }

  public MountPlan ResolveFilesystem(
    string backendId,
    FilesystemDriverProfile driverProfile,
    MountAccessMode accessMode,
    bool sourceCanWrite
  ) {
    ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
    ArgumentNullException.ThrowIfNull(driverProfile);

    var backend = this.GetBackend(backendId);
    return FilesystemMountCapabilityResolver.Resolve(driverProfile, backend.GetProfile(), accessMode, sourceCanWrite);
  }
}
