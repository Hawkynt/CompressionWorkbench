#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Human68k;

/// <summary>
/// In-place modifier for Sharp X68000 Human68k (FAT12) disk images. Performs
/// add / remove with strict <b>O(touched bytes)</b> I/O — only the FAT
/// sector(s) covering the touched cluster chain, the affected root-directory
/// entry, and the file's data clusters are read or written. Existing files'
/// data bytes stay byte-identical at their original cluster offsets, and a
/// same-size update never changes the image length.
///
/// <para>The companion <see cref="Human68kReader"/> surfaces only the first
/// contiguous cluster-aligned run of a file, so this modifier allocates
/// <b>contiguous</b> cluster runs (and chains them in the FAT) — the data is
/// therefore both reader-faithful and FAT-correct.</para>
///
/// <para>Returns <c>false</c> from <see cref="TryAddFile"/> when the disk has
/// no contiguous free run / no free directory slot, so the caller can fall
/// back to a growing rebuild for those genuinely-unsupported cases.</para>
/// </summary>
public static class Human68kModifier {

  /// <summary>FAT12 BPB geometry parsed from the boot sector.</summary>
  private readonly record struct Bpb(
      int BytesPerSector, int SectorsPerCluster, int ReservedSectors,
      int FatCount, int RootEntries, int SectorsPerFat, int TotalSectors) {
    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;
    public int FatStart => ReservedSectors * BytesPerSector;
    public int RootDirSectors => (RootEntries * 32 + BytesPerSector - 1) / BytesPerSector;
    public int RootDirOffset => (ReservedSectors + FatCount * SectorsPerFat) * BytesPerSector;
    public int DataStartSector => ReservedSectors + FatCount * SectorsPerFat + RootDirSectors;
    public int TotalClusters => Math.Max(0, (TotalSectors - DataStartSector) / SectorsPerCluster);
    public int ClusterOffset(int cluster) => (DataStartSector + (cluster - 2) * SectorsPerCluster) * BytesPerSector;
  }

  /// <summary>True if the stream parses as a Human68k FAT12 volume.</summary>
  public static bool IsHuman68k(Stream image) => TryReadBpb(image, out _);

