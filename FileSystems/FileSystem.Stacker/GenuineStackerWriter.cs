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
  private const int FatSize = 2;
  private const int RootEntries = 512;
  private const int RootSecs = RootEntries * 32 / Ss;           // 32
  private const int FirstRoot = FatStart + 3 * FatCnt * FatSize; // 8
  private const int RealFirstData = FirstRoot + RootSecs;        // 40
  private const int Reserv = 1;
  private const int BootBlock = 1;
  private const int AmapStart = FatStart + 3;                    // 5
  private const int ClusterBytes = Ss * Spc;

  /// <summary>Stacker major version recorded in the decoded superblock (&lt; 410 ⇒ v3).</summary>
  public int Version { get; init; } = 3;

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

  /// <summary>Builds the STACVOL image bytes.</summary>
  public byte[] Build() {
    var plans = new List<(string Name, byte[] Data, int First, int Count)>();
    var next = 2;
    foreach (var (name, data) in this._files) {
      var clusters = Math.Max(1, (data.Length + ClusterBytes - 1) / ClusterBytes);
      plans.Add((name, data, next, clusters));
      next += clusters;
    }
    var totalDataClusters = next - 2;
    // Each AMAP "area" (512-byte sector) holds entries for 170 clusters; only
    // areas 0..2 sit in the reserved FAT band before the root directory.
    if (next > 512)
      throw new InvalidOperationException("GenuineStackerWriter: too many clusters for the fixed AMAP band.");

    var dataEnd = RealFirstData + totalDataClusters * Spc;
    var totalSects = Math.Max(200, dataEnd + Spc);
    if ((totalSects & 1) != 0) totalSects++;
    var img = new byte[totalSects * Ss];

    this.WriteScb(img, totalSects);
    WriteBootBlock(img, totalSects);

    var fatOff = FatStart * Ss;
    img[fatOff] = 0xF8; img[fatOff + 1] = 0xFF; img[fatOff + 2] = 0xFF;

    var rootOff = FirstRoot * Ss;
    var dirIndex = 0;
    foreach (var (name, data, first, count) in plans) {
      var de = rootOff + dirIndex * 32;
      WriteShortName(img, de, name);
      img[de + 11] = 0x20;
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(de + 26), (ushort)first);
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(de + 28), (uint)data.Length);
      dirIndex++;

      var written = 0;
      for (var i = 0; i < count; i++) {
        var cluster = first + i;
        var isLast = i == count - 1;
        WriteFat12(img, fatOff, cluster, isLast ? 0xFFF : cluster + 1);

        var physSector = RealFirstData + (cluster - 2) * Spc;
        var copy = Math.Min(ClusterBytes, data.Length - written);
        if (copy > 0) Array.Copy(data, written, img, physSector * Ss, copy);
        written += copy;

        // STAC AMAP 3-byte entry (FAT12): absolute physical sector + stored flag.
        var pos = cluster * 3;
        var area = pos / Ss;
        var amapSector = (area / 6) * 9 + (area % 6) + 3 + FatStart;
        var p = amapSector * Ss + (pos % Ss);
        img[p + 0] = (byte)physSector;
        img[p + 1] = (byte)(physSector >> 8);
        img[p + 2] = (byte)((Spc - 1) & 0x0f);   // size_lo=15, flags nibble 0 ⇒ stored
      }
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
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x16), FatSize);

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
    P16(0x7A, RealFirstData);

    var key = (int)img[0x4C];
    for (var i = 0; i < 0x30; i++) {
      var tk = Rol1((0xc4 - key) & 0xff);
      var enc = tk ^ plain[i];
      img[0x50 + i] = (byte)enc;
      key = enc;
    }
  }

  private static void WriteBootBlock(byte[] img, int totalSects) {
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
    img[b + 0x15] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(b + 0x16), FatSize);
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
