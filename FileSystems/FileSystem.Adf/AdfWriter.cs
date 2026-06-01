#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Adf;

/// <summary>
/// Creates Amiga Disk File (.adf) images using the Fast File System (FFS).
/// Produces standard DD disk images of exactly 901,120 bytes (1760 sectors of 512 bytes).
/// </summary>
public sealed class AdfWriter {
  private const int SectorSize = 512;
  private const int TotalSectors = 1760;
  private const int DiskSize = TotalSectors * SectorSize;
  private const int RootSector = 880;
  private const int BitmapSector = 881;
  private const int HashTableCount = 72;
  private const int HashTableOffset = 24;   // 72 × uint32 BE hash table entries
  private const int HashChainOffset = 496;  // uint32 BE — next entry in same hash bucket

  private readonly List<(string Name, byte[] Data, DateTime? ModTime)> _files = [];

  /// <summary>
  /// Adds a file to the disk image being built.
  /// </summary>
  /// <param name="name">The filename (up to 30 ASCII characters).</param>
  /// <param name="data">The file content.</param>
  /// <param name="modTime">File modification time. Uses current time when null.</param>
  public void AddFile(string name, byte[] data, DateTime? modTime = null) => _files.Add((name, data, modTime));

  /// <summary>
  /// Builds and returns the complete 901,120-byte ADF disk image.
  /// </summary>
  /// <param name="diskName">The volume name written to the root block (up to 30 characters).</param>
  /// <param name="fileSystemType">
  /// Boot block file-system identifier byte at offset 3:
  /// 0 = OFS (Original File System), 1 = FFS (Fast File System, default).
  /// The on-disk block layout this writer emits matches the FFS family for
  /// both values (pure data blocks, no per-data-block header); the boot byte
  /// just advertises which AmigaDOS handler the volume targets.
  /// </param>
  /// <returns>A byte array of exactly 901,120 bytes representing the disk image.</returns>
  public byte[] Build(string diskName = "DISK", byte fileSystemType = 1) {
    var disk = new byte[DiskSize];
    var used = new bool[TotalSectors];

    // Boot block: "DOS\<fileSystemType>" — 0 = OFS, 1 = FFS (default).
    disk[0] = (byte)'D';
    disk[1] = (byte)'O';
    disk[2] = (byte)'S';
    disk[3] = fileSystemType;
    used[0] = true;
    used[1] = true;

    // Reserve root and bitmap
    used[RootSector] = true;
    used[BitmapSector] = true;

    // Maps a slash-separated directory path (e.g. "docs/api") to the sector of
    // its user-directory header block. The empty path maps to the root block so
    // that a directory's hash table can be located uniformly.
    var dirSectors = new Dictionary<string, int> { [""] = RootSector };

    foreach (var (name, data, modTime) in _files) {
      // Split the incoming name into its directory components and leaf filename.
      // Either '/' or '\' may appear as a separator; empty components are ignored.
      var parts = name.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0)
        continue;

      var leafName = parts[^1];
      var parentSector = EnsureDirectoryPath(disk, used, dirSectors, parts[..^1], modTime);

      // Allocate file header block
      var headerSector = AllocateSector(used, RootSector + 2);
      if (headerSector < 0)
        throw new InvalidOperationException($"ADF: disk is full; cannot allocate a header sector for '{name}'.");

      // Allocate data blocks
      var dataBlockCount = (data.Length + SectorSize - 1) / SectorSize;
      var dataBlocks = new int[dataBlockCount];
      for (var i = 0; i < dataBlockCount; i++) {
        dataBlocks[i] = AllocateSector(used, headerSector + 1);
        if (dataBlocks[i] < 0)
          throw new InvalidOperationException($"ADF: disk is full; cannot allocate data block {i} for '{name}'.");
      }

      // Write data blocks (FFS: pure data, no header)
      var remaining = data.Length;
      for (var i = 0; i < dataBlockCount; i++) {
        var off = dataBlocks[i] * SectorSize;
        var chunk = Math.Min(SectorSize, remaining);
        data.AsSpan(i * SectorSize, chunk).CopyTo(disk.AsSpan(off));
        remaining -= chunk;
      }

      // Write file header block
      var hdrOff = headerSector * SectorSize;
      WriteUInt32BE(disk, hdrOff, 2); // T_HEADER
      WriteUInt32BE(disk, hdrOff + 4, (uint)headerSector); // own key
      WriteUInt32BE(disk, hdrOff + 8, (uint)dataBlockCount); // high_seq (data block count)
      // Data block pointers at offsets 308, 304, 300, ... (reverse order)
      for (var i = 0; i < dataBlockCount && i < HashTableCount; i++)
        WriteUInt32BE(disk, hdrOff + 308 - i * 4, (uint)dataBlocks[i]);
      WriteUInt32BE(disk, hdrOff + 324, (uint)data.Length); // file size
      WriteFilename(disk, hdrOff + 432, leafName); // filename (leaf only)
      WriteUInt32BE(disk, hdrOff + 508, 0xFFFFFFFD); // sec_type = ST_FILE
      WriteUInt32BE(disk, hdrOff + 504, (uint)parentSector); // parent directory

      // Timestamp: days/mins/ticks since AmigaOS epoch (1978-01-01).
      var (fDays, fMins, fTicks) = ToAmigaTime(modTime ?? DateTime.Now);
      WriteUInt32BE(disk, hdrOff + 420, fDays);
      WriteUInt32BE(disk, hdrOff + 424, fMins);
      WriteUInt32BE(disk, hdrOff + 428, fTicks);

      // Link the file header into its parent directory's hash table.
      LinkIntoHashTable(disk, parentSector, leafName, headerSector);

      // Compute header checksum
      ComputeChecksum(disk, hdrOff);
    }

