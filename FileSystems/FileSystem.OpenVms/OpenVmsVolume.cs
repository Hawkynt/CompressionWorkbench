#pragma warning disable CS1591
using Compression.Core.DiskImage;

namespace FileSystem.OpenVms;

/// <summary>
/// Random-access view of a workbench-layout volume. Everything the reader needs to
/// enumerate files — the home block, BITMAP.SYS, INDEXF.SYS and the root
/// directory — lives in the fixed metadata prefix, so the volume is recognised
/// and listed from a few hundred kilobytes however large it is. File contents
/// are then copied straight out of the backing stream, never materialised.
/// <para>
/// This is the path the descriptor uses. <see cref="OpenVmsReader"/> keeps the
/// whole-image API for the in-place modifier, which works on volumes small
/// enough to hold in an array.
/// </para>
/// </summary>
public sealed class OpenVmsVolume : IDisposable {

  private readonly ImageAccessor _accessor;

  /// <summary>The fixed metadata prefix, or as much of it as the image holds.</summary>
  public byte[] Metadata { get; }

  /// <summary>Records that the volume's home block carries the workbench-layout layout marker.</summary>
  public bool IsCwbVolume { get; }

  /// <summary>User entries surfaced from 000000.DIR.</summary>
  public IReadOnlyList<OpenVmsReader.Entry> Entries { get; } = [];

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._accessor.Length;

  public OpenVmsVolume(Stream image, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(image);
    this._accessor = new ImageAccessor(image, leaveOpen);
    var prefix = (int)Math.Min(OpenVmsLayout.MetadataBytes, this._accessor.Length);
    this.Metadata = this._accessor.Read(0, prefix);
    this.IsCwbVolume = OpenVmsReader.HasLayoutMarker(this.Metadata);
    if (this.IsCwbVolume) this.Entries = this.ParseDirectory();
  }

  /// <summary>Reads the File Header for <paramref name="fileId"/> out of INDEXF.SYS.</summary>
  public OpenVmsFileHeader? ReadFileHeader(int fileId) => OpenVmsReader.ReadFileHeader(this.Metadata, fileId);

  /// <summary>
  /// Copies <paramref name="entry"/>'s contents into <paramref name="destination"/>
  /// by walking its retrieval pointers. Returns the number of bytes written.
  /// </summary>
  public long ExtractTo(OpenVmsReader.Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);

    var fh = this.ReadFileHeader(entry.FileId);
    if (fh == null || !fh.InUse) return 0;

    long written = 0;
    foreach (var ext in fh.Extents) {
      var remaining = fh.Size - written;
      if (remaining <= 0) break;
      var copy = Math.Min(ext.Count * (long)OpenVmsLayout.BlockSize, remaining);
      var srcOff = OpenVmsLayout.LbnToByteOffset(ext.StartLbn);
      // A truncated image yields a short read rather than an exception: the
      // descriptor's carver surface has to survive partial volumes.
      copy = Math.Min(copy, this._accessor.Length - srcOff);
      if (copy <= 0) break;
      this._accessor.CopyTo(srcOff, destination, copy);
      written += copy;
    }
    return written;
  }

  /// <summary>Copies <paramref name="count"/> bytes at <paramref name="offset"/> into <paramref name="destination"/>.</summary>
  public void CopyTo(long offset, Stream destination, long count)
    => this._accessor.CopyTo(offset, destination, count);

  private List<OpenVmsReader.Entry> ParseDirectory() {
    var entries = new List<OpenVmsReader.Entry>();
    var visited = new HashSet<int>();
    var lbn = OpenVmsLayout.RootDirectoryLbn;
    while (lbn > 0 && visited.Add(lbn)) {
      var off = OpenVmsLayout.LbnToByteOffset(lbn);
      if (off + OpenVmsLayout.BlockSize > this._accessor.Length) break;
      // The root block sits in the prefix; a chained continuation may not, so
      // the block always comes through the accessor.
      var blk = this._accessor.Read(off, OpenVmsLayout.BlockSize);
      foreach (var de in OpenVmsDirectory.Enumerate(blk)) {
        // The directory's entry for itself belongs to the volume, not to whoever
        // filled it, and listing it would put a name nobody added into every
        // listing.
        if (de.IsFree || OpenVmsDirectory.IsSelfEntry(de)) continue;

        entries.Add(new OpenVmsReader.Entry(de.FileId, de.Sequence, de.Name, de.Size));
      }
      lbn = OpenVmsDirectory.ReadChainLink(blk);
    }
    return entries;
  }

  public void Dispose() => this._accessor.Dispose();
}
