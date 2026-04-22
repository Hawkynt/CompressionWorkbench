namespace Compression.Registry;

/// <summary>
/// Normalized archive entry metadata returned by format descriptors.
/// </summary>
public sealed record ArchiveEntryInfo(
  int Index,
  string Name,
  long OriginalSize,
  long CompressedSize,
  string Method,
  bool IsDirectory,
  bool IsEncrypted,
  DateTime? LastModified,
  string? Kind = null
);
