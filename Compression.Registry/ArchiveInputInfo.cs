namespace Compression.Registry;

/// <summary>
/// Describes a single input file/directory for archive creation.
/// </summary>
/// <remarks>
/// <para><b>In-memory inputs.</b> When <see cref="InMemoryContent"/> is set the
/// input's bytes are taken straight from memory and <see cref="FullPath"/> need
/// not refer to a real file. This lets callers reconfigure/convert an archive or
/// filesystem image without first extracting its entries to a temporary
/// directory — the extracted streams are fed back in directly. Descriptors read
/// content through <see cref="ReadContent"/> so the same code path serves both
/// on-disk and in-memory inputs.</para>
/// </remarks>
public sealed record ArchiveInputInfo(
  string FullPath,
  string ArchiveName,
  bool IsDirectory,
  byte[]? InMemoryContent = null,
  ArchiveEntryMetadata? Metadata = null
) {
  /// <summary>Creates an in-memory input whose content comes from
  /// <paramref name="content"/> rather than a file on disk.</summary>
  public static ArchiveInputInfo InMemory(string archiveName, byte[] content,
      ArchiveEntryMetadata? metadata = null)
    => new(FullPath: archiveName, ArchiveName: archiveName, IsDirectory: false,
      InMemoryContent: content, Metadata: metadata);

  /// <summary>Creates an in-memory input from a byte span (copied into the
  /// input, so the caller's buffer may be reused/stack-allocated).</summary>
  public static ArchiveInputInfo InMemory(string archiveName, ReadOnlySpan<byte> content,
      ArchiveEntryMetadata? metadata = null)
    => new(FullPath: archiveName, ArchiveName: archiveName, IsDirectory: false,
      InMemoryContent: content.ToArray(), Metadata: metadata);

  /// <summary>Creates an in-memory input by reading <paramref name="content"/>
  /// fully into memory. Reads from the stream's current position to its end.</summary>
  public static ArchiveInputInfo InMemory(string archiveName, System.IO.Stream content,
      ArchiveEntryMetadata? metadata = null) {
    ArgumentNullException.ThrowIfNull(content);
    using var ms = new System.IO.MemoryStream();
    content.CopyTo(ms);
    return new(FullPath: archiveName, ArchiveName: archiveName, IsDirectory: false,
      InMemoryContent: ms.ToArray(), Metadata: metadata);
  }

  /// <summary>Creates an on-disk input from a <see cref="System.IO.FileInfo"/>.
  /// Content is read lazily from the file via <see cref="ReadContent"/>;
  /// the archive name defaults to the file's leaf name.</summary>
  public static ArchiveInputInfo FromFile(System.IO.FileInfo file, string? archiveName = null) {
    ArgumentNullException.ThrowIfNull(file);
    var metadata = new ArchiveEntryMetadata(
      CreationTimeUtc: file.CreationTimeUtc,
      LastAccessTimeUtc: file.LastAccessTimeUtc,
      LastWriteTimeUtc: file.LastWriteTimeUtc,
      UnixMode: OperatingSystem.IsWindows() ? null : (ushort)System.IO.File.GetUnixFileMode(file.FullName));
    return new(FullPath: file.FullName, ArchiveName: archiveName ?? file.Name,
      IsDirectory: false, Metadata: metadata);
  }

  /// <summary>
  /// The last-write time the entry should carry: <see cref="Metadata"/>'s when given, else
  /// that of the file or directory at <see cref="FullPath"/>; null for an in-memory input
  /// without metadata. Creators store it, so a rebuild (extract, edit, re-create) does not
  /// reset every entry's time.
  /// </summary>
  public DateTime? SourceLastModifiedUtc {
    get {
      if (this.Metadata?.LastWriteTimeUtc is { } stated) return stated.UtcDateTime;
      if (this.InMemoryContent != null || string.IsNullOrEmpty(this.FullPath)) return null;
      try {
        if (System.IO.File.Exists(this.FullPath) || System.IO.Directory.Exists(this.FullPath))
          return System.IO.File.GetLastWriteTimeUtc(this.FullPath);
      } catch (System.IO.IOException) {
      } catch (UnauthorizedAccessException) {
      }
      return null;
    }
  }

  /// <summary>
  /// The Unix permission bits (the low 12 bits of the mode) the entry should carry:
  /// <see cref="Metadata"/>'s when given, else the source file's on a Unix host; null when unknown.
  /// </summary>
  public System.IO.UnixFileMode? SourceUnixMode {
    get {
      if (this.Metadata?.UnixMode is { } stated) return (System.IO.UnixFileMode)(stated & 0xFFF);
      if (OperatingSystem.IsWindows() || this.InMemoryContent != null || string.IsNullOrEmpty(this.FullPath)) return null;
      try {
        return System.IO.File.Exists(this.FullPath) || System.IO.Directory.Exists(this.FullPath)
          ? System.IO.File.GetUnixFileMode(this.FullPath)
          : null;
      } catch (System.IO.IOException) {
        return null;
      } catch (UnauthorizedAccessException) {
        return null;
      }
    }
  }

  /// <summary>Returns the input's bytes: the in-memory content when present,
  /// otherwise the file at <see cref="FullPath"/>. Descriptors should call this
  /// instead of <c>File.ReadAllBytes(FullPath)</c> so they transparently support
  /// in-memory (temp-free) creation and conversion.</summary>
  public byte[] ReadContent() {
    if (this.InMemoryContent != null) return this.InMemoryContent;
    var data = System.IO.File.ReadAllBytes(this.FullPath);
    ReadObserver?.Invoke(data.LongLength);
    return data;
  }

  /// <summary>
  /// Told the byte count of every on-disk input as <see cref="ReadContent"/> reads it, so a host
  /// can show progress while an archive is created or rebuilt. Null when nobody listens. It is
  /// process-wide and may be called from several threads at once (parallel writers).
  /// </summary>
  public static Action<long>? ReadObserver { get; set; }
}
