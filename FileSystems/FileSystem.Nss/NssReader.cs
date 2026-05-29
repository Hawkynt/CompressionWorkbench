#pragma warning disable CS1591
namespace FileSystem.Nss;

/// <summary>
/// Best-effort NSS image reader. Parses no object tree — only surfaces
/// the anchors NssHeaders located. Because the on-disk layout is
/// proprietary and lacks a verifiable public spec, we never claim to
/// reconstruct files; we expose the located pool/volume/superblock
/// offsets as synthetic entries the user can correlate with the raw
/// image.
/// </summary>
public sealed class NssReader {
  private readonly byte[] _image;
  private readonly List<NssEntry> _entries = new();

  public NssHeaders Headers { get; }
  public string VolumeName { get; private set; } = "";

  public IReadOnlyList<NssEntry> Entries => this._entries;

  /// <summary>Bytes captured at the most useful anchor (pool / superblock / volume), 4 KB.</summary>
  public byte[] HeaderRaw => this.Headers.HeaderRaw;

  /// <summary>True iff at least one primary NSS anchor was located.</summary>
  public bool AnyValid => this.Headers.AnyValid;

  public long ImageLength => this._image.LongLength;

  public NssReader(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    // Bounded to ScanLimit — NSS pool anchors live in the first 1 MB, and
    // we never extract user files because the object tree spec is unknown.
    while (ms.Length < NssHeaders.ScanLimit && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    this._image = ms.ToArray();

    this.Headers = NssHeaders.TryParse(this._image);
    if (this.Headers.VolumeFound)
      this.VolumeName = NssHeaders.TryReadVolumeNameNear(this._image, this.Headers.VolumeFoundOffset);

    // Surface synthetic per-anchor entries for the user. These are the only
    // "entries" we can honestly produce.
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
  }

  /// <summary>Returns the 64-byte window at the synthetic entry's anchor offset.</summary>
  public byte[] ExtractAnchor(NssEntry entry) {
    long anchor = -1;
    if (entry.Name.StartsWith("pool_anchor_", StringComparison.Ordinal)) anchor = this.Headers.PoolFoundOffset;
    else if (entry.Name.StartsWith("superblock_anchor_", StringComparison.Ordinal)) anchor = this.Headers.SuperblockFoundOffset;
    else if (entry.Name.StartsWith("volume_anchor_", StringComparison.Ordinal)) anchor = this.Headers.VolumeFoundOffset;

    if (anchor < 0 || anchor >= this._image.LongLength) return [];
    var n = (int)Math.Min(64L, this._image.LongLength - anchor);
    var buf = new byte[64];
    Array.Copy(this._image, anchor, buf, 0, n);
    return buf;
  }
}
