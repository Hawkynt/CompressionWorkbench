#pragma warning disable CS1591
namespace FileSystem.Nss;

/// <summary>
/// One entry surfaced by the NSS read-only descriptor. We do not parse the
/// object tree itself (the on-disk layout is proprietary and lacks a
/// publicly verifiable spec). We only ever produce synthetic entries
/// describing the pool / volume headers we located.
/// </summary>
public sealed class NssEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
    /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
    /// <summary>
  /// Gets or sets the last modified.
  /// </summary>
public DateTime? LastModified { get; init; }
}
