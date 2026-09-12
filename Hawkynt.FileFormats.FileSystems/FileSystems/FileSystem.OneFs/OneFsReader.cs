#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry.Streaming;

namespace FileSystem.OneFs;

/// <summary>
/// Conservative single-image inspection surface for Dell PowerScale / Isilon
/// OneFS media.
/// </summary>
/// <remarks>
/// <para>
/// Dell documents the cluster-level structures required to resolve OneFS data:
/// superblocks live at multiple fixed block addresses on every drive and point
/// at the LIN master, the LIN B+ tree maps logical inode numbers to mirrored
/// inode addresses, and file metatrees map logical blocks to protection groups.
/// Those addresses identify a node, drive and physical block, so a lone drive
/// image is not a self-contained filesystem namespace.
/// </para>
/// <para>
/// Dell also documents enough physical geometry for safe analysis: every data
/// disk is divided into 32 MiB cylinder groups made of 8 KiB filesystem blocks,
/// with a per-cylinder-group bitmap tracking whether blocks are used for data,
/// inodes or other metadata. The bitmap's raw serialization and location are not
/// publicly specified, so this reader reports the geometry but does not attempt
/// to interpret allocation state.
/// </para>
/// <para>
/// Dell does not publish a fixed raw-image magic value that identifies a OneFS
/// drive at offset zero. In particular, the historical <c>"OneFS"</c> / <c>"ONEF"</c>
/// literals previously used here could not be corroborated by Dell documentation
/// and are therefore not parsed or advertised as signatures. Public OneFS logs
/// do show that a superblock magic is validated, but neither its authoritative
/// value nor the fixed superblock block addresses have been found in a public
/// byte-level specification.
/// </para>
/// <para>
/// The reader deliberately performs no payload reads while listing. This keeps a
/// multi-terabyte disk image out of managed memory and, more importantly, avoids
/// inventing structure from undocumented bytes. The raw image remains available
/// as a bounded streaming entry for forensic inspection or export.
/// </para>
/// </remarks>
public sealed class OneFsReader : IDisposable {

  /// <summary>Documented OneFS filesystem block size.</summary>
  public const int PhysicalBlockSize = 8 * 1024;

  /// <summary>Documented physical cylinder-group size of each OneFS data disk.</summary>
  public const int CylinderGroupSize = 32 * 1024 * 1024;

  /// <summary>Documented number of 8 KiB blocks in a 32 MiB cylinder group.</summary>
  public const int BlocksPerCylinderGroup = CylinderGroupSize / PhysicalBlockSize;

  /// <summary>Name of the synthetic inspection metadata entry.</summary>
  public const string MetadataEntryName = "metadata.ini";

  /// <summary>Name of the opaque raw-image entry.</summary>
  public const string RawImageEntryName = "onefs-volume.bin";

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<OneFsEntry> _entries = [];

  /// <summary>Gets the synthetic entries exposed by the conservative reader.</summary>
  public IReadOnlyList<OneFsEntry> Entries => this._entries;

  /// <summary>
  /// Legacy compatibility property. No authoritative fixed OneFS raw-media tag
  /// is currently known, so this value is always empty.
  /// </summary>
  public string Tag { get; private set; } = "";

  /// <summary>
  /// Legacy compatibility property. No undocumented trailing header word is
  /// interpreted; this value is always zero.
  /// </summary>
  public uint TrailingWord { get; private set; }

  /// <summary>
  /// Legacy compatibility property. The current reader does not claim to have
  /// validated a proprietary raw-media header, so this value is always false.
  /// </summary>
  public bool ValidHeader { get; private set; }

  /// <summary>Gets the byte length of the opaque source image.</summary>
  public long ImageSize { get; }

  /// <summary>
  /// Initializes a conservative OneFS image reader without consuming or owning
  /// the source stream. Kept as the original one-argument public constructor for
  /// binary/source API compatibility.
  /// </summary>
  public OneFsReader(Stream stream) : this(stream, leaveOpen: true) { }

  /// <summary>
  /// Initializes a conservative OneFS image reader without consuming the image.
  /// </summary>
  /// <param name="stream">Readable, seekable raw image stream.</param>
  /// <param name="leaveOpen">Whether disposing the reader leaves the source open.</param>
  public OneFsReader(Stream stream, bool leaveOpen) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      throw new ArgumentException("OneFS inspection requires a readable stream.", nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("OneFS inspection requires a seekable stream.", nameof(stream));
    if (stream.Length <= 0)
      throw new InvalidDataException("OneFS: empty image cannot be inspected.");

    this._stream = stream;
    this._leaveOpen = leaveOpen;
    this.ImageSize = stream.Length;
    this.BuildEntries();
  }

