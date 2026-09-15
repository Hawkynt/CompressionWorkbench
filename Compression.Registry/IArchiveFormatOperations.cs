using Compression.Registry.Streaming;

namespace Compression.Registry;

/// <summary>
/// The base capability every archive descriptor implements: list entries and extract them
/// to a directory. All other archive capabilities (create, modify, in-memory extract,
/// defragment, shrink, input constraints) are separate opt-in interfaces so callers can
/// discover them at the type level.
/// </summary>
/// <remarks>
/// <para>
/// The historical <see cref="List(Stream,string?)"/> / <c>Extract(Stream, ...)</c>
/// methods remain the descriptor's native compatibility surface. The explicit input-mode methods
/// below make the three archive-reading models available uniformly to every archive and
/// pseudo-archive: forward-only stream input, required-seek input, and in-memory
/// <see cref="ReadOnlySpan{T}"/> input.
/// </para>
/// <para>
/// Existing descriptors automatically gain all three modes. A descriptor can override the
/// default interface implementations when it has a genuinely streaming parser, a native
/// random-access reader, or a zero-copy span parser. Until then, forward-only streams are
/// spooled to a temporary seekable file and spans are copied once into an owned memory stream;
/// neither fallback imposes a whole-archive managed-array limit on stream input.
/// </para>
/// </remarks>
public interface IArchiveFormatOperations {
  /// <summary>List all entries in the archive using the descriptor's native stream path.</summary>
  List<ArchiveEntryInfo> List(Stream stream, string? password);

  /// <summary>Extract entries from the archive to an output directory using the descriptor's native stream path.</summary>
  void Extract(Stream stream, string outputDir, string? password, string[]? files);

  /// <summary>
  /// Lists entries from a forward-only or seekable stream. This is the libarchive-style
  /// streaming entry point: callers do not need to provide seek capability.
  /// </summary>
  public virtual List<ArchiveEntryInfo> ListStreaming(Stream archive, string? password) {
    using var lease = SeekableArchiveInputLease.Open(archive);
    return this.ListSeekable(lease.Stream, password);
  }

  /// <summary>
  /// Extracts entries from a forward-only or seekable stream. Descriptors with a native
  /// one-pass parser should override this method; the default spools only when necessary.
  /// </summary>
  public virtual void ExtractStreaming(Stream archive, string outputDir, string? password, string[]? files) {
    using var lease = SeekableArchiveInputLease.Open(archive);
    this.ExtractSeekable(lease.Stream, outputDir, password, files);
  }

  /// <summary>
  /// Lists entries through the explicit seek-based path. The supplied stream must support
  /// seeking; it is rewound before the descriptor's native reader is invoked.
  /// </summary>
  public virtual List<ArchiveEntryInfo> ListSeekable(Stream archive, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead)
      throw new ArgumentException("Archive input must be readable.", nameof(archive));
    if (!archive.CanSeek)
      throw new ArgumentException("Seek-based archive input must support seeking.", nameof(archive));
    archive.Position = 0;
    return this.List(archive, password);
  }

  /// <summary>
  /// Extracts entries through the explicit seek-based path. The supplied stream must support
  /// seeking; it is rewound before the descriptor's native reader is invoked.
  /// </summary>
  public virtual void ExtractSeekable(Stream archive, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead)
      throw new ArgumentException("Archive input must be readable.", nameof(archive));
    if (!archive.CanSeek)
      throw new ArgumentException("Seek-based archive input must support seeking.", nameof(archive));
    archive.Position = 0;
    this.Extract(archive, outputDir, password, files);
  }

  /// <summary>
  /// Lists entries from an in-memory archive image. The default compatibility bridge copies
  /// the span once because a <see cref="Stream"/> cannot safely retain a borrowed span; native
  /// span parsers should override this method to remain allocation-free.
  /// </summary>
  public virtual List<ArchiveEntryInfo> ListSpan(ReadOnlySpan<byte> archive, string? password) {
    using var stream = new MemoryStream(archive.ToArray(), writable: false);
    return this.ListSeekable(stream, password);
  }

  /// <summary>
  /// Extracts entries from an in-memory archive image. Native span parsers can override this
  /// method to avoid the compatibility copy used by the default implementation.
  /// </summary>
  public virtual void ExtractSpan(ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files) {
    using var stream = new MemoryStream(archive.ToArray(), writable: false);
    this.ExtractSeekable(stream, outputDir, password, files);
  }

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
  /// <c>byte[]</c>. It asks <c>Extract(Stream, ...)</c> for the selected entry in an
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
  /// Opens one entry from a forward-only or seekable archive source. A temporary spool, when
  /// required, stays alive until the returned entry stream is disposed.
  /// </summary>
  public virtual Stream OpenEntryStreaming(Stream archive, string entryName, string? password) {
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
    var lease = SeekableArchiveInputLease.Open(archive);
    try {
      var entry = this.OpenEntrySeekable(lease.Stream, entryName, password);
      return new OwnedArchiveEntryStream(entry, lease);
    } catch {
      lease.Dispose();
      throw;
    }
  }

  /// <summary>
  /// Opens one entry through the explicit seek-based path. The archive is rewound before the
  /// descriptor-specific entry reader is invoked.
  /// </summary>
  public virtual Stream OpenEntrySeekable(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
    if (!archive.CanRead)
      throw new ArgumentException("Archive input must be readable.", nameof(archive));
    if (!archive.CanSeek)
      throw new ArgumentException("Seek-based archive input must support seeking.", nameof(archive));
    archive.Position = 0;
    return this.OpenEntry(archive, entryName, password);
  }

  /// <summary>
  /// Opens one entry from an in-memory archive image. Because the returned stream may outlive
  /// this call, the default bridge owns one copy of the supplied span until that stream is disposed.
  /// Native span readers can override this method when they can return independently owned output.
  /// </summary>
  public virtual Stream OpenEntrySpan(ReadOnlySpan<byte> archive, string entryName, string? password) {
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
    var source = new MemoryStream(archive.ToArray(), writable: false);
    try {
      var entry = this.OpenEntrySeekable(source, entryName, password);
      return new OwnedArchiveEntryStream(entry, source);
    } catch {
      source.Dispose();
      throw;
    }
  }

  /// <summary>
  /// Extracts a single entry to a byte array. This is the explicitly buffered
  /// convenience API; callers working with large entries should use
  /// <see cref="OpenEntry(Stream,string,string?)"/> instead.
  /// </summary>
  /// <remarks>
  /// The default routes through <see cref="OpenEntry(Stream,string,string?)"/>, so descriptor-specific
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

  /// <summary>Extracts one entry from a forward-only or seekable archive source into memory.</summary>
  public virtual byte[] ExtractEntryToMemoryStreaming(Stream archive, string entryName, string? password) {
    using var entry = this.OpenEntryStreaming(archive, entryName, password);
    using var memory = new MemoryStream();
    entry.CopyTo(memory);
    return memory.ToArray();
  }

  /// <summary>Extracts one entry from an in-memory archive image into a new byte array.</summary>
  public virtual byte[] ExtractEntryToMemorySpan(ReadOnlySpan<byte> archive, string entryName, string? password) {
    using var entry = this.OpenEntrySpan(archive, entryName, password);
    using var memory = new MemoryStream();
    entry.CopyTo(memory);
    return memory.ToArray();
  }
}
