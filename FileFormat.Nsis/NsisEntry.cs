namespace FileFormat.Nsis;

/// <summary>
/// Represents a data block embedded in an NSIS installer.
/// </summary>
/// <param name="FileName">The entry name. Will be a generated name such as "block_0" unless the
/// installer header was successfully parsed to recover actual file names.</param>
/// <param name="Size">The uncompressed size in bytes, or -1 when unknown (solid stream).</param>
/// <param name="CompressedSize">The compressed size in bytes, or -1 when unknown (solid stream).</param>
/// <param name="IsDirectory">Whether the entry represents a directory.</param>
public sealed record NsisEntry(
  string FileName,
  long Size,
  long CompressedSize,
  bool IsDirectory
);
