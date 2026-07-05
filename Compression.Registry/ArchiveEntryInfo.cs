namespace Compression.Registry;

/// <summary>
/// Normalized archive entry metadata returned by format descriptors.
/// </summary>
/// <param name="Index">Zero-based position of the entry in the listing.</param>
/// <param name="Name">Full slash-separated path of the entry within the archive.</param>
/// <param name="OriginalSize">
/// The entry's own uncompressed on-disk size. For a symbolic link this is the byte
/// length of the stored target path (the on-disk truth), NOT the size of whatever
/// the link points at — see <see cref="TargetSize"/> for the resolved target size.
/// </param>
/// <param name="CompressedSize">The entry's stored/compressed size, or -1 when unknown.</param>
/// <param name="Method">The compression/storage method label.</param>
/// <param name="IsDirectory">True when the entry is a directory.</param>
/// <param name="IsEncrypted">True when the entry's data is encrypted.</param>
/// <param name="LastModified">The entry's last-modified timestamp, when known.</param>
/// <param name="Kind">Optional taxonomy label (container/stream/track/channel/tag).</param>
/// <param name="IsSymlink">True when the entry is a symbolic link (or NTFS junction / reparse-point link).</param>
/// <param name="LinkTarget">The raw stored link target path, or null when the entry is not a link.</param>
/// <param name="TargetSize">
/// The size of the file the link ultimately resolves to, when it points at a regular
/// file within the same filesystem listing; null when unresolved (absolute target,
/// target outside the listing, a directory target, or a dangling/cyclic link). Filled
/// by <see cref="SymlinkResolver"/>.
/// </param>
public sealed record ArchiveEntryInfo(
  int Index,
  string Name,
  long OriginalSize,
  long CompressedSize,
  string Method,
  bool IsDirectory,
  bool IsEncrypted,
  DateTime? LastModified,
  string? Kind = null,
  bool IsSymlink = false,
  string? LinkTarget = null,
  long? TargetSize = null
);
