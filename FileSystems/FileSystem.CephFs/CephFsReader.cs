#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.CephFs;

/// <summary>
/// Stage 0 detection-only reader for CephFS / RADOS OSD object metadata
/// dumps. Ceph is a distributed object store (RADOS) with the CephFS
/// POSIX namespace layered over it via MDS daemons — files become RADOS
/// objects sharded across many OSDs. Single OSD object metadata dumps
/// begin with the ASCII tag <c>"CEPH"</c> (0x43 0x45 0x50 0x48 =
/// 0x43455048 BE).
///
/// Only the tag is verified. Full RADOS semantics (object name → PG
/// mapping via CRUSH, replica/EC erasure coding, MDS namespace
/// resolution) require a live Ceph cluster's mon/mds state.
///
/// <para><b>Stage-0 confirmation (no promotion possible from a single image).</b>
/// A CephFS volume is metadata-in-pool plus data-striped-across-OSDs:</para>
/// <list type="bullet">
///   <item><description><b>Metadata pool</b>: inodes, dirfrags, and the MDS
///     journal live as RADOS objects in a dedicated pool, mutated by MDS
///     daemons. Path resolution requires journal replay + dirfrag walking
///     across many objects.</description></item>
///   <item><description><b>Data objects</b>: each file is striped (default
///     stripe-unit 4 MiB) into RADOS objects named
///     <c>{inode-hex}.{stripe-index-hex}</c>, then placed via CRUSH against
///     the cluster's mon-map / osd-map / CRUSH-map.</description></item>
///   <item><description><b>OSD backing store</b>: BlueStore (RocksDB key/value
///     index over a raw block device) or legacy FileStore (object → file on a
///     local POSIX FS). Neither stores CephFS-level paths.</description></item>
/// </list>
/// <para>Promotion to R/O would require simultaneous access to a full OSD-set
/// snapshot, the live cluster maps (mon/mds/osd/CRUSH), and a BlueStore
/// reader — and even then the surface is OSD-level objects, not CephFS-level
/// paths. Conclusion: stay Stage 0. The honest deliverable is magic-tag
/// detection + metadata.ini + raw bytes.</para>
/// </summary>
public sealed class CephFsReader : IDisposable {

  /// <summary>Ceph OSD metadata tag: ASCII "CEPH" (0x43455048 BE).</summary>
  public static readonly byte[] CephTag = "CEPH"u8.ToArray();

  private const int HeaderSize = 8;

  private readonly byte[] _data;
  private readonly List<CephFsEntry> _entries = [];

  public IReadOnlyList<CephFsEntry> Entries => _entries;
  public uint MagicWord { get; private set; }
  public uint TrailingWord { get; private set; }
  public bool ValidHeader { get; private set; }

  public CephFsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < HeaderSize)
      throw new InvalidDataException("CephFS: file too small for OSD object header.");

    if (!_data.AsSpan(0, 4).SequenceEqual(CephTag))
      throw new InvalidDataException("CephFS: missing 'CEPH' tag at offset 0.");

    this.MagicWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(0, 4));
    this.TrailingWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(4, 4));
    this.ValidHeader = true;

    var meta = BuildMetadata();
    _entries.Add(new CephFsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });
    _entries.Add(new CephFsEntry { Name = "ceph-object.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data });
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only\n");
    bldr.Append("format=CephFS / RADOS OSD object\n");
    bldr.Append("magic_tag=CEPH\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic_word=0x{this.MagicWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"trailing_word=0x{this.TrailingWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append("note=Stage 0 — detection only. CephFS is a distributed object store + MDS namespace; ");
    bldr.Append("CRUSH PG mapping + MDS resolution require live cluster state.\n");
    bldr.Append("rationale=No standalone CephFS image exists: metadata lives in a RADOS metadata pool ");
    bldr.Append("(MDS-managed inodes / dirfrags / journal), file data is striped across many RADOS ");
    bldr.Append("objects placed via CRUSH across OSDs (BlueStore / FileStore backends).\n");
    bldr.Append("promotion_status=Stage 0 confirmed — R/O over a single image is structurally impossible.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(CephFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}
