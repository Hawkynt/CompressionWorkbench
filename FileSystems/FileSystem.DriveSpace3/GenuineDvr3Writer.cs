#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry.Cvf;

namespace FileSystem.DriveSpace3;

/// <summary>
/// Builds a <b>genuine</b> Microsoft DriveSpace 3 Compressed Volume File (CVF) —
/// the on-disk shape a real DriveSpace 3 driver mounts, verified by the
/// independent GPL <c>dmsdos</c> driver (the Linux DoubleSpace/DriveSpace/Stacker
/// driver), which detects this writer's output as "drivespace 3 CVF", mounts it,
/// lists the inner directory and reads every file back byte-exact.
/// <para>
/// Unlike <see cref="DriveSpace3Writer"/> (the older self-round-trip
/// <c>MS_DSP3</c>/offset-36 layout — which the real driver <i>rejects</i>),
/// DriveSpace 3 is in fact a member of the DOS <c>MSDBL6.0</c> CVF family: the
/// same container as <see cref="FileSystem.DoubleSpace.GenuineCvfWriter"/>, but
/// distinguished by <c>64</c> sectors per cluster (32&#160;KB clusters,
/// boot byte&#160;13), a <c>version_flag = 3</c> (boot byte&#160;51) and a
/// 5-byte-per-entry MDFAT (102 entries per sector plus 2 pad bytes) instead of
/// the 4-byte v2 entry. Clusters are stored uncompressed (MDFAT flags = 3:
/// used + uncompressed) and identity-mapped through the MDFAT; the inner volume
/// is FAT16. Inner-directory file sizes truncate each cluster's tail, so we
/// store every cluster as a full 64-sector run with the unused tail zero-padded.
/// </para>
/// </summary>
public sealed class GenuineDvr3Writer {

  private const int Ss = 512;
  private const int Spc = 64;                                   // 32 KB clusters
  private const int Resv = 16;
  private const int NumFats = 1;
  private const int FatSize = 2;                                // inner FAT16: 2 sectors
  private const int RootEntries = 512;
  private const int RootLog = Resv + NumFats * FatSize;         // 18
  private const int RootSecs = RootEntries * 32 / Ss;           // 32
  private const int FirstData = RootLog + RootSecs;             // 50
  private const int InnerBase = 417;                            // res0 / emulated boot block
  private const int MdfatStartSec = 130;
  private const int SDcluster = 0;                              // boot byte 45
  private const int ClusterBytes = Ss * Spc;                    // 32768

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Optional inner-volume label (≤11 chars). Empty = no label entry.</summary>
  public string VolumeLabel { get; init; } = "";

  /// <summary>Creation/modification timestamp stamped on every file entry.
  /// Default (before 1980) leaves the FAT date/time fields zero.</summary>
  public DateTime Timestamp { get; init; }

  /// <summary>Per-cluster compression codec. <see cref="Compression.Registry.Cvf.CvfLzMethod.Stored"/>
  /// (default) emits uncompressed clusters; DS/JM emit genuine DriveSpace 3 compressed clusters.</summary>
  public Compression.Registry.Cvf.CvfLzMethod CompressionMethod { get; init; }
    = Compression.Registry.Cvf.CvfLzMethod.Stored;

  /// <summary>Codec effort (search depth). Higher = better ratio, slower.</summary>
  public int CompressionLevel { get; init; } = 1;

  /// <summary>When true, keep a compressed cluster even if it does not shrink
  /// (as long as it still fits the cluster's sector budget); otherwise the
  /// smaller of compressed/stored is chosen per cluster (auto-best).</summary>
  public bool ForceCompress { get; init; }

