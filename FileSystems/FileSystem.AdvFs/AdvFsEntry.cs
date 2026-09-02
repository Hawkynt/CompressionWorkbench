#pragma warning disable CS1591
namespace FileSystem.AdvFs;

/// <summary>
/// Logical entry surfaced by <see cref="AdvFsReader"/>. Header/metadata entries
/// (<c>FULL.advfs</c>, <c>metadata.ini</c>, <c>rbmt_page0.bin</c>) carry
/// <c>Offset = -1</c>; AdvFS-WB writer-emitted file entries carry the absolute
/// byte offset into the image where their payload lives plus the payload length.
/// </summary>
public sealed class AdvFsEntry {
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
  /// <summary>Absolute byte offset of the file payload inside the image, or -1 for synthetic header entries.</summary>
  public long Offset { get; init; } = -1;
}
