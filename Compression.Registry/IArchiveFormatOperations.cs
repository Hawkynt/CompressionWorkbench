namespace Compression.Registry;

/// <summary>
/// The base capability every archive descriptor implements: list entries and extract them
/// to a directory. All other archive capabilities (create, modify, in-memory extract,
/// defragment, shrink, input constraints) are separate opt-in interfaces so callers can
/// discover them at the type level.
/// </summary>
public interface IArchiveFormatOperations {
  /// <summary>List all entries in the archive.</summary>
  List<ArchiveEntryInfo> List(Stream stream, string? password);

  /// <summary>Extract entries from the archive to an output directory.</summary>
  void Extract(Stream stream, string outputDir, string? password, string[]? files);
}
