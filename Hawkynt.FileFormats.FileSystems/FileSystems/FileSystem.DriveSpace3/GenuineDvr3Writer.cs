#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.FileSystems.Cvf;

namespace FileSystem.DriveSpace3;

/// <summary>
/// Builds a <b>genuine</b> Microsoft DriveSpace 3 Compressed Volume File (CVF) in the layout Windows 98's
/// DriveSpace 3 creates — the real driver and the independent <c>dmsdos</c> driver both mount it and
/// read every file back byte-exact.
/// <para>
/// DriveSpace 3 is a member of the DOS <c>MSDBL6.0</c> CVF family: the same container as
/// <see cref="FileSystem.DoubleSpace.GenuineCvfWriter"/>, but with <c>64</c> sectors per cluster
/// (32&#160;KB clusters), an MDBPB extended to 0x47 (jump <c>EB 6E</c>), a 5-byte-per-entry MDFAT (102
/// entries per sector plus 2 pad bytes) and an inner FAT16 volume sized for the 2 GB maximum. The system
/// area (BitFAT, MDFAT, inner boot sector, FAT, root directory) has the fixed geometry a created volume
/// carries whatever its size; see <c>FileSystem.DriveSpace3/FORMAT-NOTES.md</c>.
/// </para>
/// </summary>
public sealed class GenuineDvr3Writer {

  private const int Ss = 512;
  private const int Spc = 64;                                   // 32 KB clusters
  private const int NumFats = 1;
  private const int RootEntries = 512;
  private const int RootSecs = RootEntries * 32 / Ss;           // 32
  private const int ClusterBytes = Ss * Spc;                    // 32768

  // The system area of a volume Windows 98's DriveSpace 3 creates, the same whatever the volume's size:
  // BitFAT in sectors 1..1021, MDFAT from 1022 (FAT entries / 102, plus 31 sectors), the inner volume
  // from 1693 with 33 reserved sectors (so the data area starts on a cluster boundary), a 255-sector FAT16
  // and the 512-entry root directory; the inner volume declares the 2 GB maximum.
  private const int Resv = 33;
  private const int FatSectors = 255;
  private const int MdfatStartSec = 1022;
  private const int InnerBase = 1693;
  private const int RootLog = Resv + NumFats * FatSectors;      // 288
  private const int FirstData = RootLog + RootSecs;             // 320
  private const int SDcluster = FirstData / Spc - 2;            // 3, MDBPB 0x2D
  private const uint InnerSectors = 0x3FC0C0;                   // MDBPB 0x20: 4 178 112
  private const int MinFileSectors = 2144;                      // a 1 MB volume, the smallest created

  // FAT16 cluster numbers stop short of the reserved values; the inner volume declares at most this many.
  private const int MaxClusters = (int)((InnerSectors - FirstData) / Spc);

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Optional inner-volume label (≤11 chars). Empty = no label entry.</summary>
  public string VolumeLabel { get; init; } = "";

  /// <summary>Creation/modification timestamp stamped on every file entry.
  /// Default (before 1980) leaves the FAT date/time fields zero.</summary>
  public DateTime Timestamp { get; init; }

  /// <summary>Per-cluster compression codec: <see cref="Compression.Core.FileSystems.Cvf.CvfLzMethod.Stored"/>
  /// (default), JM (JM-0-0, DriveSpace 3 <i>Normal</i>), SQ (SQ-0-0, <i>Ultra</i>) or Auto (per cluster the
  /// smaller of JM and SQ) — the codecs the real DriveSpace 3 driver writes. Anything else is refused by
  /// <see cref="Build"/>.</summary>
  public Compression.Core.FileSystems.Cvf.CvfLzMethod CompressionMethod { get; init; }
    = Compression.Core.FileSystems.Cvf.CvfLzMethod.Stored;

  /// <summary>Codec effort (search depth). Higher = better ratio, slower.</summary>
  public int CompressionLevel { get; init; } = 1;

  /// <summary>When true, keep a compressed cluster even if it does not shrink
  /// (as long as it still fits the cluster's sector budget); otherwise the
  /// smaller of compressed/stored is chosen per cluster (auto-best).</summary>
  public bool ForceCompress { get; init; }

  private static byte[] Smaller(byte[] a, byte[] b) => b.Length < a.Length ? b : a;

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

    var maxCluster = next - 1;                                   // highest cluster used (>=1)
    if (maxCluster > MaxClusters)
      throw new NotSupportedException($"The files need {maxCluster - 1} clusters; a DriveSpace 3 volume holds at most {MaxClusters - 1}.");
    if (this._files.Count + (string.IsNullOrEmpty(this.VolumeLabel) ? 0 : 1) > RootEntries)
      throw new NotSupportedException($"The root directory holds {RootEntries} entries; {this._files.Count} files were given.");

