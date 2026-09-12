#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Mfs;

/// <summary>
/// In-place modifier for MFS (Macintosh File System) disk images. Performs
/// add / remove on an existing image with strict <b>O(touched bytes)</b> I/O —
/// only reads the volume info block (sector 2), the file directory area
/// (sectors 3 through <c>drAlBlSt - 1</c>), and the affected file's
/// allocation blocks. Never reads or writes the entire image.
///
/// <para>The companion <see cref="MfsWriter"/> rebuilds an image from
/// scratch; this class is for the "I have an existing image, mutate it"
/// path that <c>IArchiveModifiable</c> exposes.</para>
///
/// <para>Layout reminders for the simplified MFS variant produced by
/// <see cref="MfsWriter"/> and consumed by <see cref="MfsReader"/>:
/// <list type="bullet">
///   <item>Sectors 0-1: boot blocks (1024 bytes total).</item>
///   <item>Sector 2 (offset 1024): MDB / volume info block — magic 0xD2D7 BE,
///         <c>drNmAlBlks</c> at +18 (u16 BE), <c>drAlBlkSiz</c> at +20 (u32 BE),
///         <c>drAlBlSt</c> (first allocation-block sector) at +28 (u16 BE),
///         volume name pstring at +36.</item>
///   <item>File directory: starts at offset 1024+128 (= 1152, mid sector 2)
///         and runs up to <c>drAlBlSt * 512</c>.</item>
///   <item>Each directory entry is variable length:
///     <list type="bullet">
///       <item>+0: flags byte (0x80 = in use, 0 = end-of-directory marker).</item>
///       <item>+1: version byte.</item>
///       <item>+26: first allocation block (u16 BE, 0-based within data area).</item>
///       <item>+28: logical EOF / data size in bytes (u32 BE).</item>
///       <item>+38: name length byte.</item>
///       <item>+39..: ASCII name, padded so total entry length is even.</item>
///     </list>
///   </item>
///   <item>Allocation blocks (<c>drAlBlkSiz</c> bytes each, typically 1024)
///         start at <c>drAlBlSt * 512</c> and are stored contiguously per file.
///         Block IDs are 0-based within the data area in this implementation.</item>
///   <item>Real-MFS-on-disk would also have a 12-bit packed block map between
///         the MDB and the directory; the simplified writer omits it. The
///         modifier therefore derives allocation status by walking the
///         existing directory entries (each entry covers blocks
///         <c>FirstBlock</c>..<c>FirstBlock + ⌈Size / drAlBlkSiz⌉ - 1</c>),
///         which matches what the round-trip reader/writer already do. This
///         keeps the on-disk format compatible with the round-trip path.</item>
/// </list></para>
/// </summary>
public static class MfsModifier {
  private const int SectorSize = 512;
  private const int MdbOffset = 1024;
  private const int MdbSectorIdx = 2;
  private const int DirStartOffset = MdbOffset + 128; // 1152
  private const ushort MfsMagic = 0xD2D7;

  // ── Public API ────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a file to the existing MFS image. Performs in-place modification:
  /// derives the allocation map from existing directory entries, allocates
  /// a contiguous run of free blocks, writes the file data, and writes a
  /// new directory entry into the first free slot. Bytes touched: 1 MDB
  /// sector + the directory sectors that get walked + the file's data
  /// blocks + the dir sector(s) containing the new entry.
  /// </summary>
  /// <exception cref="IOException">Disk full (no contiguous free run large
  /// enough) or directory full (no room for a new entry).</exception>
  /// <exception cref="ArgumentException">File name longer than 255 bytes.</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var nameBytes = Encoding.ASCII.GetBytes(name);
    if (nameBytes.Length > 255)
      throw new ArgumentException("MFS file names are limited to 255 bytes.", nameof(name));

    var (numAllocBlocks, blockSize, firstAllocSector) = ReadVolumeInfo(image);
    var firstAllocOffset = firstAllocSector * SectorSize;
    var dirEnd = firstAllocOffset;

    var blocksNeeded = data.Length == 0 ? 0 : (int)((data.Length + blockSize - 1) / blockSize);

    // Walk directory: collect occupied block ranges, find a free slot.
    var occupied = new List<(int Start, int Count)>();
    var freeSlot = WalkDirectoryForAdd(image, numAllocBlocks, (int)blockSize, dirEnd, occupied);

