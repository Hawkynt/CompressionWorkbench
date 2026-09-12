#pragma warning disable CS1591
namespace FileFormat.Ghost;

/// <summary>
/// One synthesised entry surfaced by <see cref="GhostReader"/> to the
/// registry: either metadata, a partition's raw decompressed bytes, or
/// (when extraction fails) the raw container payload.
/// </summary>
public sealed class GhostEntry {
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
  /// Gets or sets the data.
  /// </summary>
  public byte[] Data { get; init; } = [];
}