    // 2. Compress each cluster, assigning packed physical sectors. Only DriveSpace 3's own codecs
    // are written: JM and SQ (DS belongs to DoubleSpace volumes).
    if (this.CompressionMethod is not (CvfLzMethod.Stored or CvfLzMethod.Jm or CvfLzMethod.Sq or CvfLzMethod.Auto))
      throw new NotSupportedException($"DriveSpace 3 writes stored, JM or SQ clusters; {this.CompressionMethod} is not a DriveSpace 3 codec.");
    // Cluster runs start two sectors past the inner system area, where the BitFAT's first bit points.
    var physDataStart = InnerBase + FirstData + 2;
    var physCursor = physDataStart;
    var layout = new List<ClusterPlan>();
    foreach (var (cl, full) in clusterFull) {
      var comp = this.CompressionMethod == CvfLzMethod.Auto
        ? Smaller(CvfLzCodec.Encode(full, CvfLzMethod.Jm, this.CompressionLevel)!, CvfLzCodec.Encode(full, CvfLzMethod.Sq, this.CompressionLevel)!)
        : CvfLzCodec.Encode(full, this.CompressionMethod, this.CompressionLevel);
      var ksize = comp is null ? Spc : (comp.Length + Ss - 1) / Ss;
      if (comp is not null && ksize <= Spc && (ksize < Spc || this.ForceCompress)) {
        layout.Add(new ClusterPlan(cl, comp, ksize, true, physCursor));
        physCursor += ksize;
      } else {
        layout.Add(new ClusterPlan(cl, full, Spc, false, physCursor));
        physCursor += Spc;
      }
    }

    var totalSectors = Math.Max(MinFileSectors, physCursor + 2);
    if ((totalSectors & 1) != 0) totalSectors++;
    var img = new byte[totalSectors * Ss];

    WriteMdbpb(img);
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

    // BitFAT (from sector 1): one bit per physical sector from physDataStart, set for every sector a
    // cluster run occupies, MSB-first through little-endian 16-bit words.
    foreach (var plan in layout)
      for (var sector = plan.PhysSector; sector < plan.PhysSector + plan.Sectors; ++sector) {
        var bit = sector - physDataStart;
        var word = Ss + bit / 16 * 2;
        var mask = 0x8000 >> (bit % 16);
        img[word] |= (byte)mask;
        img[word + 1] |= (byte)(mask >> 8);
      }

    // Signatures: "MDR\0" opens the last sector, F8 08 22 00 the inner volume's second sector.
    "MDR\0"u8.CopyTo(img.AsSpan((totalSectors - 1) * Ss));
    InnerSecondSector.CopyTo(img.AsSpan((InnerBase + 1) * Ss));
    return img;
  }

  // MDBPB bytes 0x00..0x47 of a created volume, total sectors and geometry included; nothing in them
  // follows the volume's content.
  private static ReadOnlySpan<byte> Mdbpb => [
    0xEB, 0x6E, 0x90, (byte)'M', (byte)'S', (byte)'D', (byte)'B', (byte)'L', (byte)'6', (byte)'.', (byte)'0', 0x00, 0x02, 0x40, 0x21, 0x00,
    0x01, 0x00, 0x02, 0x00, 0x00, 0xF8, 0xFF, 0x00, 0x11, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00,
    0xC0, 0xC0, 0x3F, 0x00, 0xFD, 0x03, 0x09, 0x9D, 0x06, 0x20, 0x01, 0x40, 0x01, 0x03, 0x00, 0xFF,
    0x00, 0x00, 0x06, 0x00, 0x01, 0x00, 0xBA, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF8, 0x07,
    0x14, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00,
  ];

  // The inner volume's second sector starts with these bytes on a created volume.
  private static ReadOnlySpan<byte> InnerSecondSector => [0xF8, 0x08, 0x22, 0x00];

  private static void WriteMdbpb(byte[] img) => Mdbpb.CopyTo(img);

  // What DOS sees: the MDBPB's geometry with two FATs (the driver serves the one stored FAT for both), so
  // the total is the MDBPB's plus one FAT.
  private static void WriteInnerBoot(byte[] img) {
    var b = InnerBase * Ss;
    Mdbpb[..0x20].CopyTo(img.AsSpan(b));
    img[b + 0x10] = 2;
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(b + 0x20), InnerSectors + FatSectors);
    img[b + 0x26] = 0x29;                                  // extended boot signature
    "COMPRESSED "u8.CopyTo(img.AsSpan(b + 0x2B, 11));
    "FAT16   "u8.CopyTo(img.AsSpan(b + 0x36, 8));
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
