#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Sidecar binding from an existing format descriptor ID to a native filesystem
/// driver core. This lets large/legacy descriptors acquire driver semantics
/// without mixing mount state, locking and block-device code into their archive
/// surface. The source generator discovers public parameterless implementations
/// and registers them by <see cref="FormatId"/>.
/// </summary>
public interface IFilesystemDriverAdapter :
  IFilesystemDriverProvider,
  IFilesystemDriverReadinessProvider {
  string FormatId { get; }
}
