#pragma warning disable CS1591
namespace FileSystem.AdvFs;

/// <summary>
/// Logical entry surfaced by <see cref="AdvFsReader"/>. Header/metadata entries
/// (<c>FULL.advfs</c>, <c>metadata.ini</c>, <c>rbmt_page0.bin</c>) carry
/// <c>Offset = -1</c>; AdvFS-WB writer-emitted file entries carry the absolute
/// byte offset into the image where their payload lives plus the payload length.
/// </summary>
public sealed class AdvFsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  /// <summary>Absolute byte offset of the file payload inside the image, or -1 for synthetic header entries.</summary>
  public long Offset { get; init; } = -1;
}
