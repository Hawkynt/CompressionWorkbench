#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fat;

/// <summary>
/// Builds FAT12 / FAT16 / FAT32 filesystem images from scratch per the Microsoft
/// FAT specification (FATGEN103, EFI FAT32). Auto-selects FAT type based on
/// cluster count. Emits VFAT / LFN (Long File Name) directory entries
/// transparently when the input filename does not fit in 8.3 (mixed-case,
/// non-ASCII, longer than 8 + 3 chars, or with multiple dots) — DOS-era
/// readers see only the short name, modern readers see the long one.
/// </summary>
/// <remarks>
/// FAT32 layout: 32 reserved sectors (boot @0, FSInfo @1, backup boot @6), two
/// FAT copies, root directory at cluster 2 with FAT entry = end-of-chain.
/// LFN format: 32-byte slots with attribute 0x0F immediately preceding the
/// matching 8.3 dirent, written in reverse order so the highest-sequence slot
/// is read first; each holds 13 UTF-16LE code units (5+6+2 split) and a
/// checksum of the associated short name.
/// </remarks>
public sealed class FatWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>
  /// Adds a file to the image. Long names (mixed case, > 8.3, non-ASCII,
  /// multiple dots) are written as VFAT/LFN entries with an auto-generated
  /// 8.3 short-name alias. Plain 8.3 names are written as a single dirent.
  /// </summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>
  /// Builds the FAT filesystem image.
  /// </summary>
  /// <param name="totalSectors">Total sectors (default 2880 = 1.44 MB floppy).</param>
  /// <param name="bytesPerSector">Bytes per sector (default 512).</param>
  /// <returns>Complete disk image as byte array.</returns>
  public byte[] Build(int totalSectors = 2880, int bytesPerSector = 512) {
    const int fatCount = 2;

    // Start with FAT12 floppy defaults
    var reservedSectors = 1;
    var sectorsPerCluster = 1;
    var rootEntryCount = 224;
    var fatSize = 9; // sectors per FAT for 1.44MB floppy

    // Determine FAT type
    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    var totalDataClusters = (totalSectors - firstDataSector) / sectorsPerCluster;
    var fatType = totalDataClusters < 4085 ? 12 : totalDataClusters < 65525 ? 16 : 32;

    // Adjust parameters for FAT16/32.
    if (fatType == 16) {
      sectorsPerCluster = 4;
      rootEntryCount = 512;
      rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
      fatSize = (totalSectors * 2 / bytesPerSector) + 1;
      firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    } else if (fatType == 32) {
      reservedSectors = 32; // FAT32 requires >=1 but convention is 32 (leaves room for FSInfo+BackupBoot)
      rootEntryCount = 0;   // FAT32 root is in the cluster chain, not a fixed area
      rootDirSectors = 0;
      // Sectors-per-cluster heuristic from FATGEN103 table.
      sectorsPerCluster = totalSectors < 66600 ? 1
        : totalSectors < 532480 ? 1      // up to 260 MB, 512-byte clusters ⇒ 1 spc
        : totalSectors < 16777216 ? 8    // up to 8 GB ⇒ 4 KB clusters
        : totalSectors < 33554432 ? 16
        : totalSectors < 67108864 ? 32
        : 64;
      // Estimate FAT size: (data sectors / spc) entries × 4 bytes each, rounded up.
      var dataSectorsEstimate = totalSectors - reservedSectors;
      var dataClustersEstimate = dataSectorsEstimate / sectorsPerCluster;
      fatSize = (dataClustersEstimate * 4 + bytesPerSector - 1) / bytesPerSector;
      firstDataSector = reservedSectors + fatCount * fatSize;
    }

    var disk = new byte[(long)totalSectors * bytesPerSector];

    // ── Boot sector (shared base) ──────────────────────────────────────────
    if (fatType == 32) { disk[0] = 0xEB; disk[1] = 0x58; disk[2] = 0x90; }
    else { disk[0] = 0xEB; disk[1] = 0x3C; disk[2] = 0x90; }
    Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(disk, 3);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(11), (ushort)bytesPerSector);
    disk[13] = (byte)sectorsPerCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(14), (ushort)reservedSectors);
    disk[16] = (byte)fatCount;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(17), (ushort)rootEntryCount);
    if (fatType != 32 && totalSectors < 65536)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(19), (ushort)totalSectors);
    else
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(32), (uint)totalSectors);
    disk[21] = 0xF8; // media: fixed / hard disk
    if (fatType != 32)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(22), (ushort)fatSize);
    // (FAT32 writes fat_size_32 at offset 36 below.)
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(24), 63); // sectors per track
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(26), 255); // heads
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(28), 0u);  // hidden sectors

    if (fatType == 32) {
      // ── FAT32 extended BPB ───────────────────────────────────────────────
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(36), (uint)fatSize);   // BPB_FATSz32
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(40), 0);               // BPB_ExtFlags: mirror
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(42), 0);               // BPB_FSVer: 0.0
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(44), 2u);              // BPB_RootClus: root at cluster 2
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(48), 1);               // BPB_FSInfo: sector 1
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(50), 6);               // BPB_BkBootSec: backup at sector 6
      // 52-63 reserved (already zero)
      disk[64] = 0x80;                                                             // BS_DrvNum
      disk[66] = 0x29;                                                             // BS_BootSig: extended BPB present
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(67), 0x12345678u);     // BS_VolID
      Encoding.ASCII.GetBytes("NO NAME    ").CopyTo(disk, 71);                     // BS_VolLab (11 bytes)
      Encoding.ASCII.GetBytes("FAT32   ").CopyTo(disk, 82);                        // BS_FilSysType (8 bytes)
    } else {
      // Short extended BPB (FAT12/16)
      disk[36] = 0x80;
      disk[38] = 0x29;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(39), 0x12345678u);
      Encoding.ASCII.GetBytes("NO NAME    ").CopyTo(disk, 43);
      Encoding.ASCII.GetBytes(fatType == 12 ? "FAT12   " : "FAT16   ").CopyTo(disk, 54);
    }

    disk[510] = 0x55; disk[511] = 0xAA;

    // ── FAT32 FSInfo sector (sector 1) ───────────────────────────────────
    if (fatType == 32) {
      var fsInfo = 1 * bytesPerSector;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo), 0x41615252u);           // FSI_LeadSig
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 484), 0x61417272u);     // FSI_StrucSig
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 488), 0xFFFFFFFFu);     // FSI_Free_Count = unknown
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 492), 0xFFFFFFFFu);     // FSI_Nxt_Free = unknown
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 508), 0xAA550000u);     // FSI_TrailSig

      // ── Backup boot sector (sector 6) ──────────────────────────────────
      var bkOff = 6 * bytesPerSector;
      Array.Copy(disk, 0, disk, bkOff, bytesPerSector);
      // Backup FSInfo (sector 7)
      var bkFsInfo = 7 * bytesPerSector;
      Array.Copy(disk, fsInfo, disk, bkFsInfo, bytesPerSector);
    }

    // ── FAT initialisation: media byte + EoC markers for clusters 0 and 1 ─
    var fatOffset = reservedSectors * bytesPerSector;
    if (fatType == 12) {
      disk[fatOffset] = 0xF8; disk[fatOffset + 1] = 0xFF; disk[fatOffset + 2] = 0xFF;
    } else if (fatType == 16) {
      disk[fatOffset] = 0xF8; disk[fatOffset + 1] = 0xFF;
      disk[fatOffset + 2] = 0xFF; disk[fatOffset + 3] = 0xFF;
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset), 0x0FFFFFF8u);
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 4), 0x0FFFFFFFu);
    }

    // ── Root directory and file data ──────────────────────────────────────
    var clusterSize = sectorsPerCluster * bytesPerSector;

    // Pre-compute the per-file directory-entry blob. Each plain 8.3 file
    // contributes 32 bytes; every LFN-eligible file contributes
    // ceil(len/13)·32 bytes of LFN slots followed by 32 bytes of 8.3 entry.
    // We need the total up-front to know how many clusters the FAT32 root
    // directory needs (it lives in the cluster chain, so root may span > 1
    // cluster) — and to honour FAT12/16's BPB_RootEntCnt limit.
    var existingShortNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var perFileSlots = new List<byte[]>(_files.Count);
    foreach (var (name, _) in _files) {
      perFileSlots.Add(BuildDirentSlots(name, existingShortNames));
    }
    var totalDirentBytes = perFileSlots.Sum(s => s.Length);

    int dirEntryPos;
    int nextCluster;
    var dataAreaOffset = firstDataSector * bytesPerSector;
    if (fatType == 32) {
      // Root lives in the cluster chain at cluster 2 — possibly spanning
      // multiple clusters if many LFN slots accumulate. Allocate enough
      // contiguous clusters to fit all root dirents, mark them as a chain
      // ending in EOC, and start file clusters after the last root cluster.
      var rootClustersNeeded = Math.Max(1, (totalDirentBytes + clusterSize - 1) / clusterSize);
      for (var rc = 0; rc < rootClustersNeeded; rc++) {
        var cluster = 2 + rc;
        var nextVal = (rc + 1 < rootClustersNeeded) ? cluster + 1 : 0x0FFFFFFF;
        WriteFatEntry(disk, fatOffset, cluster, nextVal, fatType);
      }
      var rootStart = firstDataSector * bytesPerSector;
      dirEntryPos = rootStart;
      nextCluster = 2 + rootClustersNeeded;
    } else {
      dirEntryPos = (reservedSectors + fatCount * fatSize) * bytesPerSector;
      nextCluster = 2;
    }

    // Place each file: copy its dirent slots into the root, then write data
    // into the next free cluster run + chain it in the FAT.
    for (var i = 0; i < _files.Count; i++) {
      var (_, data) = _files[i];
      var slots = perFileSlots[i];
      // Patch the start-cluster fields of the *short-name entry* (always the
      // last 32 bytes of the slot blob) now that we know where its data
      // will live.
      var sn = slots.AsSpan(slots.Length - 32, 32);
      if (fatType == 32)
        BinaryPrimitives.WriteUInt16LittleEndian(sn[20..], (ushort)((nextCluster >> 16) & 0xFFFF));
      BinaryPrimitives.WriteUInt16LittleEndian(sn[26..], (ushort)(nextCluster & 0xFFFF));
      BinaryPrimitives.WriteUInt32LittleEndian(sn[28..], (uint)data.Length);

      // Copy dirent slots into the root directory area.
      slots.CopyTo(disk.AsSpan(dirEntryPos));
      dirEntryPos += slots.Length;

      // Write file data to clusters
      var clustersNeeded = Math.Max(1, (data.Length + clusterSize - 1) / clusterSize);
      var clusterOffset = dataAreaOffset + (long)(nextCluster - 2) * clusterSize;
      if (clusterOffset + data.Length <= disk.Length && data.Length > 0)
        Buffer.BlockCopy(data, 0, disk, (int)clusterOffset, data.Length);

      // Write FAT chain
      for (var c = 0; c < clustersNeeded; c++) {
        var cluster = nextCluster + c;
        var nextVal = (c + 1 < clustersNeeded)
          ? cluster + 1
          : (fatType == 12 ? 0xFFF : fatType == 16 ? 0xFFFF : 0x0FFFFFFF);
        WriteFatEntry(disk, fatOffset, cluster, nextVal, fatType);
      }

      nextCluster += clustersNeeded;
    }

    // ── FSInfo accounting (FAT32 only) ───────────────────────────────────
    if (fatType == 32) {
      var fsInfo = 1 * bytesPerSector;
      // Total data clusters in the volume = (DataSec / SectorsPerCluster).
      var dataSec = totalSectors - firstDataSector;
      var totalClusters = (uint)(dataSec / sectorsPerCluster);
      // Used = clusters allocated from 2..nextCluster-1.
      var used = (uint)(nextCluster - 2);
      var free = used <= totalClusters ? totalClusters - used : 0u;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 488), free);
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 492), (uint)(nextCluster - 1));
      // Mirror to backup FSInfo at sector 7.
      Array.Copy(disk, fsInfo, disk, 7 * bytesPerSector, bytesPerSector);
    }

    // Copy FAT1 to FAT2
    Buffer.BlockCopy(disk, fatOffset, disk, fatOffset + fatSize * bytesPerSector, fatSize * bytesPerSector);

    return disk;
  }

  // ── VFAT/LFN encoding ────────────────────────────────────────────────
  //
  // For each input filename we produce a contiguous byte buffer of dirent
  // slots: zero or more 32-byte LFN slots followed by exactly one 32-byte
  // 8.3 short-name entry. The 8.3 portion is left with placeholder zeroes
  // for first-cluster / file-size — the caller patches those once the data
  // location is known.

  /// <summary>Returns true if <paramref name="name"/> can be represented
  /// in pure 8.3 (uppercase ASCII, ≤ 8 chars base, ≤ 3 chars ext, single
  /// dot, no spaces, no LFN-only chars).</summary>
  private static bool IsPlain8Dot3(string name) {
    var dotIdx = name.LastIndexOf('.');
    var basePart = dotIdx >= 0 ? name[..dotIdx] : name;
    var extPart = dotIdx >= 0 ? name[(dotIdx + 1)..] : "";
    if (basePart.Length is 0 or > 8) return false;
    if (extPart.Length > 3) return false;
    // Disallow secondary dots in the base — that always requires LFN.
    if (basePart.Contains('.')) return false;
    foreach (var c in basePart)
      if (!Is83Char(c)) return false;
    foreach (var c in extPart)
      if (!Is83Char(c)) return false;
    return true;
  }

  /// <summary>Characters allowed in a raw 8.3 entry per FATGEN103 §6.1.
  /// Uppercase ASCII, digits, and a small punctuation set. Lowercase
  /// letters force LFN to preserve case (DOS uppercases on display but
  /// VFAT preserves user case via the long name).</summary>
  private static bool Is83Char(char c) =>
    c is >= 'A' and <= 'Z'
    or >= '0' and <= '9'
    or '_' or '-' or '$' or '%' or '\'' or '@' or '~' or '`' or '!'
    or '(' or ')' or '{' or '}' or '^' or '#' or '&';

  /// <summary>Sanitises a single character for the 8.3 alias: uppercase
  /// ASCII, digits, and underscore-substitute everything else.</summary>
  private static char SanitizeForShort(char c) {
    if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
    if (c is >= 'a' and <= 'z') return (char)(c - 32);
    return Is83Char(c) ? c : '_';
  }

  /// <summary>Generates an 8.3 alias for a long filename per the VFAT
  /// algorithm: uppercase, drop spaces and dots from the base, replace
  /// disallowed chars with underscore, truncate the base to 6 chars and
  /// append <c>~N</c> if collisions or truncation occurred.</summary>
  private static string GenerateShortName(string longName, HashSet<string> existing) {
    var lastDot = longName.LastIndexOf('.');
    var rawBase = lastDot > 0 ? longName[..lastDot] : longName;
    var rawExt = lastDot > 0 ? longName[(lastDot + 1)..] : "";

    var basePart = new StringBuilder();
    var lossy = false;
    foreach (var c in rawBase) {
      if (c is ' ' or '.') { lossy = true; continue; }
      if (Is83Char(char.ToUpperInvariant(c))) basePart.Append(char.ToUpperInvariant(c));
      else if (c is >= 'a' and <= 'z') { basePart.Append((char)(c - 32)); lossy = true; }
      else { basePart.Append('_'); lossy = true; }
    }
    if (basePart.Length == 0) basePart.Append("FILE");

    var extPart = new StringBuilder();
    foreach (var c in rawExt) {
      if (extPart.Length >= 3) { lossy = true; break; }
      if (c is ' ' or '.') { lossy = true; continue; }
      if (Is83Char(char.ToUpperInvariant(c))) extPart.Append(char.ToUpperInvariant(c));
      else { extPart.Append('_'); lossy = true; }
    }

    // Truncate base to 6 chars and append ~N when a long name collapses;
    // also when a name was truncated above 8 chars; also when we already
    // have a colliding short name from a previous file.
    var needsTilde = lossy || basePart.Length > 8 || rawBase.Length > 8;
    if (needsTilde) {
      var head = basePart.ToString();
      if (head.Length > 6) head = head[..6];
      for (var n = 1; n < 1_000_000; n++) {
        var candidate = $"{head}~{n}";
        if (extPart.Length > 0) candidate += "." + extPart;
        if (existing.Add(candidate)) return candidate;
      }
      throw new InvalidOperationException("FAT: unable to generate unique 8.3 short name.");
    }

    var simple = basePart.ToString();
    if (extPart.Length > 0) simple += "." + extPart;
    if (!existing.Add(simple)) {
      // Plain-8.3 collision (case-insensitive): fall back to ~N too.
      var head = basePart.Length > 6 ? basePart.ToString(0, 6) : basePart.ToString();
      for (var n = 1; n < 1_000_000; n++) {
        var candidate = $"{head}~{n}";
        if (extPart.Length > 0) candidate += "." + extPart;
        if (existing.Add(candidate)) return candidate;
      }
    }
    return simple;
  }

  /// <summary>FAT/VFAT short-name checksum (FATGEN103 §6.4): unsigned
  /// rotate-right-with-add over the 11 raw 8.3 bytes. Stored in every LFN
  /// slot so a corrupt or out-of-order slot can be detected.</summary>
  private static byte LfnChecksum(ReadOnlySpan<byte> short11) {
    byte sum = 0;
    for (var i = 0; i < 11; i++)
      sum = (byte)((((sum & 1) != 0 ? 0x80 : 0) + (sum >> 1) + short11[i]) & 0xFF);
    return sum;
  }

  /// <summary>Builds the 32-byte raw 8.3 directory entry (offset 0..31)
  /// for a short name like <c>"HELLO   TXT"</c> (already padded). Caller
  /// fills first-cluster + size fields later.</summary>
  private static byte[] BuildShortEntry(string shortName) {
    var entry = new byte[32];
    var dotIdx = shortName.LastIndexOf('.');
    var basePart = dotIdx >= 0 ? shortName[..dotIdx] : shortName;
    var extPart = dotIdx >= 0 ? shortName[(dotIdx + 1)..] : "";
    var basePad = basePart.PadRight(8).Substring(0, 8);
    var extPad = extPart.PadRight(3).Substring(0, 3);
    Encoding.ASCII.GetBytes(basePad).CopyTo(entry, 0);
    Encoding.ASCII.GetBytes(extPad).CopyTo(entry, 8);
    entry[11] = 0x20; // Archive attribute
    return entry;
  }

  /// <summary>Builds the slot blob for one file (LFN entries first if the
  /// long name needs them, then the 8.3 entry). Updates <paramref
  /// name="existingShortNames"/> with the chosen alias to detect ~N
  /// collisions across subsequent files.</summary>
  private static byte[] BuildDirentSlots(string longName, HashSet<string> existingShortNames) {
    if (IsPlain8Dot3(longName)) {
      existingShortNames.Add(longName.ToUpperInvariant());
      return BuildShortEntry(longName.ToUpperInvariant());
    }

    var shortName = GenerateShortName(longName, existingShortNames);
    var shortEntry = BuildShortEntry(shortName);
    var checksum = LfnChecksum(shortEntry.AsSpan(0, 11));

    // Each LFN slot carries 13 UTF-16 units: pad with NUL (after the real
    // name) plus 0xFFFF for unused trailing slots, per the spec.
    var fragments = (longName.Length + 13) / 13; // include space for trailing NUL
    if (fragments < 1) fragments = 1;
    if (fragments > 20)
      throw new InvalidOperationException("FAT: long name exceeds 255 UTF-16 chars.");

    var blob = new byte[fragments * 32 + 32];
    // Slot N (highest sequence) goes first on disk; per FAT spec it's
    // marked with the 0x40 "last-LFN" flag.
    for (var slotIdx = 0; slotIdx < fragments; slotIdx++) {
      var seq = fragments - slotIdx; // LDIR_Ord 1..N reading on-disk
      var firstChar = (seq - 1) * 13;
      var slotOffset = slotIdx * 32;

      blob[slotOffset + 0] = (byte)(seq | (slotIdx == 0 ? 0x40 : 0));
      blob[slotOffset + 11] = 0x0F;  // attribute: LFN
      blob[slotOffset + 12] = 0;     // type
      blob[slotOffset + 13] = checksum;
      // FstClusLO at offset 26 stays zero per spec.

      // Layout: 5 chars at [1..10], 6 chars at [14..25], 2 chars at [28..31].
      WriteLfnChars(blob, slotOffset + 1, 5, longName, firstChar);
      WriteLfnChars(blob, slotOffset + 14, 6, longName, firstChar + 5);
      WriteLfnChars(blob, slotOffset + 28, 2, longName, firstChar + 11);
    }
    shortEntry.CopyTo(blob, fragments * 32);
    return blob;
  }

  /// <summary>Writes <paramref name="count"/> UTF-16LE chars from
  /// <paramref name="name"/> starting at <paramref name="firstChar"/>;
  /// the first out-of-range char is encoded as NUL (0x0000), every
  /// subsequent slot position is padded with 0xFFFF.</summary>
  private static void WriteLfnChars(byte[] blob, int offset, int count, string name, int firstChar) {
    var pastEnd = false;
    for (var j = 0; j < count; j++) {
      var idx = firstChar + j;
      ushort code;
      if (pastEnd) {
        code = 0xFFFF;
      } else if (idx < name.Length) {
        code = name[idx];
      } else if (idx == name.Length) {
        code = 0x0000;
        pastEnd = true;
      } else {
        code = 0xFFFF;
      }
      BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(offset + j * 2), code);
    }
  }

  /// <summary>
  /// Convenience: builds a FAT image from a list of files, auto-sizing to fit.
  /// Used by virtual-disk writers (QCOW2, VHD, VMDK, VDI) to embed a filesystem
  /// inside a disk container so that Create() produces a usable volume.
  /// </summary>
  public static byte[] BuildFromFiles(IEnumerable<(string name, byte[] data)> files) {
    var w = new FatWriter();
    var totalData = 0L;
    foreach (var (name, data) in files) {
      w.AddFile(ToShortName(name), data);
      totalData += data.Length;
    }
    // Auto-size: data + ~50% overhead, minimum 1.44 MB.
    var neededBytes = Math.Max(totalData * 3 / 2 + 32768, 1440 * 1024);
    var totalSectors = Math.Max(2880, (int)((neededBytes + 511) / 512));
    return w.Build(totalSectors);
  }

  private static string ToShortName(string name) {
    var leaf = Path.GetFileName(name);
    var dotIdx = leaf.LastIndexOf('.');
    var basePart = (dotIdx >= 0 ? leaf[..dotIdx] : leaf).ToUpperInvariant();
    var extPart = (dotIdx >= 0 ? leaf[(dotIdx + 1)..] : "").ToUpperInvariant();
    basePart = new string(basePart.Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_').ToArray());
    extPart = new string(extPart.Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_').ToArray());
    if (basePart.Length == 0) basePart = "FILE";
    if (basePart.Length > 8) basePart = basePart[..8];
    if (extPart.Length > 3) extPart = extPart[..3];
    return extPart.Length > 0 ? $"{basePart}.{extPart}" : basePart;
  }

  private static void WriteFatEntry(byte[] disk, int fatOffset, int cluster, int value, int fatType) {
    if (fatType == 12) {
      var bytePos = fatOffset + cluster * 3 / 2;
      if (bytePos + 1 >= disk.Length) return;
      if ((cluster & 1) == 0) {
        disk[bytePos] = (byte)(value & 0xFF);
        disk[bytePos + 1] = (byte)((disk[bytePos + 1] & 0xF0) | ((value >> 8) & 0x0F));
      } else {
        disk[bytePos] = (byte)((disk[bytePos] & 0x0F) | ((value << 4) & 0xF0));
        disk[bytePos + 1] = (byte)((value >> 4) & 0xFF);
      }
    } else if (fatType == 16) {
      var pos = fatOffset + cluster * 2;
      if (pos + 2 <= disk.Length)
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(pos), (ushort)value);
    } else {
      var pos = fatOffset + cluster * 4;
      if (pos + 4 <= disk.Length)
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(pos), (uint)value & 0x0FFFFFFFu);
    }
  }
}
