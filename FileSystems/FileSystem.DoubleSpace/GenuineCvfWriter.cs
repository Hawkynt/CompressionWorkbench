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
/// Unlike <see cref="DoubleSpaceWriter"/> (which emits the older
/// <c>MSDSP*</c> / offset-36 layout our own reader round-trips), this writer
/// reproduces the real <c>MSDBL6.0</c> container:
/// </para>
/// <list type="bullet">
///   <item><b>Sector 0</b> — MDBPB: standard BPB (512 B/sector, 16 sec/cluster,
///     16 reserved, 1 FAT, 512 root entries, 128-sector FAT) plus the
///     DoubleSpace geometry substructure (inner-volume base sector @0x27, root
///     @0x29, first-data @0x2B, MDFAT index offset @0x2D, MDFAT-start-1 @0x24).</item>
///   <item><b>Sector 130</b> — MDFAT: one little-endian u32 per inner-volume
///     cluster at <c>(0x24+1)*512 + (0x2D + cluster)*4</c>; physical sector =
///     <c>(entry &amp; 0x1FFFFF) + 1</c>; stored clusters carry the run-length
///     flag bits (0xFFC0…&#160;full, 0xC000…&#160;final).</item>
///   <item><b>Sector 417 (inner base)</b> — a complete FAT12 volume (boot, FAT,
///     root directory, data) laid out contiguously; clusters are stored
///     verbatim (no compression), which the driver reads transparently.</item>
/// </list>
/// The fixed geometry matches a ~557 KB compressed volume; files must fit the
/// inner FAT12 data area (≈ 69 clusters of 8 KB).
/// </summary>
public sealed class GenuineCvfWriter {

  // Container geometry (proven against real MS-DOS 6.22 DRVSPACE images).
  private const int Ss = 512;
  private const int Spc = 16;                 // sectors per cluster (8 KB)
  private const int Resv = 16;                // reserved sectors
  private const int NumFats = 1;
  private const int FatSize = 128;            // sectors per inner FAT
  private const int RootEntries = 512;
  private const int RootLog = Resv + NumFats * FatSize;       // 144
  private const int RootSecs = RootEntries * 32 / Ss;          // 32
  private const int FirstData = RootLog + RootSecs;            // 176
  private const int InnerTotalSectors = 1290;
  private const int InnerBase = 417;
  private const int MdfatStartSec = 130;
  private const int MdfatIdxOff = 9;
  private const int ClusterBytes = Ss * Spc;                   // 8192

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

  /// <summary>Per-cluster compression codec. Stored (default) emits uncompressed
  /// clusters; DS = DoubleSpace DS-0-x, JM = DriveSpace JM-0-x.</summary>
  public Compression.Registry.Cvf.CvfLzMethod CompressionMethod { get; init; }
    = Compression.Registry.Cvf.CvfLzMethod.Stored;

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

    // 2. Compress each cluster (auto-best) and pack into physical sectors.
    var physDataStart = InnerBase + FirstData;
    var physCursor = physDataStart;
    var layout = new List<ClusterPlan>();
    foreach (var (cl, full) in clusterFull) {
      var comp = Compression.Registry.Cvf.CvfLzCodec.Encode(full, this.CompressionMethod, this.CompressionLevel);
      var ksize = comp is null ? Spc : (comp.Length + Ss - 1) / Ss;
      if (comp is not null && ksize <= Spc && (ksize < Spc || this.ForceCompress)) {
        layout.Add(new ClusterPlan(cl, comp, ksize, true, physCursor));
        physCursor += ksize;
      } else {
        layout.Add(new ClusterPlan(cl, full, Spc, false, physCursor));
        physCursor += Spc;
      }
    }

    var totalSectors = Math.Max(1152, physCursor);
    if ((totalSectors & 1) != 0) totalSectors++;
    var img = new byte[totalSectors * Ss];

    WriteMdbpb(img);
    WriteInnerBoot(img);

    var innerOff = InnerBase * Ss;
    var fatOff = innerOff + Resv * Ss;
    var rootOff = innerOff + RootLog * Ss;
    var mdfatBase = MdfatStartSec * Ss + MdfatIdxOff * 4;

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

    // BITFAT: mirror the real driver's first marked region.
    img[Ss + 1] = 0x80;
    return img;
  }

  private static void WriteMdbpb(byte[] img) {
    img[0] = 0xEB; img[1] = 0x3C; img[2] = 0x90;
    "MSDBL6.0"u8.CopyTo(img.AsSpan(3, 8));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0B), Ss);
    img[0x0D] = Spc;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0E), Resv);
    img[0x10] = NumFats;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x11), RootEntries);
    img[0x15] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x16), FatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x18), 17);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x1A), 6);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x20), InnerTotalSectors);
    // DoubleSpace geometry substructure.
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x24), MdfatStartSec - 1);
    img[0x26] = 9;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x27), InnerBase);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x29), RootLog);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x2B), FirstData);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x2D), MdfatIdxOff);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x2F), RootSecs);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x31), 1024);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x33), 2);
    "12  "u8.CopyTo(img.AsSpan(0x39, 4));
    img[0x3D] = 1; img[0x3F] = 1;
  }

  private static void WriteInnerBoot(byte[] img) {
    var b = InnerBase * Ss;
    img[b] = 0xEB; img[b + 1] = 0x3C; img[b + 2] = 0x90;
    "MSDBL6.0"u8.CopyTo(img.AsSpan(b + 3, 8));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x0B), Ss);
    img[b + 0x0D] = Spc;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x0E), Resv);
    img[b + 0x10] = NumFats;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x11), RootEntries);
    img[b + 0x15] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x16), FatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x18), 17);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x1A), 6);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(b + 0x20), InnerTotalSectors);
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