  private static bool TryReadBpb(Stream image, out Bpb bpb) {
    bpb = default;
    if (image.Length < 512) return false;
    var boot = new byte[512];
    image.Position = 0;
    image.ReadExactly(boot);
    var hasTag = boot[0x10] == (byte)'X' && boot[0x11] == (byte)'6' && boot[0x12] == (byte)'8' && boot[0x13] == (byte)'K';
    if (!hasTag && boot[0] != 0x60) return false;

    var bps = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B, 2));
    if (bps is < 256 or > 4096) bps = 512;
    var spc = Math.Max(1, (int)boot[0x0D]);
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0E, 2));
    var fats = boot[0x14];
    var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x15, 2));
    var sectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x1A, 2));
    if (fats is < 1 or > 4) return false;
    if (rootEntries is < 16 or > 1024) return false;
    if (reserved < 1 || sectorsPerFat < 1) return false;
    var totalSectors = (int)(image.Length / bps);
    bpb = new Bpb(bps, spc, reserved, fats, rootEntries, sectorsPerFat, totalSectors);
    return true;
  }

  /// <summary>
  /// Attempts a genuine in-place add. Returns false (image untouched) when
  /// there is no free directory slot or no contiguous free cluster run.
  /// </summary>
  public static bool TryAddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!TryReadBpb(image, out var bpb)) return false;

    var fat = ReadFat(image, bpb);
    var root = ReadRoot(image, bpb);

    var slot = FindFreeDirSlot(root);
    if (slot < 0) return false;

    var clustersNeeded = Math.Max(1, (data.Length + bpb.BytesPerCluster - 1) / bpb.BytesPerCluster);
    var firstCluster = FindContiguousFreeClusters(fat, bpb, clustersNeeded);
    if (firstCluster < 0) return false;

    // Write the data into the contiguous cluster run.
    for (var k = 0; k < clustersNeeded; k++) {
      var cluster = firstCluster + k;
      var off = bpb.ClusterOffset(cluster);
      var bytesInto = k * bpb.BytesPerCluster;
      var toCopy = Math.Min(data.Length - bytesInto, bpb.BytesPerCluster);
      var buf = new byte[bpb.BytesPerCluster];
      if (toCopy > 0) Array.Copy(data, bytesInto, buf, 0, toCopy);
      image.Position = off;
      image.Write(buf, 0, buf.Length);
      // Chain the FAT entry.
      SetFat(fat, cluster, k == clustersNeeded - 1 ? 0xFFF : cluster + 1);
    }

    // Fill the directory entry.
    var (nameField, extField) = SplitShortName(name);
    var recOff = slot * 32;
    Array.Clear(root, recOff, 32);
    Array.Copy(nameField, 0, root, recOff, 8);
    Array.Copy(extField, 0, root, recOff + 8, 3);
    root[recOff + 0x0B] = 0x20; // archive
    BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(recOff + 0x1A, 2), (ushort)firstCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(recOff + 0x1C, 4), (uint)data.Length);

    WriteFat(image, bpb, fat);
    WriteRoot(image, bpb, root);
    return true;
  }

  /// <summary>
  /// Removes the named file in place: frees its FAT chain, optionally wipes
  /// the cluster data, and marks the directory entry deleted (0xE5).
  /// Returns true if found.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    if (!TryReadBpb(image, out var bpb)) return false;

    var fat = ReadFat(image, bpb);
    var root = ReadRoot(image, bpb);
    var needle = NormalizeName(name);

    for (var i = 0; i < bpb.RootEntries; i++) {
      var recOff = i * 32;
      var first = root[recOff];
      if (first == 0x00) break;
      if (first == 0xE5) continue;
      var attr = root[recOff + 0x0B];
      if (attr == 0x0F || (attr & 0x08) != 0) continue;
      var entryName = JoinName(ReadName(root.AsSpan(recOff, 8)), ReadName(root.AsSpan(recOff + 8, 3)));
      if (!string.Equals(entryName, needle, StringComparison.OrdinalIgnoreCase)) continue;

      int firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(recOff + 0x1A, 2));

      // Walk + free the FAT chain (optionally wiping each cluster).
      var cluster = firstCluster;
      var guard = 0;
      while (cluster >= 2 && cluster < 0xFF0 && guard++ < bpb.TotalClusters + 4) {
        var next = GetFat(fat, cluster);
        if (wipeData) {
          var off = bpb.ClusterOffset(cluster);
          if (off >= 0 && off + bpb.BytesPerCluster <= image.Length) {
            image.Position = off;
            image.Write(new byte[bpb.BytesPerCluster], 0, bpb.BytesPerCluster);
          }
        }
        SetFat(fat, cluster, 0);
        if (next < 2 || next >= 0xFF8) break;
        cluster = next;
      }

      root[recOff] = 0xE5;
      WriteFat(image, bpb, fat);
      WriteRoot(image, bpb, root);
      return true;
    }
    return false;
  }

  // ── FAT I/O ─────────────────────────────────────────────────────────

  private static byte[] ReadFat(Stream image, Bpb bpb) {
    var len = bpb.SectorsPerFat * bpb.BytesPerSector;
    var buf = new byte[len];
    image.Position = bpb.FatStart;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteFat(Stream image, Bpb bpb, byte[] fat) {
    // Write all FAT copies.
    for (var f = 0; f < bpb.FatCount; f++) {
      image.Position = (bpb.ReservedSectors + f * bpb.SectorsPerFat) * (long)bpb.BytesPerSector;
      image.Write(fat, 0, fat.Length);
    }
  }

  private static int GetFat(byte[] fat, int cluster) {
    var off = cluster * 3 / 2;
    if (off + 1 >= fat.Length) return 0xFFF;
    var pair = fat[off] | (fat[off + 1] << 8);
    return (cluster & 1) == 0 ? pair & 0x0FFF : (pair >> 4) & 0x0FFF;
  }

  private static void SetFat(byte[] fat, int cluster, int value) {
    var off = cluster * 3 / 2;
    if (off + 1 >= fat.Length) return;
    if ((cluster & 1) == 0) {
      fat[off] = (byte)(value & 0xFF);
      fat[off + 1] = (byte)((fat[off + 1] & 0xF0) | ((value >> 8) & 0x0F));
    } else {
      fat[off] = (byte)((fat[off] & 0x0F) | ((value & 0x0F) << 4));
      fat[off + 1] = (byte)((value >> 4) & 0xFF);
    }
  }

  private static int FindContiguousFreeClusters(byte[] fat, Bpb bpb, int count) {
    var run = 0;
    var start = -1;
    var maxCluster = 2 + bpb.TotalClusters;
    for (var c = 2; c < maxCluster; c++) {
      if (GetFat(fat, c) == 0) {
        if (run == 0) start = c;
        run++;
        if (run == count) return start;
      } else {
        run = 0;
        start = -1;
      }
    }
    return -1;
  }

  // ── Root directory I/O ──────────────────────────────────────────────

  private static byte[] ReadRoot(Stream image, Bpb bpb) {
    var len = bpb.RootEntries * 32;
    var buf = new byte[len];
    image.Position = bpb.RootDirOffset;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteRoot(Stream image, Bpb bpb, byte[] root) {
    image.Position = bpb.RootDirOffset;
    image.Write(root, 0, root.Length);
  }

  private static int FindFreeDirSlot(byte[] root) {
    for (var i = 0; i * 32 < root.Length; i++) {
      var first = root[i * 32];
      if (first == 0x00 || first == 0xE5) return i;
    }
    return -1;
  }

  // ── Name helpers ────────────────────────────────────────────────────

  private static (byte[] Name, byte[] Ext) SplitShortName(string raw) {
    var safe = (raw ?? "").Replace('\\', '/');
    var slash = safe.LastIndexOf('/');
    if (slash >= 0) safe = safe[(slash + 1)..];
    safe = safe.ToUpperInvariant();
    var dot = safe.LastIndexOf('.');
    var rawName = dot > 0 ? safe[..dot] : safe;
    var rawExt = dot > 0 ? safe[(dot + 1)..] : "";
    var nameBytes = new byte[8]; Array.Fill(nameBytes, (byte)0x20);
    var extBytes = new byte[3]; Array.Fill(extBytes, (byte)0x20);
    var nb = Encoding.ASCII.GetBytes(rawName);
    var eb = Encoding.ASCII.GetBytes(rawExt);
    Array.Copy(nb, 0, nameBytes, 0, Math.Min(nb.Length, 8));
    Array.Copy(eb, 0, extBytes, 0, Math.Min(eb.Length, 3));
    return (nameBytes, extBytes);
  }

  private static string NormalizeName(string raw) {
    var safe = (raw ?? "").Replace('\\', '/');
    var slash = safe.LastIndexOf('/');
    if (slash >= 0) safe = safe[(slash + 1)..];
    safe = safe.ToUpperInvariant();
    var dot = safe.LastIndexOf('.');
    var name = dot > 0 ? safe[..dot] : safe;
    var ext = dot > 0 ? safe[(dot + 1)..] : "";
    if (name.Length > 8) name = name[..8];
    if (ext.Length > 3) ext = ext[..3];
    return JoinName(name, ext);
  }

  private static string JoinName(string name, string ext)
    => string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";

  private static string ReadName(ReadOnlySpan<byte> span) {
    Span<char> chars = stackalloc char[span.Length];
    var len = 0;
    foreach (var b in span) {
      if (b is 0 or 0x20) { if (len == 0) continue; break; }
      chars[len++] = (char)(b & 0x7F);
    }
    return new string(chars[..len]).ToUpperInvariant();
  }
}
