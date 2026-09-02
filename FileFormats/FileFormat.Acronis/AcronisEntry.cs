#pragma warning disable CS1591
namespace FileFormat.Acronis;

/// <summary>
/// Synthetic Stage-0 entry surfaced from an Acronis True Image (.tib / .tibx)
/// container. We never decode the proprietary chunk stream, so each entry is
/// either the synthetic <c>metadata.ini</c> or the raw passthrough image bytes
/// keyed by <see cref="Data"/>.
/// </summary>
public sealed class AcronisEntry {
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