    // Write root block
    var rootOff = RootSector * SectorSize;
    WriteUInt32BE(disk, rootOff, 2); // T_HEADER
    WriteUInt32BE(disk, rootOff + 4, (uint)RootSector); // own key
    // The root hash table was populated in place while linking entries; only the
    // fixed root-block fields remain to be written here.
    // Bitmap flag at offset 312: -1 = valid
    WriteUInt32BE(disk, rootOff + 312, 0xFFFFFFFF);
    // Bitmap pointer at offset 316
    WriteUInt32BE(disk, rootOff + 316, (uint)BitmapSector);
    WriteFilename(disk, rootOff + 432, diskName);
    WriteUInt32BE(disk, rootOff + 508, 1); // sec_type = ST_ROOT

    // Root block timestamps: last-modified (offset 420) and disk-creation (offset 472).
    var now = DateTime.Now;
    var (rDays, rMins, rTicks) = ToAmigaTime(now);
    WriteUInt32BE(disk, rootOff + 420, rDays);   // r_days  — last root alteration
    WriteUInt32BE(disk, rootOff + 424, rMins);   // r_mins
    WriteUInt32BE(disk, rootOff + 428, rTicks);  // r_ticks
    WriteUInt32BE(disk, rootOff + 472, rDays);   // v_days  — disk creation date
    WriteUInt32BE(disk, rootOff + 476, rMins);   // v_mins
    WriteUInt32BE(disk, rootOff + 480, rTicks);  // v_ticks

    ComputeChecksum(disk, rootOff);

    // Checksum every user-directory block now that all children are linked into
    // their hash tables (the root entry is keyed by the empty path and already
    // checksummed above).
    foreach (var (path, sector) in dirSectors) {
      if (path.Length == 0)
        continue;
      ComputeChecksum(disk, sector * SectorSize);
    }

    // Write bitmap block
    WriteBitmap(disk, used);

