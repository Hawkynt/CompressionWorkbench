namespace Compression.Registry;

/// <summary>
/// Describes a single input file/directory for archive creation.
/// </summary>
public sealed record ArchiveInputInfo(
  string FullPath,
  string ArchiveName,
  bool IsDirectory
);
