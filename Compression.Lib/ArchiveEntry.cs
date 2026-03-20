namespace Compression.Lib;

/// <summary>
/// Normalized entry for display across all archive formats.
/// </summary>
internal sealed record ArchiveEntry(
  int Index,
  string Name,
  long OriginalSize,
  long CompressedSize,
  string Method,
  bool IsDirectory,
  bool IsEncrypted,
  DateTime? LastModified
) {
  internal double Ratio => OriginalSize > 0 ? 100.0 * CompressedSize / OriginalSize : 0;
}
