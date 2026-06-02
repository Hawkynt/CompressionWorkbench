#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.GlusterFs;

/// <summary>
/// Stage 0 detection-only reader for GlusterFS — permanent honest
/// fallback. GlusterFS itself has <b>no on-disk image format</b>: a
/// brick is a normal directory on a local POSIX filesystem
/// (XFS / ext4 / ...) and volume files are stored at their normal
/// POSIX paths inside that directory. All GlusterFS-specific state
/// lives in extended attributes (the <c>trusted.gfid</c>,
/// <c>trusted.glusterfs.dht</c>, <c>trusted.glusterfs.volume-id</c>,
/// <c>trusted.glusterfs.pathinfo</c> namespace).
///
/// Consequences:
/// <list type="bullet">
///   <item>There is no superblock or brick header to parse.</item>
///   <item>Distribution / replication state (DHT hashing → brick
///   mapping, AFR replicate metadata, EC dispersed metadata, rebalance
///   bookkeeping) only exists across multiple bricks on multiple
///   hosts, not inside any single image.</item>
///   <item>An R/O promotion is fundamentally incompatible with this
///   project's image-stream contract — recognising a GlusterFS
///   "volume" would require walking a live POSIX directory tree and
///   reading xattrs through the host OS, which is outside the
///   <c>Stream</c>-based <c>IArchiveFormatOperations</c> surface.</item>
/// </list>
///
/// The 0xCAFE5BAB magic verified by <see cref="Parse"/> is a
/// workbench-internal probe convention used to dump and round-trip
/// hand-crafted "brick object" experiments; it is <b>not</b> a real
/// on-disk GlusterFS marker and no real GlusterFS deployment produces
/// it. The reader therefore stays a thin two-entry detector
/// (synthetic <c>metadata.ini</c> + raw <c>gluster-brick.bin</c>) and
/// will never grow real semantics.
/// </summary>
public sealed class GlusterFsReader : IDisposable {

  /// <summary>
  /// Workbench-internal probe magic (0xCA 0xFE 0x5B 0xAB, 0xCAFE5BAB
  /// big-endian) used by the detector tests. <b>Not</b> a real
  /// GlusterFS structure — GlusterFS has no on-disk header at all.
  /// </summary>
  public static readonly byte[] BrickMagic = [0xCA, 0xFE, 0x5B, 0xAB];

  private const int HeaderSize = 8;

  private readonly byte[] _data;
  private readonly List<GlusterFsEntry> _entries = [];

  public IReadOnlyList<GlusterFsEntry> Entries => _entries;
  public uint MagicWord { get; private set; }
  public uint TrailingWord { get; private set; }
  public bool ValidHeader { get; private set; }

  public GlusterFsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < HeaderSize)
      throw new InvalidDataException("GlusterFS: file too small for brick object header.");

    if (!_data.AsSpan(0, 4).SequenceEqual(BrickMagic))
      throw new InvalidDataException("GlusterFS: missing 0xCAFE5BAB brick magic at offset 0.");

    this.MagicWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(0, 4));
    this.TrailingWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(4, 4));
    this.ValidHeader = true;

    var meta = BuildMetadata();
    _entries.Add(new GlusterFsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });
    _entries.Add(new GlusterFsEntry { Name = "gluster-brick.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data });
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only\n");
    bldr.Append("format=GlusterFS (no on-disk image; brick = normal directory on local FS)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic_word=0x{this.MagicWord:X8}\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append("magic_kind=workbench-internal probe (NOT a real GlusterFS marker)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"trailing_word=0x{this.TrailingWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append("stage=0\n");
    bldr.Append("stage_permanent=true\n");
    bldr.Append("ro_blocked_reason=GlusterFS has no on-disk image format; bricks are normal ");
    bldr.Append("directories on XFS/ext4 and state lives in xattrs (trusted.gfid, ");
    bldr.Append("trusted.glusterfs.*). Walking a live POSIX tree + reading xattrs is outside ");
    bldr.Append("the image-stream contract.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(GlusterFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}
