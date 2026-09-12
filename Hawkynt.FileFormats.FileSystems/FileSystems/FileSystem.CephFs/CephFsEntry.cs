#pragma warning disable CS1591
namespace FileSystem.CephFs;

/// <summary>
/// Represents one object in a portable <c>rados export</c> pool dump.
/// </summary>
public sealed class CephFsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public long Offset { get; init; }
  public byte[] Data { get; init; } = [];

  /// <summary>RADOS object identifier.</summary>
  public string ObjectId { get; init; } = "";
  /// <summary>RADOS namespace; empty means the default namespace.</summary>
  public string Namespace { get; init; } = "";
  /// <summary>RADOS locator key used for placement.</summary>
  public string LocatorKey { get; init; } = "";
  /// <summary>User xattrs as they are restored by <c>rados import</c>.</summary>
  public IReadOnlyDictionary<string, byte[]> Attributes { get; init; } = new Dictionary<string, byte[]>();
  /// <summary>RADOS OMAP header.</summary>
  public byte[] OmapHeader { get; init; } = [];
  /// <summary>RADOS OMAP key/value pairs.</summary>
  public IReadOnlyDictionary<string, byte[]> Omap { get; init; } = new Dictionary<string, byte[]>();
}