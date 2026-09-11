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
/// the superblock points at the LIN master, the LIN B+ tree maps logical inode
/// numbers to mirrored inode addresses, and file metatrees map logical blocks to
/// protection groups. Those addresses identify a node, drive and physical block,
/// so a lone drive image is not a self-contained filesystem namespace.
/// </para>
/// <para>
/// Dell does not publish a fixed raw-image magic value that identifies a OneFS
/// drive at offset zero. In particular, the historical <c>"OneFS"</c> / <c>"ONEF"</c>
/// literals previously used here could not be corroborated by Dell documentation
/// and are therefore not parsed or advertised as signatures. The reader is
/// reached by explicit format selection or the <c>.onefs</c> extension and treats
/// the supplied bytes as opaque media.
/// </para>
/// <para>
/// The reader deliberately performs no payload reads while listing. This keeps a
/// multi-terabyte disk image out of managed memory and, more importantly, avoids
/// inventing structure from undocumented bytes. The raw image remains available
/// as a bounded streaming entry for forensic inspection or export.
/// </para>
/// <para>
/// References used for the clean-room behaviour documented here:
/// <list type="bullet">
///   <item><description>Dell Technologies Info Hub, "OneFS Metadata" (LIN tree,
///     inode mirrors, IFM/DFM B+ trees and protection groups).</description></item>
///   <item><description>Dell PowerScale OneFS Technical Overview, "File system
///     structure" (distributed UFS-based filesystem and single namespace).</description></item>
///   <item><description>Dell PowerScale OneFS Technical Specifications Guide
///     (8 KiB filesystem block size).</description></item>
/// </list>
/// No external implementation code is copied or translated.
/// </para>
/// </remarks>
public sealed class OneFsReader : IDisposable {

  /// <summary>Documented OneFS filesystem block size.</summary>
  public const int PhysicalBlockSize = 8 * 1024;

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
    builder.Append("physical_block_size=").Append(PhysicalBlockSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
    builder.Append("image_size=").Append(this.ImageSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
    builder.Append("rw_promotion=blocked\n");
    builder.Append("rw_promotion_reason_1=OneFS exposes one namespace across the cluster, not one self-contained namespace per drive\n");
    builder.Append("rw_promotion_reason_2=LIN B+ tree entries resolve logical inode numbers to mirrored inode addresses on node+drive+block tuples\n");
    builder.Append("rw_promotion_reason_3=IFM metatrees resolve logical file blocks to protection groups distributed across cluster nodes and drives\n");
    builder.Append("rw_promotion_reason_4=no published raw-media serialization/update specification or offline single-drive checker was found\n");
    builder.Append("maintenance=blocked\n");
    builder.Append("maintenance_reason=free/live allocation, relocation metadata, protection-group membership and transaction rules cannot be proven from one opaque image\n");
    builder.Append("ufs_note=Dell describes OneFS as UFS-based; that architectural ancestry does not make an isolated /ifs drive image a generic standalone UFS volume\n");
    builder.Append("note=The raw image is exposed byte-for-byte for inspection only; no undocumented bytes are interpreted or rewritten.\n");
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
