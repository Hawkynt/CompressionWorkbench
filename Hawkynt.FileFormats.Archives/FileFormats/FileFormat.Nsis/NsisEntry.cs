namespace FileFormat.Nsis;

/// <summary>
/// A file an NSIS installer extracts: one <c>File</c> instruction of the install script.
/// </summary>
/// <param name="FileName">
/// The target path as the script names it, with forward slashes. A leading <c>$INSTDIR</c> is
/// dropped, as 7-Zip does; any other variable (<c>$PLUGINSDIR</c>, <c>$SYSDIR</c>, ...) stays.
/// </param>
/// <param name="DataOffset">Offset of the file's length-prefixed block within the data section.</param>
/// <param name="LastModified">The file time the compiler recorded, in UTC, or <see langword="null"/> when none.</param>
public sealed record NsisEntry(string FileName, uint DataOffset, DateTime? LastModified) {

  /// <summary>Gets the uncompressed size in bytes, or -1 until the size has been resolved.</summary>
  public long Size { get; internal set; } = -1;

  /// <summary>
  /// Gets the stored size of the file's block, or -1 when the block lives inside a solid stream
  /// and therefore has no size of its own.
  /// </summary>
  public long CompressedSize { get; internal set; } = -1;

  /// <summary>Gets whether the entry is a directory; NSIS <c>File</c> entries never are.</summary>
  public bool IsDirectory => false;
}
