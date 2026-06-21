#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Stacker;

/// <summary>
/// Builds a <b>genuine</b> Stac Electronics STACVOL — the on-disk shape a real
/// Stacker driver mounts, verified by the independent GPL <c>dmsdos</c> driver,
/// which detects this writer's output as "stacker version 3 CVF", mounts it,
/// lists the inner directory and reads every file back byte-exact.
/// <para>
/// Unlike <see cref="StackerWriter"/> (the older self-round-trip layout with the
/// invented <c>STKMAP01</c> trailer — which the real driver <i>rejects</i>), the
/// genuine STACVOL is shaped as:
/// </para>
/// <list type="bullet">
///   <item><b>Sector 0 — SCB:</b> the <c>"STACKER"</c> magic, a minimal FAT BPB
///     (so the generic FAT layer parses sector 0 without crashing), the raw
///     <c>0x1A0A</c> signature at 0x4e/0x4f, and the <b>obfuscated superblock</b>
///     at 0x50 (0x30 bytes enciphered with the Stacker rolling-XOR cipher seeded
///     at 0x4c). The decoded superblock carries the version (0x60), sector size
///     (0x62), total sectors (0x6C), emulated-boot-block sector (0x70), AMAP
///     start (0x74), FAT start (0x76) and data start (0x7a).</item>
///   <item><b>Emulated boot block</b> (at the 0x70 sector) — a standard BPB
///     describing the inner FAT volume.</item>
///   <item><b>Interleaved FAT + AMAP</b> from the FAT-start sector: the AMAP
///     (Stacker's MDFAT) sector for a cluster is
///     <c>(area/6)*9 + area%6 + 3 + fatStart</c> where <c>area = cluster*3/512</c>.
///     Each 3-byte entry stores the absolute physical sector and a stored-cluster
///     flag (uncompressed); clusters are identity-mapped and read verbatim.</item>
/// </list>
/// </summary>
public sealed class GenuineStackerWriter {

  private const int Ss = 512;
  private const int Spc = 16;                                   // 8 KB clusters
  private const int FatStart = 2;
  private const int FatCnt = 1;
  private const int RootEntries = 512;
  private const int RootSecs = RootEntries * 32 / Ss;           // 32
  private const int Reserv = 1;
  private const int BootBlock = 1;
  private const int ClusterBytes = Ss * Spc;

  // Geometry sized to the cluster count (FAT band big enough that the root sits
  // past the interleaved AMAP). The inner FAT is read sequentially from FatStart,
  // so it must fit in the 3 sectors before AMAP area 0 — bounding a genuine
  // sequential-layout STACVOL at ~1023 clusters (8 MB); larger needs the
  // interleaved-FAT layout.
  private int _fatSize;
  private int _firstRoot;
  private int _realFirstData;
  private const int AmapStart = FatStart + 3;                    // 5 (informational)

  /// <summary>Stacker major version recorded in the decoded superblock (&lt; 410 ⇒ v3).</summary>
  public int Version { get; init; } = 3;

  /// <summary>Optional inner-volume label (≤11 chars). Empty = no label entry.</summary>
  public string VolumeLabel { get; init; } = "";

  /// <summary>Creation/modification timestamp stamped on every file entry.
  /// Default (before 1980) leaves the FAT date/time fields zero.</summary>
  public DateTime Timestamp { get; init; }

  /// <summary>Per-cluster compression. Stored (default) or DS — the dmsdos
  /// Stacker reader dispatches a 0x5344 ("DS") cluster header to the DS decoder,
  /// so DS-compressed Stacker clusters are read by the real driver. (JM/Auto map
  /// to DS here, as the Stacker path only recognises DS among the LZ headers.)</summary>
  public Compression.Registry.Cvf.CvfLzMethod CompressionMethod { get; init; }
    = Compression.Registry.Cvf.CvfLzMethod.Stored;

  /// <summary>Codec effort (search depth). Higher = better ratio, slower.</summary>
  public int CompressionLevel { get; init; } = 1;

