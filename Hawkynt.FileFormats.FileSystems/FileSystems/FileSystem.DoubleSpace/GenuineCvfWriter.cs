#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.DoubleSpace;

/// <summary>
/// Builds a <b>genuine</b> MS-DOS DoubleSpace/DriveSpace Compressed Volume File
/// (CVF) in the on-disk shape a real DoubleSpace/DriveSpace driver mounts —
/// verified by the independent GPL <c>dmsdos</c> driver (the Linux
/// DoubleSpace/DriveSpace/Stacker driver): it mounts this writer's output,
/// lists the inner directory, and reads every file back byte-exact
/// (detected as "drivespace CVF version 2").
/// <para>
/// The writer reproduces the real <c>MSDBL6.0</c> container:
/// </para>
/// <list type="bullet">
///   <item><b>Sector 0</b> — MDBPB: standard BPB (512 B/sector, 16 sec/cluster,
///     28 reserved, 1 FAT, 512 root entries, 132-sector FAT) plus the
///     DoubleSpace geometry substructure (inner-volume base sector @0x27, root
///     @0x29, first-data @0x2B, MDFAT index offset @0x2D, MDFAT-start-1 @0x24)
///     and the size-tier words a driver-created volume carries.</item>
///   <item><b>Sectors 1–133</b> — BitFAT: one bit per physical sector of the cluster heap.</item>
///   <item><b>Sector 134</b> — MDFAT: one little-endian u32 per inner-volume
///     cluster at <c>(0x24+1)*512 + (0x2D + cluster)*4</c>; physical sector =
///     <c>(entry &amp; 0x1FFFFF) + 1</c>.</item>
///   <item><b>Sector 429 (inner base)</b> — the inner FAT12 volume's boot sector, FAT and root
///     directory; the cluster heap follows two sectors after them.</item>
/// </list>
/// Files must fit the inner FAT12 volume (at most 4080 clusters of 8 KB).
/// </summary>
public sealed class GenuineCvfWriter {

  // Container geometry, as on volumes MS-DOS 6.22's DRVSPACE /CREATE writes.
  private const int Ss = 512;
  private const int Spc = 16;                 // sectors per cluster (8 KB)
  private const int MinResv = 16;             // reserved sectors, at least
  private const int NumFats = 1;              // the CVF stores one FAT; the inner boot sector shows DOS two
  private const int RootEntries = 512;
  private const int RootSecs = RootEntries * 32 / Ss;          // 32
  private const int ClusterBytes = Ss * Spc;                   // 8192

  // The system-area geometry a driver-created volume carries when its maximum size is the smallest
  // tier (a volume on a host of up to 32 MB): a 132-sector FAT area, the BitFAT in sectors 1..133, the
  // MDFAT from sector 134 (two sectors per FAT sector plus 31), the inner volume from sector 429. The
  // MDBPB repeats the tier in 0x2F (FAT sectors / 4), 0x35 / 0x37 and 0x3E (FAT sectors * 2); the
  // real driver and its checker take them together, so they are written as one set.
  private const int FatSectors = 132;
  private const int MdfatStartSec = FatSectors + 2;                       // 134
  private const int MdfatSectors = FatSectors * 2 + 31;                   // 295
  private const int InnerBase = MdfatStartSec + MdfatSectors;             // 429
  private const ushort TierWord2F = FatSectors / 4;                       // 33
  private const ushort TierWord35 = 20992;
  private const ushort TierWord37 = 5;
  private const ushort TierWord3E = FatSectors * 2;                       // 264

  // FAT12 holds at most 4084 clusters; DOS reads a FAT16 beyond that.
  private const int MaxClusters = 4084;

  private int _fatSize;       // inner FAT sectors (FatSectors; FAT12 entries)
  private int _innerBase;     // res0 / emulated boot block sector
  private int _resv;          // reserved sectors: at least MinResv, raised so _firstData is cluster-aligned
  private int _rootLog;       // _resv + NumFats*fatSize
  private int _firstData;     // rootLog + RootSecs, a multiple of Spc
  private int _mdfatIdxOff;   // MDFAT index of cluster 0: _firstData / Spc - 2

