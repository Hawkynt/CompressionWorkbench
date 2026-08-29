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
  /// conversion and derived-filesystem pipelines.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Reads past the logical size return 0 (EOF); seek targets cannot escape the
  /// entry. The caller owns disposal.
  /// </para>
  /// <para>
  /// The default implementation intentionally does <b>not</b> materialize a
  /// <c>byte[]</c>. It asks <see cref="Extract"/> for the selected entry in an
  /// isolated temporary directory, opens the resulting file as a seekable
  /// stream, and deletes that tree on dispose. This gives every descriptor a
  /// large-file-safe streaming fallback even before it grows a native per-entry
  /// reader. Native readers (FAT chains, ZIP decoder streams, TAR slices, etc.)
  /// should still override this to avoid the temporary extraction pass.
  /// </para>
  /// </remarks>
  public virtual Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
    var extracted = TemporaryExtractedEntryStream.Open(this, archive, entryName, password);
    return new BoundedEntryStream(extracted, extracted.Length, leaveOpen: false);
  }

  /// <summary>
  /// Extracts a single entry to a byte array. This is the explicitly buffered
  /// convenience API; callers working with large entries should use
  /// <see cref="OpenEntry"/> instead.
  /// </summary>
  /// <remarks>
  /// The default routes through <see cref="OpenEntry"/>, so descriptor-specific
  /// isolation/decoding semantics are preserved. A result past the runtime array
  /// limit naturally fails here rather than imposing that limit on the streaming
  /// API or filesystem-driver layer.
  /// </remarks>
  public virtual byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var entry = this.OpenEntry(archive, entryName, password);
    using var memory = new MemoryStream();
    entry.CopyTo(memory);
    return memory.ToArray();
  }
}