  private void BuildEntries() {
    var metadata = this.BuildMetadata();
    this._entries.Add(new OneFsEntry {
      Name = MetadataEntryName,
      Size = metadata.Length,
      IsDirectory = false,
      Offset = 0,
      Data = metadata,
    });
    this._entries.Add(new OneFsEntry {
      Name = RawImageEntryName,
      Size = this.ImageSize,
      IsDirectory = false,
      Offset = 0,
      Data = [],
    });
  }

  private byte[] BuildMetadata() {
    var builder = new StringBuilder();
    builder.Append("parse_status=opaque-single-image\n");
    builder.Append("stage=0\n");
    builder.Append("format=Dell PowerScale / Isilon OneFS\n");
    builder.Append("detection=extension-or-explicit-selection\n");
    builder.Append("authoritative_raw_magic=not_published\n");
    builder.Append("superblock_magic=known-to-exist-value-not-publicly-verified\n");
    builder.Append("superblock_locations=multiple-fixed-block-addresses-values-not-publicly-verified\n");
    builder.Append("superblock_role=references-LIN-master\n");
    builder.Append("physical_block_size=").Append(PhysicalBlockSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
    builder.Append("cylinder_group_size=").Append(CylinderGroupSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
    builder.Append("blocks_per_cylinder_group=").Append(BlocksPerCylinderGroup.ToString(CultureInfo.InvariantCulture)).Append('\n');
    builder.Append("allocation_tracking=per-cylinder-group-bitmap-serialization-not-published\n");
    builder.Append("image_size=").Append(this.ImageSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
    builder.Append("rw_promotion=blocked\n");
    builder.Append("rw_promotion_reason_1=OneFS exposes one namespace across the cluster, not one self-contained namespace per drive\n");
    builder.Append("rw_promotion_reason_2=LIN B+ tree entries resolve logical inode numbers to mirrored inode addresses on node+drive+block tuples\n");
    builder.Append("rw_promotion_reason_3=IFM metatrees resolve logical file blocks to protection groups distributed across cluster nodes and drives\n");
    builder.Append("rw_promotion_reason_4=safe writes use distributed two-phase commit and per-node journals\n");
    builder.Append("rw_promotion_reason_5=no published byte-level allocation/tree/journal serialization or offline single-drive checker was found\n");
    builder.Append("maintenance=blocked\n");
    builder.Append("maintenance_reason=bitmap location and encoding, relocation metadata, protection-group membership and transaction rules cannot be proven from one opaque image\n");
    builder.Append("ufs_note=OneFS is FreeBSD-derived and early Isilon material describes BAM as working with or instead of BSD UFS; an isolated /ifs data drive is not established as a generic standalone UFS volume\n");
    builder.Append("note=The raw image is exposed byte-for-byte for inspection only; only documented geometry is interpreted.\n");
    return Encoding.UTF8.GetBytes(builder.ToString());
  }

  /// <summary>
  /// Opens an entry as a bounded read-only stream. Opening the raw image resets
  /// the source to byte zero but never copies the payload into managed memory.
  /// </summary>
  public Stream OpenEntry(OneFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (string.Equals(entry.Name, MetadataEntryName, StringComparison.Ordinal))
      return new BoundedEntryStream(new MemoryStream(entry.Data, writable: false), entry.Data.LongLength, leaveOpen: false);

    if (!string.Equals(entry.Name, RawImageEntryName, StringComparison.Ordinal))
      throw new FileNotFoundException($"OneFS entry not found: {entry.Name}", entry.Name);

    this._stream.Position = 0;
    return new BoundedEntryStream(this._stream, this.ImageSize, leaveOpen: true);
  }

  /// <summary>
  /// Materializes an entry in memory. Prefer <see cref="OpenEntry"/> for the raw
  /// image so large media remains streaming.
  /// </summary>
  public byte[] Extract(OneFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > Array.MaxLength)
      throw new IOException($"OneFS entry '{entry.Name}' is too large to materialize; use OpenEntry for streaming access.");

    using var source = this.OpenEntry(entry);
    using var target = new MemoryStream(entry.Size > 0 ? checked((int)entry.Size) : 0);
    source.CopyTo(target);
    return target.ToArray();
  }

  /// <summary>Releases the source stream when ownership was requested.</summary>
  public void Dispose() {
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