  // MDFAT stored-run flag bits (upper word), per the real driver's encoding.
  // 0xFFC0… marks a full 16-sector stored cluster: the driver reads all 16
  // sectors and the inner-FAT directory's file size truncates the tail, so we
  // store EVERY cluster (including a file's final, partly-filled one) as a full
  // 16-sector run with the unused tail zero-padded — sidestepping the separate
  // partial-run encoding while staying byte-exact.
  private const uint FlagFullCluster = 0xFFC00000;

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Optional inner-volume label (≤11 chars). Empty = no label entry.</summary>
  public string VolumeLabel { get; init; } = "";

  /// <summary>Creation/modification timestamp stamped on every file entry.
  /// Default (before 1980) leaves the FAT date/time fields zero.</summary>
  public DateTime Timestamp { get; init; }

  /// <summary>Per-cluster compression codec: Stored (default), DS (DoubleSpace DS-0-0) or JM (DriveSpace
  /// JM-0-0). Anything else is refused by <see cref="Build"/>.</summary>
  public Compression.Core.FileSystems.Cvf.CvfLzMethod CompressionMethod { get; init; }
    = Compression.Core.FileSystems.Cvf.CvfLzMethod.Stored;

  /// <summary>Codec effort (search depth). Higher = better ratio, slower.</summary>
  public int CompressionLevel { get; init; } = 1;

  /// <summary>Keep a compressed cluster even if it does not shrink (auto-best off).</summary>
  public bool ForceCompress { get; init; }

