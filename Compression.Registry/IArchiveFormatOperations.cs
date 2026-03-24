namespace Compression.Registry;

/// <summary>
/// Operations for multi-file archive formats.
/// </summary>
public interface IArchiveFormatOperations {
  /// <summary>List all entries in the archive.</summary>
  List<ArchiveEntryInfo> List(Stream stream, string? password);

  /// <summary>Extract entries from the archive to an output directory.</summary>
  void Extract(Stream stream, string outputDir, string? password, string[]? files);

  /// <summary>
  /// Create a new archive. Not all formats support creation; those that don't should throw
  /// <see cref="NotSupportedException"/>.
  /// </summary>
  void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => throw new NotSupportedException($"Format does not support creation.");
}
