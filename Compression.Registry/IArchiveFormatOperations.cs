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

  /// <summary>
  /// Extracts a single entry to a byte array without writing to disk. The default
  /// implementation falls back to <see cref="Extract"/> with a temporary directory,
  /// so every descriptor works out of the box. Descriptors with a native per-entry
  /// reader (FAT, ZIP, TAR, 7z, …) should override for a true in-memory path —
  /// that's what powers the small-image <c>ConvertArchive</c> pipeline that never
  /// touches a tempdir.
  /// </summary>
  /// <remarks>
  /// The fallback rewinds <paramref name="archive"/> to position 0, extracts only
  /// the requested entry into a per-call temp directory, reads it back into memory
  /// and deletes the dir. Wrapped in try/finally so a thrown reader exception still
  /// cleans up the dir before propagating.
  /// </remarks>
  public virtual byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_x2m_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tempDir);
      if (archive.CanSeek) archive.Position = 0;
      Extract(archive, tempDir, password, [entryName]);
      var file = Path.Combine(tempDir, entryName.Replace('/', Path.DirectorySeparatorChar));
      return File.Exists(file) ? File.ReadAllBytes(file) : [];
    } finally {
      try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* best-effort */ }
    }
  }
}
