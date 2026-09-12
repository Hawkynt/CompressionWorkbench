#pragma warning disable CS1591
namespace FileSystem.GlusterFs;

/// <summary>
/// Represents an entry surfaced from a single GlusterFS brick backing store.
/// </summary>
public sealed class GlusterFsEntry {
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
  /// Gets or sets the offset.
  /// </summary>
  public long Offset { get; init; }
  /// <summary>
  /// Gets or sets eagerly materialized data, used by synthetic metadata entries.
  /// </summary>
  public byte[] Data { get; init; } = [];

  /// <summary>
  /// Lazily materializes an entry delegated to the backing filesystem reader.
  /// </summary>
  internal Func<byte[]>? DataFactory { get; init; }

  /// <summary>
  /// Native path inside the backing filesystem, used for xattr lookup.
  /// </summary>
  internal string? BackingPath { get; init; }
}
