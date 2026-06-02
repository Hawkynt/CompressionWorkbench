using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// An entry returned by <see cref="ArchiveReader.Entries"/>. Carries the same
/// metadata as <see cref="ArchiveEntry"/> but additionally exposes
/// <see cref="OpenRead"/> and <see cref="CopyTo(string)"/> /
/// <see cref="CopyTo(Stream)"/> sugar — so callers can do
/// <c>foreach (var e in reader.Files) e.CopyTo(targetDir / e.FileName)</c>
/// without ever calling the format-level operations directly.
/// </summary>
/// <remarks>
/// <para>
/// All read operations route through the parent <see cref="ArchiveReader"/>,
/// which owns the underlying archive stream and the format ops dispatch. The
/// stream returned by <see cref="OpenRead"/> is always a
/// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> (or a
/// wrapper satisfying the same contract) sized to <see cref="Size"/>, so it
/// cannot leak slack space, adjacent entries, or padding bytes.
/// </para>
/// </remarks>
public sealed class ArchiveReaderEntry {

  private readonly ArchiveReader _owner;

  /// <summary>Full archive-relative path, forward-slash separated
  /// (e.g. <c>docs/readme.txt</c>).</summary>
  public string Name { get; }

  /// <summary>Parent path inside the archive (e.g. <c>docs</c>); empty string
  /// for entries at the root.</summary>
  public string Directory { get; }

  /// <summary>Leaf name (e.g. <c>readme.txt</c>).</summary>
  public string FileName { get; }

  /// <summary>Logical (uncompressed) size in bytes.</summary>
  public long Size { get; }

  /// <summary>True if this entry is a directory placeholder rather than a file.</summary>
  public bool IsDirectory { get; }

  /// <summary>True when the entry's data is encrypted in the archive.</summary>
  public bool IsEncrypted { get; }

  /// <summary>Last-modified timestamp, when the format records one.</summary>
  public DateTime? LastModified { get; }

  /// <summary>Compressed (on-disk) size when the format reports it; mirrors
  /// the value from <see cref="ArchiveEntryInfo.CompressedSize"/>.</summary>
  public long CompressedSize { get; }

  /// <summary>Compression method label as reported by the format.</summary>
  public string Method { get; }

  internal ArchiveReaderEntry(ArchiveReader owner, ArchiveEntryInfo info) {
    this._owner = owner;
    this.Name = info.Name;
    this.Size = info.OriginalSize;
    this.IsDirectory = info.IsDirectory;
    this.IsEncrypted = info.IsEncrypted;
    this.LastModified = info.LastModified;
    this.CompressedSize = info.CompressedSize;
    this.Method = info.Method ?? string.Empty;

    var idx = info.Name.LastIndexOf('/');
    if (idx < 0) {
      this.Directory = string.Empty;
      this.FileName = info.Name;
    } else {
      this.Directory = info.Name[..idx];
      this.FileName = info.Name[(idx + 1)..];
    }
  }

  /// <summary>
  /// Opens a bounded read-only stream over this entry's bytes. The stream
  /// cannot read past <see cref="Size"/> and cannot leak slack space,
  /// adjacent entries, or padding bytes. The caller disposes the returned
  /// stream.
  /// </summary>
  /// <remarks>
  /// Each call opens its own private view of the source archive, so two
  /// <c>OpenRead</c> streams can be active simultaneously (e.g. callers
  /// iterating <see cref="ArchiveReader.Files"/> with parallel processing).
  /// The native overrides on FAT/ZIP/TAR/7z and the 16 formats added in
  /// this commit all use seek-based reads, so the source can be cloned freely.
  /// </remarks>
  public Stream OpenRead() => this._owner.OpenEntryStream(this.Name);

  /// <summary>Extracts this entry's bytes to <paramref name="targetPath"/>.
  /// Creates parent directories. Existing files are overwritten.</summary>
  public void CopyTo(string targetPath) {
    ArgumentNullException.ThrowIfNull(targetPath);
    if (this.IsDirectory) {
      System.IO.Directory.CreateDirectory(targetPath);
      return;
    }
    var dir = Path.GetDirectoryName(targetPath);
    if (!string.IsNullOrEmpty(dir))
      System.IO.Directory.CreateDirectory(dir);
    using var src = this.OpenRead();
    using var dst = File.Create(targetPath);
    src.CopyTo(dst);
  }

  /// <summary>Copies this entry's bytes to <paramref name="target"/>.</summary>
  public void CopyTo(Stream target) {
    ArgumentNullException.ThrowIfNull(target);
    if (this.IsDirectory) return;
    using var src = this.OpenRead();
    src.CopyTo(target);
  }
}
