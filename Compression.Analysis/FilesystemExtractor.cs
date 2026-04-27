using Compression.Lib;
using Compression.Registry;

namespace Compression.Analysis;

/// <summary>
/// Extracts files out of a carved filesystem (as located by
/// <see cref="FilesystemCarver"/>). Wraps the host stream with a
/// <see cref="SubStream"/> starting at the carved FS offset, then dispatches
/// to the format descriptor's standard <c>List</c> / <c>Extract</c> APIs.
/// <para>
/// Per-file errors are captured (one corrupt inode should not abort the
/// whole recovery). The caller gets a summary of successes + failures.
/// </para>
/// </summary>
public static class FilesystemExtractor {

  /// <summary>
  /// Lists entries of a carved filesystem without extracting.
  /// Returns an empty list if the format descriptor cannot be resolved or
  /// <see cref="IArchiveFormatOperations.List"/> throws.
  /// </summary>
  public static IReadOnlyList<ArchiveEntryInfo> ListCarved(Stream stream, CarvedFilesystem fs) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(fs);
    FormatRegistration.EnsureInitialized();

    var desc = FormatRegistry.GetById(fs.FormatId);
    if (desc is not IArchiveFormatOperations archiveOps) return [];

    var available = stream.Length - fs.ByteOffset;
    if (available <= 0) return [];

    var sub = new SubStream(stream, fs.ByteOffset, available);
    try {
      return archiveOps.List(sub, password: null);
    } catch {
      return [];
    }
  }

  /// <summary>
  /// Extracts every file out of a carved filesystem into <paramref name="outputDir"/>.
  /// Per-entry exceptions are caught and recorded in <see cref="ExtractResult.Errors"/>;
  /// extraction of remaining entries continues.
  /// </summary>
  public static ExtractResult ExtractCarved(Stream stream, CarvedFilesystem fs, string outputDir) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(fs);
    ArgumentException.ThrowIfNullOrEmpty(outputDir);
    FormatRegistration.EnsureInitialized();

    var desc = FormatRegistry.GetById(fs.FormatId);
    if (desc is not IArchiveFormatOperations archiveOps) {
      return new ExtractResult(
        FilesExtracted: 0,
        FilesFailed: 0,
        Errors: [$"No archive operations registered for format '{fs.FormatId}'."]);
    }

    var available = stream.Length - fs.ByteOffset;
    if (available <= 0) {
      return new ExtractResult(0, 0, [$"Carved FS offset {fs.ByteOffset} is at or past end of stream."]);
    }

    Directory.CreateDirectory(outputDir);

    // First pass: list to count entries. If List() throws, the FS is invalid
    // at this offset — give up early with a single error.
    List<ArchiveEntryInfo> entries;
    var listSub = new SubStream(stream, fs.ByteOffset, available);
    try {
      entries = archiveOps.List(listSub, password: null);
    } catch (Exception ex) {
      return new ExtractResult(0, 0, [$"List() failed for carved {fs.FormatId} at 0x{fs.ByteOffset:X}: {ex.Message}"]);
    }

    var fileEntries = entries.Where(e => !e.IsDirectory).ToList();
    var errors = new List<string>();
    var extracted = 0;
    var failed = 0;

    // The descriptor's Extract walks the whole tree. Most readers don't
    // support per-file extraction with robust error capture — so we drive
    // one Extract call per file via the files-filter. This isolates corrupt
    // entries without aborting the whole extraction.
    foreach (var entry in fileEntries) {
      var extractSub = new SubStream(stream, fs.ByteOffset, available);
      try {
        archiveOps.Extract(extractSub, outputDir, password: null, files: [entry.Name]);
        extracted++;
      } catch (Exception ex) {
        failed++;
        errors.Add($"{entry.Name}: {ex.Message}");
      }
    }

    return new ExtractResult(extracted, failed, errors);
  }
}

/// <summary>Summary returned by <see cref="FilesystemExtractor.ExtractCarved"/>.</summary>
public sealed record ExtractResult(
  int FilesExtracted,
  int FilesFailed,
  IReadOnlyList<string> Errors
);