  /// <summary>Adds a file to the root directory of the compressed volume.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var leaf = Path.GetFileName(name.Replace('\\', '/').TrimEnd('/'));
    if (string.IsNullOrEmpty(leaf)) throw new ArgumentException("File name must not be empty.", nameof(name));
    this._files.Add((leaf, data));
  }

  // Byte offset of a cluster's 5-byte MDFAT entry, per the driver's DRVSP3
  // packing: 102 entries per 512-byte sector, then a 2-byte gap.
  private static int MdfatBytePos(int cluster) =>
    (SDcluster + cluster) * 5 + ((SDcluster + cluster) / 102) * 2 + Ss * MdfatStartSec;

  // One planned cluster: its number, the full (zero-padded) logical bytes, the
  // payload actually written to disk, its sector count, and whether compressed.
  private readonly record struct ClusterPlan(
    int Cluster, byte[] Payload, int Sectors, bool Compressed, int PhysSector);

  /// <summary>Builds the CVF image bytes.</summary>
  public byte[] Build() {
    // 1. Plan files → clusters; capture each file's first cluster + size.
    var files = new List<(string Name, int First, int Count, long Size)>();
    var clusterFull = new List<(int Cluster, byte[] Full)>();
    var next = 2;
    foreach (var (name, data) in this._files) {
      var count = Math.Max(1, (data.Length + ClusterBytes - 1) / ClusterBytes);
      files.Add((name, next, count, data.Length));
      for (var i = 0; i < count; i++) {
        var full = new byte[ClusterBytes];
        var off = i * ClusterBytes;
        var copy = Math.Min(ClusterBytes, data.Length - off);
        if (copy > 0) Array.Copy(data, off, full, 0, copy);
        clusterFull.Add((next + i, full));
      }
      next += count;
    }

    // 2. Compress each cluster (auto-best), assigning packed physical sectors.
    var physDataStart = InnerBase + FirstData;
    var physCursor = physDataStart;
    var layout = new List<ClusterPlan>();
    foreach (var (cl, full) in clusterFull) {
      var comp = CvfLzCodec.Encode(full, this.CompressionMethod, this.CompressionLevel);
      var ksize = comp is null ? Spc : (comp.Length + Ss - 1) / Ss;
      if (comp is not null && ksize <= Spc && (ksize < Spc || this.ForceCompress)) {
        layout.Add(new ClusterPlan(cl, comp, ksize, true, physCursor));
        physCursor += ksize;
      } else {
        layout.Add(new ClusterPlan(cl, full, Spc, false, physCursor));
        physCursor += Spc;
      }
    }

    var totalSectors = physCursor + 2;
    if ((totalSectors & 1) != 0) totalSectors++;
    var img = new byte[totalSectors * Ss];

    WriteMdbpb(img, totalSectors);
    WriteInnerBoot(img);

    var innerOff = InnerBase * Ss;
    var fatOff = innerOff + Resv * Ss;
    var rootOff = innerOff + RootLog * Ss;

    // FAT16 reserved entries (clusters 0 and 1).
    img[fatOff] = 0xF8; img[fatOff + 1] = 0xFF; img[fatOff + 2] = 0xFF; img[fatOff + 3] = 0xFF;

    // 3. Root directory (optional volume label first) + FAT16 chains.
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
        BinaryPrimitives.WriteUInt16LittleEndian(
          img.AsSpan(fatOff + cluster * 2), (ushort)(isLast ? 0xFFFF : cluster + 1));
      }
    }

    // 4. Cluster payloads + 5-byte MDFAT entries (flags 3 = stored, 2 = compressed).
    foreach (var plan in layout) {
      Array.Copy(plan.Payload, 0, img, plan.PhysSector * Ss, plan.Payload.Length);
      var p = MdfatBytePos(plan.Cluster);
      var sm1 = plan.PhysSector - 1;
      img[p + 0] = (byte)sm1;
      img[p + 1] = (byte)(sm1 >> 8);
      img[p + 2] = (byte)(sm1 >> 16);
      img[p + 3] = (byte)(((plan.Sectors - 1) & 0x3F) << 2);          // unknown=0, size_lo
      img[p + 4] = (byte)(((Spc - 1) & 0x3F) | ((plan.Compressed ? 2 : 3) << 6)); // size_hi, flags
    }

    // MDR end-of-CVF signature at the final sector.
    "MDR"u8.CopyTo(img.AsSpan((totalSectors - 1) * Ss, 3));
    return img;
  }

  private static void WriteMdbpb(byte[] img, int totalSectors) {
    img[0] = 0xEB; img[1] = 0x58; img[2] = 0x90;
    "MSDBL6.0"u8.CopyTo(img.AsSpan(3, 8));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0B), Ss);
    img[0x0D] = Spc;                                       // 64 sectors/cluster
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0E), Resv);
    img[0x10] = NumFats;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x11), RootEntries);
    img[0x15] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x16), FatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x18), 17);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x1A), 6);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x20), (uint)totalSectors);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x24), MdfatStartSec - 1);
    img[0x26] = 9;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x27), InnerBase);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x29), RootLog);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x2B), FirstData);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x2D), SDcluster);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x2F), RootSecs);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x31), 1024);
    img[0x33] = 3;                                         // version_flag = 3 (DriveSpace 3)
    "16  "u8.CopyTo(img.AsSpan(0x39, 4));
    img[0x3D] = 1; img[0x3F] = 1;
  }

  private static void WriteInnerBoot(byte[] img) {
    var b = InnerBase * Ss;
    img[b] = 0xEB; img[b + 1] = 0x58; img[b + 2] = 0x90;
    "MSDBL6.0"u8.CopyTo(img.AsSpan(b + 3, 8));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x0B), Ss);
    img[b + 0x0D] = Spc;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x0E), Resv);
    img[b + 0x10] = NumFats;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x11), RootEntries);
    img[b + 0x15] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x16), FatSize);
    img[b + 0x26] = 0x29;                                  // extended boot signature
    "FAT16   "u8.CopyTo(img.AsSpan(b + 0x36, 8));          // off57 -> "16  " => 16-bit FAT
    img[b + 0x1FE] = 0x55; img[b + 0x1FF] = 0xAA;
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