  /// <summary>Adds a file to the root directory of the compressed volume.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var leaf = Path.GetFileName(name.Replace('\\', '/').TrimEnd('/'));
    if (string.IsNullOrEmpty(leaf)) throw new ArgumentException("File name must not be empty.", nameof(name));
    this._files.Add((leaf, data));
  }

  private readonly record struct ClusterPlan(
    int Cluster, byte[] Payload, int Sectors, bool Compressed, int PhysSector);

  /// <summary>Builds the CVF image bytes.</summary>
  public byte[] Build() {
    // A version-2 volume carries DoubleSpace's DS or DriveSpace's JM clusters; SQ (and the codec-level
    // Auto, which may pick either) are not written into it.
    if (this.CompressionMethod is not (Compression.Core.FileSystems.Cvf.CvfLzMethod.Stored
        or Compression.Core.FileSystems.Cvf.CvfLzMethod.Ds or Compression.Core.FileSystems.Cvf.CvfLzMethod.Jm))
      throw new NotSupportedException($"A DoubleSpace/DriveSpace volume holds stored, DS or JM clusters; {this.CompressionMethod} is not written into it.");
    if (this._files.Count + (string.IsNullOrEmpty(this.VolumeLabel) ? 0 : 1) > RootEntries)
      throw new NotSupportedException($"The root directory holds {RootEntries} entries; {this._files.Count} files were given.");
    // 1. Plan files → clusters; capture each file's first cluster + size, and
    //    each cluster's full (zero-padded) logical bytes.
    var files = new List<(string Name, int First, int Count, long Size)>();
    var clusterFull = new List<(int Cluster, byte[] Full)>();
    var nextCluster = 2;
    foreach (var (name, data) in this._files) {
      var count = Math.Max(1, (data.Length + ClusterBytes - 1) / ClusterBytes);
      files.Add((name, nextCluster, count, data.Length));
      for (var i = 0; i < count; i++) {
        var full = new byte[ClusterBytes];
        var off = i * ClusterBytes;
        var copy = Math.Min(ClusterBytes, data.Length - off);
        if (copy > 0) Array.Copy(data, off, full, 0, copy);
        clusterFull.Add((nextCluster + i, full));
      }
      nextCluster += count;
    }

    // Size geometry to the cluster count.
    var maxCluster = nextCluster - 1;
    if (maxCluster + 3 > MaxClusters)
      throw new NotSupportedException($"The files need {maxCluster - 1} clusters; a FAT12 compressed volume holds at most {MaxClusters - 4}.");
    this._fatSize = FatSectors;
    // The real driver maps the inner volume in whole-cluster slots: the MDFAT index of a cluster is
    // cluster + firstData / Spc - 2, so the system area (reserved, FAT, root) must end on a cluster
    // boundary; the reserved area absorbs the padding (28 sectors for the 132-sector FAT).
    this._resv = MinResv;
    while ((this._resv + NumFats * this._fatSize + RootSecs) % Spc != 0) ++this._resv;
    this._rootLog = this._resv + NumFats * this._fatSize;
    this._firstData = this._rootLog + RootSecs;
    this._mdfatIdxOff = this._firstData / Spc - 2;
    this._innerBase = InnerBase;

    // 2. Compress each cluster and pack into physical sectors. Cluster runs start two sectors past
    //    the inner system area, where the BitFAT's first bit points (as on driver-created volumes).
    var physDataStart = this._innerBase + this._firstData + 2;
    var physCursor = physDataStart;
    var layout = new List<ClusterPlan>();
    foreach (var (cl, full) in clusterFull) {
      var comp = Compression.Core.FileSystems.Cvf.CvfLzCodec.Encode(full, this.CompressionMethod, this.CompressionLevel);
      var ksize = comp is null ? Spc : (comp.Length + Ss - 1) / Ss;
      if (comp is not null && ksize <= Spc && (ksize < Spc || this.ForceCompress)) {
        layout.Add(new ClusterPlan(cl, comp, ksize, true, physCursor));
        physCursor += ksize;
      } else {
        layout.Add(new ClusterPlan(cl, full, Spc, false, physCursor));
        physCursor += Spc;
      }
    }

    var totalSectors = Math.Max(1152, physCursor + 2);
    if ((totalSectors & 1) != 0) totalSectors++;
    var img = new byte[totalSectors * Ss];

    // BPB total-sectors (0x20) must keep dmsdos's DOS-limit max_cluster above the
    // real cluster count even when compression shrinks the physical image.
    var declaredSectors = Math.Max(totalSectors,
      (maxCluster + 2) * Spc + this._fatSize + this._resv + (RootEntries >> 4) + Spc);

    this.WriteMdbpb(img, declaredSectors);
    this.WriteInnerBoot(img, declaredSectors);

    var innerOff = this._innerBase * Ss;
    var fatOff = innerOff + this._resv * Ss;
    var rootOff = innerOff + this._rootLog * Ss;
    var mdfatBase = MdfatStartSec * Ss + this._mdfatIdxOff * 4;

    // FAT12 reserved entries (clusters 0 and 1).
    img[fatOff] = 0xF8; img[fatOff + 1] = 0xFF; img[fatOff + 2] = 0xFF;

    var dirIndex = 0;
    if (!string.IsNullOrEmpty(this.VolumeLabel)) {
      Compression.Registry.FatDirStamp.WriteVolumeLabel(img, rootOff, this.VolumeLabel);
      dirIndex = 1;
    }
    var (stampTime, stampDate) = Compression.Registry.FatDirStamp.Encode(this.Timestamp);

    foreach (var (name, first, count, size) in files) {
      var de = rootOff + dirIndex * 32;
      WriteShortName(img, de, name);
      img[de + 11] = 0x20;
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(de + 22), stampTime);
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(de + 24), stampDate);
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(de + 26), (ushort)first);
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(de + 28), (uint)size);
      dirIndex++;

      for (var i = 0; i < count; i++) {
        var cluster = first + i;
        var isLast = i == count - 1;
        WriteFat12(img, fatOff, cluster, isLast ? 0xFFF : cluster + 1);
      }
    }

    // Cluster payloads + 4-byte MDFAT entries (DBLSP/DRVSP packing: sector in
    // bits 0..20, size_lo bits 22..25, size_hi bits 26..29, flags bits 30..31).
    foreach (var plan in layout) {
      Array.Copy(plan.Payload, 0, img, plan.PhysSector * Ss, plan.Payload.Length);
      var entry = ((uint)(plan.PhysSector - 1) & 0x1FFFFFu)
        | ((uint)(plan.Sectors - 1) << 22)
        | (15u << 26)
        | ((uint)(plan.Compressed ? 2 : 3) << 30);
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(mdfatBase + plan.Cluster * 4), entry);
    }

    // BitFAT (from sector 1): one bit per physical sector from physDataStart, set for every sector a
    // cluster run occupies; bits run MSB-first through little-endian 16-bit words. The real driver
    // allocates new runs from this map, so an unmarked run would be overwritten.
    foreach (var plan in layout)
      for (var s = plan.PhysSector; s < plan.PhysSector + plan.Sectors; ++s) {
        var bit = s - physDataStart;
        var word = Ss + bit / 16 * 2;
        var mask = 0x8000 >> (bit % 16);
        img[word] |= (byte)mask;
        img[word + 1] |= (byte)(mask >> 8);
      }

    // Signatures: "MDR\0" opens the last sector of the file, "<media>DR\0" the inner volume's second sector.
    "MDR\0"u8.CopyTo(img.AsSpan(img.Length - Ss));
    img[(this._innerBase + 1) * Ss] = 0xF8;
    "DR\0"u8.CopyTo(img.AsSpan((this._innerBase + 1) * Ss + 1));
    return img;
  }

  private void WriteMdbpb(byte[] img, int declaredSectors) {
    img[0] = 0xEB; img[1] = 0x3C; img[2] = 0x90;
    "MSDBL6.0"u8.CopyTo(img.AsSpan(3, 8));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0B), Ss);
    img[0x0D] = Spc;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0E), (ushort)this._resv);
    img[0x10] = NumFats;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x11), RootEntries);
    img[0x15] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x16), (ushort)this._fatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x18), 17);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x1A), 6);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x20), (uint)declaredSectors);
    // DoubleSpace geometry substructure.
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x24), (ushort)(MdfatStartSec - 1));
    img[0x26] = 9;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x27), (ushort)this._innerBase);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x29), (ushort)this._rootLog);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x2B), (ushort)this._firstData);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x2D), (ushort)this._mdfatIdxOff);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x2F), TierWord2F);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x31), 1024);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x33), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x35), TierWord35);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x37), TierWord37);
    "16  "u8.CopyTo(img.AsSpan(0x39, 4));
    img[0x3D] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x3E), TierWord3E);
  }

  // The inner boot sector is what DOS sees: two FATs (the driver serves the one stored FAT for both),
  // so its total is the MDBPB's plus one FAT; 63 sectors per track, 16 heads — as on driver-created
  // volumes.
  private void WriteInnerBoot(byte[] img, int declaredSectors) {
    var b = this._innerBase * Ss;
    img[b] = 0xEB; img[b + 1] = 0x3C; img[b + 2] = 0x90;
    "MSDBL6.0"u8.CopyTo(img.AsSpan(b + 3, 8));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x0B), Ss);
    img[b + 0x0D] = Spc;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x0E), (ushort)this._resv);
    img[b + 0x10] = 2;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x11), RootEntries);
    img[b + 0x15] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x16), (ushort)this._fatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x18), 63);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x1A), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(b + 0x20), (uint)(declaredSectors + this._fatSize));
    img[b + 0x26] = 0x29; // extended boot signature
    "COMPRESSED "u8.CopyTo(img.AsSpan(b + 0x2B, 11));
    "FAT12   "u8.CopyTo(img.AsSpan(b + 0x36, 8));
    img[b + 0x1FE] = 0x55; img[b + 0x1FF] = 0xAA;
  }

  private static void WriteFat12(byte[] img, int fatOff, int cluster, int value) {
    var o = fatOff + cluster * 3 / 2;
    var cur = img[o] | (img[o + 1] << 8);
    cur = (cluster & 1) == 0
      ? (cur & 0xF000) | (value & 0xFFF)
      : (cur & 0x000F) | ((value & 0xFFF) << 4);
    img[o] = (byte)(cur & 0xFF);
    img[o + 1] = (byte)(cur >> 8);
  }

  private static void WriteShortName(byte[] img, int offset, string name) {
    Span<byte> field = stackalloc byte[11];
    field.Fill((byte)' ');
    var dot = name.LastIndexOf('.');
    var stem = dot < 0 ? name : name[..dot];
    var ext = dot < 0 ? "" : name[(dot + 1)..];
    var stemBytes = Encoding.ASCII.GetBytes(stem.ToUpperInvariant());
    var extBytes = Encoding.ASCII.GetBytes(ext.ToUpperInvariant());
    stemBytes.AsSpan(0, Math.Min(8, stemBytes.Length)).CopyTo(field);
    extBytes.AsSpan(0, Math.Min(3, extBytes.Length)).CopyTo(field[8..]);
    field.CopyTo(img.AsSpan(offset, 11));
  }
}
