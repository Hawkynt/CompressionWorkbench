#pragma warning disable CS1591
namespace FileSystem.Nss;

/// <summary>
/// Best-effort NSS image reader. It keeps the historical anchor diagnostics and,
/// for seekable quiescent images, additionally scans the reverse-engineered
/// DirH/LEAF profile documented in docs/NSS-ON-DISK.md to reconstruct native
/// directories and ordinary contiguous file extents.
/// </summary>
public sealed class NssReader {
  private readonly byte[] _image;
  private readonly List<NssEntry> _entries = [];
  private readonly NssNativeScanner? _native;
  private readonly long _imageLength;

  /// <summary>
  /// Gets the headers.
  /// </summary>
  public NssHeaders Headers { get; }
  /// <summary>
  /// Gets or sets the volume name.
  /// </summary>
  public string VolumeName { get; private set; } = "";

  /// <summary>
  /// Diagnostic anchors plus native ordinary files. Native directories are
  /// available separately through <see cref="NativeEntries"/> so the legacy
  /// archive extraction path cannot mistake them for zero-byte files.
  /// </summary>
  public IReadOnlyList<NssEntry> Entries => this._entries;

  /// <summary>Native filesystem entries reconstructed from matching DirH/LEAF records.</summary>
  public IReadOnlyList<NssEntry> NativeEntries => this._native?.Entries ?? [];

  /// <summary>Bytes captured at the most useful anchor (pool / superblock / volume), 4 KB.</summary>
  public byte[] HeaderRaw => this.Headers.HeaderRaw;

  /// <summary>True iff at least one primary NSS anchor was located.</summary>
  public bool AnyValid => this.Headers.AnyValid;

  /// <summary>
  /// Gets the image length.
  /// </summary>
  public long ImageLength => this._imageLength;

  /// <summary>
  /// Initializes a new instance of <see cref="NssReader"/>.
  /// </summary>
  public NssReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var original = stream.CanSeek ? stream.Position : 0;
    if (stream.CanSeek) stream.Position = 0;

    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < NssHeaders.ScanLimit && (read = stream.Read(buf, 0, (int)Math.Min(buf.Length, NssHeaders.ScanLimit - ms.Length))) > 0)
      ms.Write(buf, 0, read);
    this._image = ms.ToArray();
    this._imageLength = stream.CanSeek ? stream.Length : this._image.LongLength;

    this.Headers = NssHeaders.TryParse(this._image);
    if (this.Headers.VolumeFound)
      this.VolumeName = NssHeaders.TryReadVolumeNameNear(this._image, this.Headers.VolumeFoundOffset);

    if (stream.CanSeek && this.Headers.AnyValid)
      this._native = new NssNativeScanner(stream);

    if (this.Headers.PoolFound) {
      this._entries.Add(new NssEntry {
        Name = $"pool_anchor_{this.Headers.PoolFoundOffset:X16}.bin",
        Size = 64,
        IsDirectory = false,
      });
    }
    if (this.Headers.SuperblockFound) {
      this._entries.Add(new NssEntry {
        Name = $"superblock_anchor_{this.Headers.SuperblockFoundOffset:X16}.bin",
        Size = 64,
        IsDirectory = false,
      });
    }
    if (this.Headers.VolumeFound) {
      this._entries.Add(new NssEntry {
        Name = $"volume_anchor_{this.Headers.VolumeFoundOffset:X16}.bin",
        Size = 64,
        IsDirectory = false,
      });
    }

    if (this._native != null)
      this._entries.AddRange(this._native.Entries.Where(entry => !entry.IsDirectory));

    if (stream.CanSeek) stream.Position = original;
  }

  /// <summary>Returns the synthetic anchor bytes or native file payload.</summary>
  public byte[] ExtractAnchor(NssEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsNativeNssEntry)
      return this.ExtractNative(entry);

    long anchor = -1;
    if (entry.Name.StartsWith("pool_anchor_", StringComparison.Ordinal)) anchor = this.Headers.PoolFoundOffset;
    else if (entry.Name.StartsWith("superblock_anchor_", StringComparison.Ordinal)) anchor = this.Headers.SuperblockFoundOffset;
    else if (entry.Name.StartsWith("volume_anchor_", StringComparison.Ordinal)) anchor = this.Headers.VolumeFoundOffset;

    if (anchor < 0 || anchor >= this._image.LongLength) return [];
    var n = (int)Math.Min(64L, this._image.LongLength - anchor);
    var result = new byte[64];
    Array.Copy(this._image, anchor, result, 0, n);
    return result;
  }

  /// <summary>Extracts one native ordinary-file entry from its validated contiguous extent.</summary>
  public byte[] ExtractNative(NssEntry entry)
    => this._native?.Extract(entry)
       ?? throw new NotSupportedException("Native NSS extraction requires a seekable image stream.");
}
