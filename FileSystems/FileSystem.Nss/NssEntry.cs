#pragma warning disable CS1591
namespace FileSystem.Nss;

/// <summary>
/// One entry surfaced by the NSS read-only descriptor. We do not parse the
/// object tree itself (the on-disk layout is proprietary and lacks a
/// publicly verifiable spec). We only ever produce synthetic entries
/// describing the pool / volume headers we located.
/// </summary>
public sealed class NssEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
}
