#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileSystem.Xfs;

namespace FileSystem.Cxfs;

/// <summary>
/// Reader for the filesystem image used by SGI CXFS.
///
/// <para>SGI documents CXFS as using the same filesystem structure as XFS and
/// creating that filesystem with the same <c>mkfs</c>. The CXFS cluster database,
/// XVM topology, metadata-server state and fencing policy live outside the XFS
/// filesystem image. Accordingly this reader delegates the real file walk to
/// <see cref="XfsReader"/>.</para>
///
/// <para>The <c>sb_features2</c> value exposed here is ordinary XFS superblock
/// metadata. It is useful diagnostics for historical images, but it is not a
/// CXFS discriminator and no bit is treated as a CXFS marker.</para>
///
/// <para>When the XFS layer is too incomplete to contain a plausible root
/// directory, the reader falls back to a small detection surface containing
/// <c>metadata.ini</c> and the untouched image bytes. A valid empty XFS filesystem
/// is not mistaken for that fallback merely because it has zero directory
/// entries.</para>
/// </summary>
public sealed class CxfsReader : IDisposable {

  /// <summary>XFS superblock magic: ASCII "XFSB" (0x58465342 BE).</summary>
  public static readonly byte[] XfsbMagic = "XFSB"u8.ToArray();

  /// <summary>Offset of the legacy XFS <c>sb_features2</c> field in <c>xfs_dsb</c>.</summary>
  public const int SbFeatures2Offset = 0x82;

  private readonly byte[] _data;
  private readonly List<CxfsEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<CxfsEntry> Entries => _entries;
  /// <summary>
  /// Gets the XFS superblock magic.
  /// </summary>
  public uint XfsMagic { get; private set; }
  /// <summary>
  /// Gets the XFS <c>sb_features2</c> field for diagnostics.
  /// </summary>
  public uint SbFeatures2 { get; private set; }
  /// <summary>
  /// Gets a value indicating whether the XFS superblock header is valid.
  /// </summary>
  public bool ValidHeader { get; private set; }

  /// <summary>True when the XFS reader successfully accepted the filesystem,
  /// including a valid filesystem whose root directory is empty. False only
  /// when the detection-only fallback was required.</summary>
  public bool DelegatedToXfs { get; private set; }

  /// <summary>
  /// Initializes a new instance of <see cref="CxfsReader"/>.
  /// </summary>
  public CxfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SbFeatures2Offset + 4)
      throw new InvalidDataException("CXFS/XFS: file too small for the XFS superblock + sb_features2 field.");

    if (!_data.AsSpan(0, 4).SequenceEqual(XfsbMagic))
      throw new InvalidDataException("CXFS/XFS: missing 'XFSB' superblock magic at offset 0.");

    this.XfsMagic = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(0, 4));
    this.SbFeatures2 = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(SbFeatures2Offset, 4));
    this.ValidHeader = true;

    if (TryDelegateToXfs())
      return;

    BuildFallback();
  }

  private bool TryDelegateToXfs() {
    try {
      using var xfsStream = new MemoryStream(_data, writable: false);
      using var xfs = new XfsReader(xfsStream);
      var xfsEntries = xfs.Entries;

      // XfsReader intentionally fails closed by returning no entries when the
      // root inode cannot be walked. Distinguish that from a genuinely empty
      // filesystem by validating the root inode itself before accepting the
      // zero-entry result.
      if (xfsEntries.Count == 0 && !HasPlausibleRootDirectory())
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
      return false;
    }
  }

  /// <summary>
  /// Validates enough of the XFS root inode address to tell a valid empty root
  /// directory from a synthetic/truncated superblock that merely carries XFSB.
  /// The calculation mirrors the documented XFS inode-number geometry and the
  /// existing <see cref="XfsReader"/> address calculation; it does not interpret
  /// CXFS-specific state because there is none in the filesystem structure.
  /// </summary>
  private bool HasPlausibleRootDirectory() {
    if (_data.Length < 128)
      return false;

    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(4, 4));
    var rootIno = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(56, 8));
    var agBlocks = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(84, 4));
    var inodeSize = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(104, 2));
    var agBlkLog = _data[124];

    if (blockSize == 0 || inodeSize == 0 || agBlocks == 0 || rootIno == 0 || blockSize < inodeSize)
      return false;

    var inodesPerBlock = blockSize / inodeSize;
    if (inodesPerBlock == 0)
      return false;

    var inoPbLog = 0;
    for (var v = inodesPerBlock; v > 1; v >>= 1)
      ++inoPbLog;

    var aginoLog = agBlkLog + inoPbLog;
    if (aginoLog is <= 0 or >= 63)
      return false;

    var agNo = rootIno >> aginoLog;
    var agInoMask = (1UL << aginoLog) - 1;
    var agIno = rootIno & agInoMask;
    var block = agIno / inodesPerBlock;
    var slot = agIno % inodesPerBlock;

    ulong byteOffset;
    try {
      byteOffset = checked(((agNo * agBlocks) + block) * blockSize + slot * inodeSize);
    } catch (OverflowException) {
      return false;
    }

    if (byteOffset > int.MaxValue || byteOffset + inodeSize > (ulong)_data.LongLength)
      return false;

    var off = (int)byteOffset;
    if (BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(off, 2)) != 0x494E)
      return false;

    var mode = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(off + 2, 2));
    return (mode & 0xF000) == 0x4000;
  }

  private void BuildFallback() {
    var meta = BuildMetadata();
    _entries.Add(new CxfsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta, FromXfsLayer = false });
    _entries.Add(new CxfsEntry { Name = "cxfs-volume.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data, FromXfsLayer = false });
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only-fallback\n");
    bldr.Append("format=SGI CXFS filesystem image (XFS on disk)\n");
    bldr.Append("magic_tag=XFSB\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sb_features2_offset=0x{SbFeatures2Offset:X2}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sb_features2=0x{this.SbFeatures2:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append("note=XFS filesystem layer could not be walked; surfacing detection metadata only. ");
    bldr.Append("CXFS cluster configuration is external (cluster database/XVM), not encoded as a separate filesystem signature.\n");
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