  /// <summary>Keep a compressed cluster even if it does not shrink (auto-best off).</summary>
  public bool ForceCompress { get; init; }

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the root directory of the compressed volume.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var leaf = Path.GetFileName(name.Replace('\\', '/').TrimEnd('/'));
    if (string.IsNullOrEmpty(leaf)) throw new ArgumentException("File name must not be empty.", nameof(name));
    this._files.Add((leaf, data));
  }

  private static int Rol1(int x) => ((x << 1) | (x >> 7)) & 0xff;

  private static byte[]? Smaller(byte[]? a, byte[]? b) =>
    a is null ? b : b is null ? a : a.Length <= b.Length ? a : b;

  private readonly record struct ClusterPlan(
    int Cluster, byte[] Payload, int Sectors, int PhysSector);

  /// <summary>Builds the STACVOL image bytes.</summary>
  public byte[] Build() {
    // 1. Plan files → clusters; capture each cluster's full (zero-padded) bytes.
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
    // Size the FAT band: the inner FAT12 is read sequentially from FatStart and
    // must fit in the 3 sectors before AMAP area 0; the root must sit past the
    // last AMAP sector for the cluster count.
    var maxCluster = next - 1;
    var fatLen = ((maxCluster + 1) * 3 + 2 * Ss - 1) / (2 * Ss);   // FAT12 sectors
    if (fatLen > 3)
      throw new InvalidOperationException(
        "GenuineStackerWriter: volume exceeds the genuine sequential-FAT capacity (~1023 clusters / 8 MB); " +
        "larger STACVOLs require the interleaved-FAT layout.");
    var maxArea = maxCluster * 3 / Ss;
    var maxAmapSec = maxArea / 6 * 9 + maxArea % 6 + 3 + FatStart;
    this._fatSize = Math.Max(fatLen, (maxAmapSec - FatStart) / 3 + 1);
    this._firstRoot = FatStart + 3 * FatCnt * this._fatSize;
    this._realFirstData = this._firstRoot + RootSecs;

    // 2. Compress each cluster (auto-best) and pack into physical sectors. The
    // Stacker reader recognises only DS (0x5344) and SD-4 (0x0081); Auto keeps
    // the smaller of the two per cluster, JM/SQ fall back to DS.
    var physCursor = this._realFirstData;
    var layout = new List<ClusterPlan>();
    foreach (var (cl, full) in clusterFull) {
      var comp = this.CompressionMethod switch {
        Compression.Registry.Cvf.CvfLzMethod.Stored => null,
        Compression.Registry.Cvf.CvfLzMethod.Sd4 => Compression.Registry.Cvf.CvfLzCodec.Encode(full, Compression.Registry.Cvf.CvfLzMethod.Sd4, this.CompressionLevel),
        Compression.Registry.Cvf.CvfLzMethod.Auto => Smaller(
          Compression.Registry.Cvf.CvfLzCodec.Encode(full, Compression.Registry.Cvf.CvfLzMethod.Ds, this.CompressionLevel),
          Compression.Registry.Cvf.CvfLzCodec.Encode(full, Compression.Registry.Cvf.CvfLzMethod.Sd4, this.CompressionLevel)),
        _ => Compression.Registry.Cvf.CvfLzCodec.Encode(full, Compression.Registry.Cvf.CvfLzMethod.Ds, this.CompressionLevel),
      };
      var ksize = comp is null ? Spc : (comp.Length + Ss - 1) / Ss;
      if (comp is not null && ksize < Spc || (comp is not null && this.ForceCompress && ksize <= Spc)) {
        layout.Add(new ClusterPlan(cl, comp!, ksize, physCursor));
        physCursor += ksize;
      } else {
        layout.Add(new ClusterPlan(cl, full, Spc, physCursor));
        physCursor += Spc;
      }
    }

    var totalSects = Math.Max(200, physCursor + Spc);
    if ((totalSects & 1) != 0) totalSects++;
    var img = new byte[totalSects * Ss];

    // BB_ClustCnt = (declared - bbFirstData)/spc must stay above the real cluster
    // count even when compression shrinks the physical image (the field only feeds
    // that calc; reads use the AMAP's absolute sectors).
    var bbFirstData = RootSecs + FatCnt * this._fatSize + Reserv;
    var declaredTotal = Math.Max(totalSects, (maxCluster + 2) * Spc + bbFirstData);

    this.WriteScb(img, declaredTotal);
    WriteBootBlock(img, declaredTotal, this._fatSize);

    var fatOff = FatStart * Ss;
    img[fatOff] = 0xF8; img[fatOff + 1] = 0xFF; img[fatOff + 2] = 0xFF;

    var rootOff = this._firstRoot * Ss;
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

    // 3. Cluster payloads + STAC AMAP 3-byte entries. Stored cluster: size_lo =
    // spc-1 (flags |= stored). Compressed: size_lo = ksize-1 < spc-1 (flags = used,
    // not stored), payload starts with the DS "DS" header the reader dispatches.
    foreach (var plan in layout) {
      Array.Copy(plan.Payload, 0, img, plan.PhysSector * Ss, plan.Payload.Length);
      var pos = plan.Cluster * 3;
      var area = pos / Ss;
      var amapSector = (area / 6) * 9 + (area % 6) + 3 + FatStart;
      var p = amapSector * Ss + (pos % Ss);
      img[p + 0] = (byte)plan.PhysSector;
      img[p + 1] = (byte)(plan.PhysSector >> 8);
      img[p + 2] = (byte)((plan.Sectors - 1) & 0x0f);
    }

    return img;
  }

  private void WriteScb(byte[] img, int totalSects) {
    "STACKER"u8.CopyTo(img.AsSpan(0));

    // Minimal FAT BPB so the generic FAT layer parses sector 0 safely.
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0B), Ss);
    img[0x0D] = Spc;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0E), Reserv);
    img[0x10] = FatCnt;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x11), RootEntries);
    if (totalSects < 65536)
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x13), (ushort)totalSects);
    img[0x15] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x16), (ushort)this._fatSize);

    img[0x4C] = 0x00;                 // cipher seed
    img[0x4E] = 0x0A; img[0x4F] = 0x1A;

    var plain = new byte[0x30];
    void P16(int decodedOff, int v) {
      var idx = decodedOff - 0x50;
      plain[idx] = (byte)v; plain[idx + 1] = (byte)(v >> 8);
    }
    void P32(int decodedOff, int v) {
      var idx = decodedOff - 0x50;
      plain[idx] = (byte)v; plain[idx + 1] = (byte)(v >> 8);
      plain[idx + 2] = (byte)(v >> 16); plain[idx + 3] = (byte)(v >> 24);
    }
    P16(0x60, this.Version < 410 ? 300 : 410);
    P16(0x62, Ss);
    P32(0x6C, totalSects);
    P16(0x70, BootBlock);
    P16(0x74, AmapStart);
    P16(0x76, FatStart);
    P16(0x7A, this._realFirstData);

    var key = (int)img[0x4C];
    for (var i = 0; i < 0x30; i++) {
      var tk = Rol1((0xc4 - key) & 0xff);
      var enc = tk ^ plain[i];
      img[0x50 + i] = (byte)enc;
      key = enc;
    }
  }

  private static void WriteBootBlock(byte[] img, int totalSects, int fatSize) {
    var b = BootBlock * Ss;
    img[b] = 0xEB; img[b + 1] = 0x3C; img[b + 2] = 0x90;
    "STACKER "u8.CopyTo(img.AsSpan(b + 3, 8));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x0B), Ss);
    img[b + 0x0D] = Spc;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x0E), Reserv);
    img[b + 0x10] = FatCnt;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x11), RootEntries);
    if (totalSects < 65536)
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x13), (ushort)totalSects);
    else
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(b + 0x20), (uint)totalSects);
    img[b + 0x15] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x16), (ushort)fatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x18), 17);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x1A), 6);
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