    // Find a contiguous free run starting at the lowest free index.
    var startBlock = blocksNeeded == 0
      ? 0
      : FindFreeContiguous(occupied, numAllocBlocks, blocksNeeded)
        ?? throw new IOException($"MFS disk full: cannot allocate {blocksNeeded} contiguous blocks.");

    // Write file data into the allocated blocks.
    if (blocksNeeded > 0) {
      var dataOffset = firstAllocOffset + startBlock * (long)blockSize;
      image.Position = dataOffset;
      image.Write(data);
      // Pad the tail of the last allocation block with zeros (hygiene; allocator
      // assumes block-aligned chunks; this keeps stale bytes out of the slack).
      var written = data.Length;
      var tail = (int)(blocksNeeded * blockSize - written);
      if (tail > 0)
        image.Write(new byte[tail]);
    }

    // Write the directory entry into the chosen slot's sector.
    WriteDirectoryEntry(image, freeSlot, nameBytes, (ushort)startBlock, (uint)data.Length);
  }

  /// <summary>
  /// Removes the named file from the existing MFS image. Walks the
  /// directory to locate the entry, optionally wipes the file's allocation
  /// blocks, and clears the directory entry's flags byte (and shifts later
  /// entries in the same sector left to keep the directory chain contiguous,
  /// since MFS uses a 0-flags byte as end-of-directory marker).
  /// Returns true if the file was found and removed, false otherwise.
  /// Bytes touched: 1 MDB sector + the directory sectors walked + the
  /// removed file's data blocks (if wiping) + dir sector containing the entry.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var (_, blockSize, firstAllocSector) = ReadVolumeInfo(image);
    var firstAllocOffset = firstAllocSector * SectorSize;
    var dirEnd = firstAllocOffset;

    var loc = LocateDirectoryEntry(image, name, dirEnd);
    if (loc == null)
      return false;

    // Wipe the data blocks if requested.
    if (wipeData && loc.Size > 0) {
      var blocks = (int)((loc.Size + blockSize - 1) / blockSize);
      var totalBytes = blocks * (long)blockSize;
      var dataOffset = firstAllocOffset + loc.FirstBlock * (long)blockSize;
      image.Position = dataOffset;
      image.Write(new byte[totalBytes]);
    }

    // Mark the directory entry as deleted by clearing the in-use bit.
    // The reader's contract: flags == 0  → end of directory;
    //                       flags & 0x80 → in-use (parse);
    //                       any other nonzero → skip-and-continue. We use 0x01
    // to land in the "skip" lane while keeping the chain past us intact.
    // Touches a single byte — no costly compaction of trailing entries.
    image.Position = loc.EntryOffset;
    image.WriteByte(0x01);
    return true;
  }

  // ── MDB helpers ───────────────────────────────────────────────────────

  private static (ushort NumAllocBlocks, uint BlockSize, ushort FirstAllocSector) ReadVolumeInfo(Stream image) {
    var mdb = new byte[SectorSize];
    image.Position = MdbOffset;
    image.ReadExactly(mdb);

    var sig = BinaryPrimitives.ReadUInt16BigEndian(mdb);
    if (sig != MfsMagic)
      throw new InvalidDataException("MFS: invalid signature in MDB.");

    var numAllocBlocks = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(18));
    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(mdb.AsSpan(20));
    if (blockSize == 0) blockSize = 1024;
    var firstAllocSector = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(28));
    if (firstAllocSector == 0) firstAllocSector = 12;
    return (numAllocBlocks, blockSize, firstAllocSector);
  }

  // ── Allocation map (derived from directory) ───────────────────────────

  /// <summary>
  /// Finds the lowest-index contiguous run of <paramref name="needed"/> free
  /// blocks in <c>[0, numAllocBlocks)</c>, given the occupied ranges.
  /// Returns null if no such run exists.
  /// </summary>
  private static int? FindFreeContiguous(List<(int Start, int Count)> occupied, int numAllocBlocks, int needed) {
    if (needed == 0) return 0;
    if (numAllocBlocks <= 0) return null;
    // Sort occupied ranges by start, then walk gaps.
    occupied.Sort((a, b) => a.Start.CompareTo(b.Start));
    var cursor = 0;
    foreach (var (start, count) in occupied) {
      if (start > cursor) {
        var gap = start - cursor;
        if (gap >= needed) return cursor;
      }
      cursor = Math.Max(cursor, start + count);
    }
    if (numAllocBlocks - cursor >= needed)
      return cursor;
    return null;
  }

  // ── Directory walker ──────────────────────────────────────────────────

  /// <summary>Returns the on-disk length of a directory entry whose name has
  /// the given byte length, including padding to even total length.</summary>
  private static int EntryLengthFor(int nameLen) {
    var total = 39 + nameLen;
    if ((total & 1) != 0) total++;
    return total;
  }

  /// <summary>
  /// Walks the directory from <see cref="DirStartOffset"/> to <paramref name="dirEnd"/>,
  /// streaming entries one at a time. Records each occupied block range and
  /// returns the offset of the first end-of-directory marker (flags byte 0)
  /// — the slot where a new entry should be written.
  /// </summary>
  private static int WalkDirectoryForAdd(Stream image, int numAllocBlocks, int blockSize, int dirEnd,
                                         List<(int Start, int Count)> occupied) {
    var pos = DirStartOffset;
    var header = new byte[40];
    while (pos + header.Length <= dirEnd) {
      image.Position = pos;
      image.ReadExactly(header);
      var flags = header[0];
      if (flags == 0)
        return pos; // first end-of-directory slot — caller writes here.

      // In-use entry. Record allocation range, then advance to next entry.
      if ((flags & 0x80) != 0) {
        var firstBlock = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(26));
        var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
        var blocks = size == 0 ? 0 : (int)((size + (uint)blockSize - 1) / (uint)blockSize);
        if (blocks > 0 && firstBlock < numAllocBlocks)
          occupied.Add((firstBlock, blocks));
      }
      var nameLen = header[38];
      var entryLen = EntryLengthFor(nameLen);
      pos += entryLen;
    }
    throw new IOException("MFS directory full: no free slot.");
  }

  /// <summary>
  /// Result of a directory lookup: where the entry lives, how big it is on
  /// disk, and the file's first block + size.
  /// </summary>
  private sealed record DirEntryLocation(int EntryOffset, int EntryLength, ushort FirstBlock, uint Size);

  /// <summary>
  /// Walks the directory looking for an entry by name. Returns its location
  /// (offset + size on disk + file block/size), or null if not found.
  /// </summary>
  private static DirEntryLocation? LocateDirectoryEntry(Stream image, string targetName, int dirEnd) {
    var pos = DirStartOffset;
    var header = new byte[40];
    while (pos + header.Length <= dirEnd) {
      image.Position = pos;
      image.ReadExactly(header);
      var flags = header[0];
      if (flags == 0) return null; // end of directory.

      var nameLen = header[38];
      var entryLen = EntryLengthFor(nameLen);
      if ((flags & 0x80) != 0) {
        var nameBytes = new byte[nameLen];
        image.Position = pos + 39;
        image.ReadExactly(nameBytes);
        var entryName = Encoding.ASCII.GetString(nameBytes);
        if (string.Equals(entryName, targetName, StringComparison.Ordinal)) {
          var firstBlock = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(26));
          var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
          return new DirEntryLocation(pos, entryLen, firstBlock, size);
        }
      }
      pos += entryLen;
    }
    return null;
  }

  // ── Directory writer ──────────────────────────────────────────────────

  /// <summary>
  /// Writes a fresh directory entry at the given offset. Entry layout:
  /// flags=0x80, version=0, type/creator/forks zeroed, firstBlock at +26
  /// (u16 BE), size at +28 (u32 BE), nameLen at +38, name at +39+, padded
  /// to even length with a trailing zero (the next entry's flags byte =
  /// end-of-directory marker).
  /// </summary>
  private static void WriteDirectoryEntry(Stream image, int entryOffset, byte[] nameBytes,
                                          ushort firstBlock, uint size) {
    var entryLen = EntryLengthFor(nameBytes.Length);
    // Build entry in a single buffer — one stream write keeps the I/O bound tight.
    var buf = new byte[entryLen];
    buf[0] = 0x80; // flags: in use
    buf[1] = 0;    // version
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(26), firstBlock);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(28), size);
    buf[38] = (byte)nameBytes.Length;
    nameBytes.CopyTo(buf, 39);
    // Trailing pad byte (if any) is already zero (= future end-of-directory marker).

    image.Position = entryOffset;
    image.Write(buf);
  }
}
