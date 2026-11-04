#pragma warning disable CS1591
namespace FileSystem.Apfs;

/// <summary>
/// Represents an apfs entry.
/// </summary>
public sealed class ApfsEntry {
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
  /// Gets a value indicating whether is symlink.
  /// </summary>
  public bool IsSymlink { get; init; }
  /// <summary>
  /// Gets or sets the link target.
  /// </summary>
  public string? LinkTarget { get; init; }
  /// <summary>
  /// Gets or sets the last modified.
  /// </summary>
  public DateTime? LastModified { get; init; }
  internal ulong ObjectId { get; init; }
  /// <summary>
  /// First block of the file's data when it is one uncompressed extent covering the
  /// whole file; 0 otherwise (no data, several extents, or transparent compression).
  /// </summary>
  public ulong FirstBlock { get; init; }
  /// <summary>Length in bytes of that single extent (0 = empty/none).</summary>
  internal long ExtentLength { get; init; }
  /// <summary>Every FILE_EXTENT of the data stream, in logical order.</summary>
  internal IReadOnlyList<ApfsExtent> Extents { get; init; } = [];
  /// <summary>The <c>com.apple.decmpfs</c> attribute of a transparently compressed file.</summary>
  internal byte[]? Decmpfs { get; init; }
  /// <summary>The <c>com.apple.ResourceFork</c> attribute, when stored inline.</summary>
  internal byte[]? ResourceForkInline { get; init; }
  /// <summary>The resource fork's extents, when stored as an attribute stream.</summary>
  internal IReadOnlyList<ApfsExtent>? ResourceForkExtents { get; init; }
  /// <summary>The resource fork's length, when stored as an attribute stream.</summary>
  internal long ResourceForkSize { get; init; }
  /// <summary>True when the file's bytes come from decmpfs rather than its data stream.</summary>
  public bool IsCompressed => this.Decmpfs is not null;
}

/// <summary>One FILE_EXTENT record: logical offset, length in bytes, first physical block (0 = sparse).</summary>
internal readonly record struct ApfsExtent(ulong Logical, long Length, ulong PhysBlock);
