using Compression.Registry.Streaming;

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
  /// Opens a single entry as a read-only <see cref="Stream"/> bounded to that
  /// entry's logical bytes — physically incapable of reading slack space,
  /// adjacent entries, padding/alignment fillers, or header/metadata regions.
  /// This is the canonical per-entry isolation primitive used by streaming
  /// conversion pipelines.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The returned stream is always a <see cref="BoundedEntryStream"/> (or a
  /// wrapper that satisfies the same contract). Reads past the entry's
  /// logical size return 0 (EOF); seek targets are clamped to the bound.
  /// The caller owns disposal.
  /// </para>
  /// <para>
  /// Default implementation buffers the entry's bytes via
  /// <see cref="ExtractEntryToMemory"/> and wraps a <see cref="MemoryStream"/>
  /// over the result. Descriptors with native per-entry readers (FAT cluster
  /// chains, ZIP DEFLATE wrapper, TAR positional slice, 7z folder slot)
  /// should override to return a properly bounded streaming view of their
  /// decoder output.
  /// </para>
  /// </remarks>
  public virtual Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  /// <summary>
  /// Extracts a single entry to a byte array without writing to disk. The default
  /// implementation now routes through <see cref="OpenEntry"/> so the bounded
  /// streaming contract is enforced even when callers ask for a buffered result.
  /// Descriptors that have a more efficient native byte-array path (e.g. a
  /// reader that already materialises the whole entry) can still override.
  /// </summary>
  /// <remarks>
  /// The wrapper rewinds <paramref name="archive"/> to position 0 when
  /// possible, opens the entry as a bounded stream, and copies it into a
  /// fresh byte array. The bound on <see cref="OpenEntry"/> guarantees the
  /// result contains only the entry's logical bytes.
  /// </remarks>
  public virtual byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    // Fall back to the tempdir path when the descriptor has not overridden
    // either method — that's the only way to break the recursion between the
    // two virtual defaults.
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_x2m_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tempDir);
      this.Extract(archive, tempDir, password, [entryName]);
      var file = Path.Combine(tempDir, entryName.Replace('/', Path.DirectorySeparatorChar));
      return File.Exists(file) ? File.ReadAllBytes(file) : [];
    } finally {
      try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* best-effort */ }
    }
  }
}
