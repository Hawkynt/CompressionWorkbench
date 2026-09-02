#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileSystem.Xfs;

namespace FileSystem.Cxfs;

/// <summary>
/// R/O reader for SGI CXFS (Cluster XFS) volume images via delegation to
/// <see cref="XfsReader"/>.
///
/// <para>CXFS is SGI's clustered extension of XFS. The on-disk format is
/// XFS-compatible — same <c>"XFSB"</c> superblock magic at offset 0, same
/// <c>xfs_dsb</c> layout, same <c>dinode</c> (IN magic) layout, and same
/// dir2/dir3 directory block formats. CXFS-specific bits live in
/// <c>sb_features2</c> (offset 0x82) and in cluster-tracking fields that
/// the lock-managing layer (CMS / dmF) consults at mount time; they do
/// not modify the file/directory on-disk structures.</para>
///
/// <para>Because of that, a CXFS DAT image whose XFS layer is well-formed
/// is readable by the vanilla XFS reader. This reader first tries the
/// XFS reader; on success it surfaces the underlying XFS entries to the
/// caller (cluster metadata is intentionally ignored — that is the
/// distributed-lock / quorum / RGM layer, not file content). On failure
/// it falls back to the Stage-0 <c>metadata.ini</c> + <c>cxfs-volume.bin</c>
/// surface so the descriptor still identifies the image.</para>
///
/// <para>Honest caveat: real CXFS production volumes may use SGI-private
/// fork formats for cluster-quota and DMAPI metadata that the open-source
/// XFS reader does not understand; such inodes will simply be skipped by
/// the XFS reader (it ignores unknown <c>di_format</c> values), and any
/// data lurking in CXFS-only metadata regions will not be surfaced. Plain
/// file content stored as XFS extents / inline data IS readable.</para>
/// </summary>
public sealed class CxfsReader : IDisposable {

  /// <summary>XFS superblock magic: ASCII "XFSB" (0x58465342 BE).</summary>
  public static readonly byte[] XfsbMagic = "XFSB"u8.ToArray();

  /// <summary>Offset of sb_features2 field in the XFS superblock (xfs_dsb).</summary>
  public const int SbFeatures2Offset = 0x82;

  private readonly byte[] _data;
  private readonly List<CxfsEntry> _entries = [];

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<CxfsEntry> Entries => _entries;
    /// <summary>
  /// Gets or sets the xfs magic.
  /// </summary>
public uint XfsMagic { get; private set; }
    /// <summary>
  /// Gets or sets the sb features 2.
  /// </summary>
public uint SbFeatures2 { get; private set; }
    /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
public bool ValidHeader { get; private set; }

  /// <summary>True when the XFS reader successfully walked the image and
  /// produced at least one real file/directory entry. False when we fell
  /// back to the Stage-0 metadata-only surface.</summary>
  public bool DelegatedToXfs { get; private set; }

    /// <summary>
  /// Initializes a new instance of <see cref="CxfsReader"/>.
  /// </summary>
public CxfsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SbFeatures2Offset + 4)
      throw new InvalidDataException("CXFS: file too small for XFS superblock + sb_features2.");

    if (!_data.AsSpan(0, 4).SequenceEqual(XfsbMagic))
      throw new InvalidDataException("CXFS: missing 'XFSB' superblock magic at offset 0.");

    this.XfsMagic = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(0, 4));
    this.SbFeatures2 = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(SbFeatures2Offset, 4));
    this.ValidHeader = true;

    // First attempt: delegate the XFS layer walk. Real CXFS images store
    // file content via the same XFS dinodes / extents, so the vanilla XFS
    // reader returns real entries. Cluster-private metadata is ignored —
    // documented above.
    if (TryDelegateToXfs())
      return;

    // Stage-0 fallback when the XFS reader returned nothing (no walkable
    // root inode, or this is a CXFS-internal dump without file content).
    BuildFallback();
  }

  private bool TryDelegateToXfs() {
    try {
      using var xfsStream = new MemoryStream(_data, writable: false);
      var xfs = new XfsReader(xfsStream);
      var xfsEntries = xfs.Entries;
      if (xfsEntries.Count == 0)
        return false;

      foreach (var xe in xfsEntries) {
        var data = xe.IsDirectory ? [] : xfs.Extract(xe);
        _entries.Add(new CxfsEntry {
          Name = xe.Name,
          Size = xe.Size,
          IsDirectory = xe.IsDirectory,
          Offset = 0,
          Data = data,
          FromXfsLayer = true,
        });
      }
      this.DelegatedToXfs = true;
      return true;
    } catch {
      // Any XFS-layer failure (malformed inode, unsupported fork format,
      // truncated image) -> fall back to detection-only metadata.
      return false;
    }
  }

  private void BuildFallback() {
    var meta = BuildMetadata();
    _entries.Add(new CxfsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta, FromXfsLayer = false });
    _entries.Add(new CxfsEntry { Name = "cxfs-volume.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data, FromXfsLayer = false });
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only-fallback\n");
    bldr.Append("format=SGI CXFS (cluster XFS) superblock\n");
    bldr.Append("magic_tag=XFSB\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sb_features2_offset=0x{SbFeatures2Offset:X2}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sb_features2=0x{this.SbFeatures2:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append("note=XFS layer reader could not walk this image; surfacing detection metadata only. ");
    bldr.Append("CXFS shares XFS 'XFSB' magic; cluster-aware bits live in sb_features2 and require SGI/Trusted XFS tools.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(CxfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
