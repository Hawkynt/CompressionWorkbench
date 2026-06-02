using Compression.Registry;
using F = Compression.Lib.FormatDetector.Format;

namespace Compression.Lib;

/// <summary>
/// Ergonomic high-level facade over <see cref="IArchiveFormatOperations"/>:
/// open an archive once, enumerate its entries as
/// <see cref="ArchiveReaderEntry"/> objects, and either
/// <see cref="ArchiveReaderEntry.OpenRead"/> a bounded per-entry stream or
/// <see cref="ArchiveReaderEntry.CopyTo(string)"/> directly to disk.
/// </summary>
/// <remarks>
/// <para>
/// Construction is cheap: the archive is opened from a path, its format is
/// detected, and <see cref="IArchiveFormatOperations.List"/> is invoked once
/// to populate the entry table. The underlying file stream stays open for
/// the lifetime of the reader and is closed on <see cref="Dispose"/>.
/// </para>
/// <para>
/// Concurrency: <see cref="ArchiveReaderEntry.OpenRead"/> opens a private
/// <c>File.OpenRead</c> over the archive path per call so multiple entry
/// streams can be active simultaneously. This costs one extra file handle
/// per concurrent <c>OpenRead</c> but lets callers parallelise extraction
/// without coordinating the shared archive cursor. All native overrides
/// (FAT, ZIP, TAR, 7z, plus the 16 formats added in this commit) use
/// seek-based reads, so cloning the source stream is safe.
/// </para>
/// </remarks>
public sealed class ArchiveReader : IDisposable {

  private readonly string _path;
  private readonly string? _password;
  private readonly IArchiveFormatOperations _ops;
  private readonly FileStream _sharedStream;
  private readonly List<ArchiveReaderEntry> _entries;
  private bool _disposed;

  /// <summary>Format ID as reported by the registry (e.g. <c>Zip</c>,
  /// <c>SevenZip</c>, <c>Lzh</c>).</summary>
  public string FormatId { get; }

  /// <summary>All entries in the archive, in the order the format reports
  /// them.</summary>
  public IReadOnlyList<ArchiveReaderEntry> Entries => this._entries;

  /// <summary>Just the file entries (skipping directory placeholders).</summary>
  public IEnumerable<ArchiveReaderEntry> Files
    => this._entries.Where(e => !e.IsDirectory);

  /// <summary>Just the directory placeholders.</summary>
  public IEnumerable<ArchiveReaderEntry> Directories
    => this._entries.Where(e => e.IsDirectory);

  private ArchiveReader(string path, string? password, IArchiveFormatOperations ops,
                        FileStream sharedStream, F format,
                        List<ArchiveEntryInfo> entries) {
    this._path = path;
    this._password = password;
    this._ops = ops;
    this._sharedStream = sharedStream;
    this.FormatId = format.ToString();
    this._entries = entries.Select(info => new ArchiveReaderEntry(this, info)).ToList();
  }

  /// <summary>Opens an archive at <paramref name="path"/> for reading.</summary>
  /// <param name="path">Path to an archive whose format is recognised by
  /// the <see cref="FormatDetector"/>.</param>
  /// <param name="password">Optional password for encrypted archives.</param>
  public static ArchiveReader Open(string path, string? password = null) {
    ArgumentNullException.ThrowIfNull(path);
    if (!File.Exists(path))
      throw new FileNotFoundException("Archive not found.", path);

    FormatRegistration.EnsureInitialized();
    var format = FormatDetector.Detect(path);
    var ops = FormatRegistry.GetArchiveOps(format.ToString())
      ?? throw new NotSupportedException(
        $"Format {format} does not support archive listing — use the stream APIs instead.");

    var sharedStream = File.OpenRead(path);
    List<ArchiveEntryInfo> entries;
    try {
      entries = ops.List(sharedStream, password);
    } catch {
      sharedStream.Dispose();
      throw;
    }

    return new ArchiveReader(path, password, ops, sharedStream, format, entries);
  }

  /// <summary>
  /// Opens a bounded read-only stream over the named entry. Called by
  /// <see cref="ArchiveReaderEntry.OpenRead"/>; exposed so advanced callers
  /// can request a specific entry by name without iterating <see cref="Entries"/>.
  /// </summary>
  /// <remarks>
  /// Opens its own <c>File.OpenRead</c> over the source archive — the
  /// returned bounded stream takes ownership of that file handle, so disposing
  /// the returned stream closes the per-call handle. The reader's shared
  /// stream stays open for subsequent <c>List</c>-style operations.
  /// </remarks>
  internal Stream OpenEntryStream(string entryName) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    ArgumentNullException.ThrowIfNull(entryName);

    // Per-call file handle so concurrent OpenRead calls don't fight over
    // the shared cursor. The Stream returned by ops.OpenEntry wraps this
    // FileStream in a BoundedEntryStream; disposing the bounded view
    // disposes the FileStream (per the OpenEntry contract).
    var perCallStream = File.OpenRead(this._path);
    try {
      return this._ops.OpenEntry(perCallStream, entryName, this._password);
    } catch {
      perCallStream.Dispose();
      throw;
    }
  }

  /// <summary>Closes the underlying archive stream.</summary>
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    this._sharedStream.Dispose();
  }
}
