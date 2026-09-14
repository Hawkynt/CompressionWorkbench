#pragma warning disable CS1591
namespace FileSystem.Nss;

/// <summary>
/// One entry surfaced by the NSS read-only descriptor. Native entries are
/// reconstructed only for the conservative quiescent profile documented in
/// docs/NSS-ON-DISK.md; diagnostic anchor entries remain available separately.
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

  internal bool IsNativeNssEntry { get; init; }
  internal ulong NativeZid { get; init; }
  internal ulong NativeParentZid { get; init; }
  internal ulong NativeAbsoluteStartBlock { get; init; }
  internal uint NativeBlockCount { get; init; }
}