    return disk;
  }

  /// <summary>
  /// Ensures every directory along <paramref name="components"/> (relative to the
  /// root) exists as an AmigaDOS user-directory block, creating any missing level
  /// and linking it into its parent's hash table. Returns the sector of the
  /// deepest directory (the root sector when <paramref name="components"/> is empty).
  /// </summary>
  private int EnsureDirectoryPath(
    byte[] disk, bool[] used, Dictionary<string, int> dirSectors,
    string[] components, DateTime? modTime) {
    var parentSector = RootSector;
    var path = "";

    foreach (var component in components) {
      path = path.Length == 0 ? component : path + "/" + component;
      if (dirSectors.TryGetValue(path, out var existing)) {
        parentSector = existing;
        continue;
      }

      // Allocate and write a new user-directory header block.
      var dirSector = AllocateSector(used, RootSector + 2);
      if (dirSector < 0)
        throw new InvalidOperationException($"ADF: disk is full; cannot allocate a directory block for '{path}'.");

      var dirOff = dirSector * SectorSize;
      WriteUInt32BE(disk, dirOff, 2);                    // T_HEADER
      WriteUInt32BE(disk, dirOff + 4, (uint)dirSector);  // own key
      // Hash table (offset 24..) is left zeroed; it fills as children are linked.
      WriteFilename(disk, dirOff + 432, component);      // directory name
      WriteUInt32BE(disk, dirOff + 504, (uint)parentSector); // parent directory
      WriteUInt32BE(disk, dirOff + 508, 2);              // sec_type = ST_USERDIR

      var (dDays, dMins, dTicks) = ToAmigaTime(modTime ?? DateTime.Now);
      WriteUInt32BE(disk, dirOff + 420, dDays);
      WriteUInt32BE(disk, dirOff + 424, dMins);
      WriteUInt32BE(disk, dirOff + 428, dTicks);

      // Link this directory into its parent's hash table. The directory block's
      // checksum is computed at the end of Build, once all of its children have
      // been linked into its hash table.
      LinkIntoHashTable(disk, parentSector, component, dirSector);

      dirSectors[path] = dirSector;
      parentSector = dirSector;
    }

    return parentSector;
  }

  /// <summary>
  /// Links the block at <paramref name="entrySector"/> into the hash table of the
  /// directory (or root) at <paramref name="dirSector"/>, using the AmigaDOS name
  /// hash of <paramref name="entryName"/>. Bucket collisions are chained via the
  /// hash-chain field (offset 496) of the last entry already in the bucket.
  /// The owning directory/root block must be checksummed after its children are
  /// linked, because the checksum covers the hash table this method mutates.
  /// </summary>
  private static void LinkIntoHashTable(byte[] disk, int dirSector, string entryName, int entrySector) {
    var hash = HashName(entryName);
    var bucketOff = dirSector * SectorSize + HashTableOffset + hash * 4;
    var first = ReadUInt32BE(disk, bucketOff);
    if (first == 0) {
      WriteUInt32BE(disk, bucketOff, (uint)entrySector);
      return;
    }

    // Walk the existing chain to its end and append.
    var current = (int)first;
    while (true) {
      var chainOff = current * SectorSize + HashChainOffset;
      var next = ReadUInt32BE(disk, chainOff);
      if (next == 0) {
        WriteUInt32BE(disk, chainOff, (uint)entrySector);
        return;
      }
      current = (int)next;
    }
  }

  private static int AllocateSector(bool[] used, int preferred) {
    // Try near preferred first
    for (var s = preferred; s < TotalSectors; s++) {
      if (!used[s]) { used[s] = true; return s; }
    }
    for (var s = 2; s < preferred; s++) {
      if (!used[s]) { used[s] = true; return s; }
    }
    return -1;
  }

  private static int HashName(string name) {
    var hash = (uint)name.Length;
    foreach (var c in name)
      hash = (hash * 13 + (byte)char.ToUpperInvariant(c)) & 0x7FF;
    return (int)(hash % HashTableCount);
  }

  private static void WriteFilename(byte[] disk, int offset, string name) {
    if (name.Length > 30) name = name[..30];
    disk[offset] = (byte)name.Length;
    Encoding.ASCII.GetBytes(name).CopyTo(disk, offset + 1);
  }

  private static void WriteUInt32BE(byte[] data, int offset, uint value) {
    data[offset] = (byte)(value >> 24);
    data[offset + 1] = (byte)(value >> 16);
    data[offset + 2] = (byte)(value >> 8);
    data[offset + 3] = (byte)value;
  }

  private static uint ReadUInt32BE(byte[] data, int offset) =>
    (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

  private static void ComputeChecksum(byte[] disk, int blockOffset) {
    // Checksum is at offset 20 within the block
    WriteUInt32BE(disk, blockOffset + 20, 0);
    uint sum = 0;
    for (var i = 0; i < SectorSize / 4; i++)
      sum += ReadUInt32BE(disk, blockOffset + i * 4);
    WriteUInt32BE(disk, blockOffset + 20, (uint)(-(int)sum));
  }

  private static (uint days, uint mins, uint ticks) ToAmigaTime(DateTime dt) {
    var epoch = new DateTime(1978, 1, 1);
    if (dt < epoch) dt = epoch;
    var span = dt - epoch;
    var days = (uint)span.Days;
    var secsInDay = (long)(span.TotalSeconds - span.Days * 86400.0);
    var mins = (uint)(secsInDay / 60);
    var ticks = (uint)(secsInDay % 60 * 50);
    return (days, mins, ticks);
  }

  private static void WriteBitmap(byte[] disk, bool[] used) {
    var off = BitmapSector * SectorSize;
    // Bitmap starts at offset 4 (offset 0 is checksum)
    // Each bit represents a sector: 1=free, 0=used
    // Sectors 2 through 1759 mapped to bits
    for (var s = 2; s < TotalSectors; s++) {
      var bitIndex = s - 2;
      var wordIndex = bitIndex / 32;
      var bitPos = bitIndex % 32;
      if (!used[s])
        disk[off + 4 + wordIndex * 4 + (3 - bitPos / 8)] |= (byte)(1 << (bitPos % 8));
    }

    // Compute bitmap checksum (same algorithm, checksum at offset 0)
    WriteUInt32BE(disk, off, 0);
    uint sum = 0;
    for (var i = 0; i < SectorSize / 4; i++)
      sum += ReadUInt32BE(disk, off + i * 4);
    WriteUInt32BE(disk, off, (uint)(-(int)sum));
  }
}
