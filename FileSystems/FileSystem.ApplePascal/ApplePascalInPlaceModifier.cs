#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ApplePascal;

/// <summary>
/// In-place modifier for Apple UCSD Pascal disk volumes. Performs <c>Add</c>,
/// <c>Replace</c>, and <c>Remove</c> against an existing image without rebuilding
/// the entire volume. The companion <see cref="ApplePascalWriter"/> still serves
/// the WORM "build a fresh image from a file list" path; this class handles the
/// "mutate an existing image" path that <see cref="Compression.Registry.IArchiveModifiable"/>
/// exposes.
///
/// <para><b>Spec source.</b> Apple Pascal Operating System Reference Manual (1979/1980).
/// 512-byte blocks, volume directory at blocks 2-5 (file offset 0x400), 26-byte fixed
/// entries packed back-to-back after the 26-byte volume header, hard cap of 77 file
/// entries (78 × 26 = 2028 bytes fits inside the 4-block 2048-byte directory region).</para>
///
/// <para><b>Layout reminders (little-endian throughout):</b>
/// <list type="bullet">
///   <item>Blocks 0-1: boot blocks (1024 bytes total) — untouched by the modifier.</item>
///   <item>Blocks 2-5: volume directory (2048 bytes). First 26 bytes = volume header;
///         next 77 × 26 = 2002 bytes = file entries.</item>
///   <item>Volume header: <c>firstBlock=0</c> at +0, <c>nextBlock</c> at +2 (= first
///         file's start block, conventionally 6), entry type = 0 at +4, name length
///         at +6, name at +7..+13, total blocks at +14, file count at +16, first to
///         access (cached) at +18, last-mod date at +20, reserved at +24.</item>
///   <item>File entry: start block at +0, end block (exclusive) at +2, file kind at
///         +4, name length at +6, name at +7..+21, bytes-in-last-block at +22, date
///         at +24.</item>
///   <item>File data: blocks 6.. — every file occupies a single contiguous extent
///         <c>[startBlock, endBlock)</c>.</item>
/// </list></para>
///
/// <para><b>Scope match with WORM:</b> the modifier respects the same 77-entry
/// cap and the 8-block-tile rounding for the underlying volume size. The flat
/// directory is the only directory — Apple Pascal has no subdirectories by spec.</para>
/// </summary>
public static class ApplePascalInPlaceModifier {

  private const int BlockSize = ApplePascalReader.BlockSize;          // 512
  private const int EntrySize = ApplePascalReader.EntrySize;          // 26
  private const int MaxEntries = ApplePascalReader.MaxEntries;        // 77
  private const int DirectoryOffset = ApplePascalReader.DirectoryOffset; // 0x400
  private const int FirstDataBlock = 6;                               // by spec
  private const int MaxNameLength = 15;

  // ── Public API ──────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a single file to the existing image. If a file with the same name
  /// already exists, it is removed first (its extent zero-wiped and its
  /// directory entry shifted out) so the new entry replaces it cleanly.
  /// <para>The new file's contiguous extent is allocated by scanning every
  /// live directory entry to derive the occupied-block map; the first free
  /// run of the required size at or after block 6 is chosen.</para>
  /// </summary>
  /// <exception cref="NotSupportedException">Directory full (77 entries already
  /// in use). Subdirectory emission is out of scope — Apple Pascal does not
  /// support nested directories.</exception>
  /// <exception cref="IOException">Volume full (no contiguous free run of the
  /// required size at or after block 6).</exception>
  /// <exception cref="ArgumentException">File name resolves to empty after
  /// path flattening.</exception>
  public static void AddFile(Stream image, string name, byte[] data, int kind = 0) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new IOException("Apple Pascal modifier requires a readable, writable, seekable stream.");

    var leaf = NormalizeShortName(name);
    if (string.IsNullOrEmpty(leaf))
      throw new ArgumentException("Apple Pascal: file name resolves to empty after flattening.", nameof(name));

    // Replacement: remove the existing entry first so the slot + extent are
    // recycled. Matches the QNX4 / MFS modifier pattern.
    RemoveFile(image, leaf, wipeData: true);

    var dir = ReadDirectory(image);
    var totalBlocks = (int)BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(14, 2));
    var fileCount = (int)BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(16, 2));
    if (fileCount >= MaxEntries)
      throw new NotSupportedException(
        $"Apple Pascal: directory full (max {MaxEntries} entries). Subdirectory emission is out of " +
        "scope — Apple Pascal does not support nested directories by spec.");

    // Compute occupied block ranges from the live directory entries.
    var occupied = new List<(int Start, int End)>();
    for (var i = 0; i < fileCount; i++) {
      var entryOff = EntrySize + i * EntrySize;
      var startBlock = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(entryOff, 2));
      var endBlock = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(entryOff + 2, 2));
      if (endBlock > startBlock)
        occupied.Add((startBlock, endBlock));
    }

    // Files always reserve at least one block — even zero-byte files — so the
    // reader's extent walker has something to follow and `bytesInLastBlock`
    // can be a sane 1..512.
    var blocksNeeded = data.Length == 0
      ? 1
      : (data.Length + BlockSize - 1) / BlockSize;

    var startBlockNew = FindFreeContiguousExtent(occupied, totalBlocks, blocksNeeded)
      ?? throw new IOException(
        $"Apple Pascal: volume full — cannot allocate {blocksNeeded} contiguous block(s) for '{leaf}'.");
    var endBlockNew = startBlockNew + blocksNeeded;

    // Build the 26-byte directory entry.
    var entry = new byte[EntrySize];
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(0, 2), (ushort)startBlockNew);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2, 2), (ushort)endBlockNew);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4, 2), (ushort)kind);
    entry[6] = (byte)leaf.Length;
    Encoding.ASCII.GetBytes(leaf).CopyTo(entry.AsSpan(7));
    var bytesInLast = data.Length == 0 ? BlockSize : data.Length - (blocksNeeded - 1) * BlockSize;
    if (bytesInLast <= 0) bytesInLast = BlockSize;
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(22, 2), (ushort)bytesInLast);
    // Date at +24 stays zero (matches WORM writer).

    // Insert the entry at the tail of the live region — entries are not sorted
    // by spec, so appending preserves existing on-disk order.
    var newEntryOff = EntrySize + fileCount * EntrySize;
    entry.CopyTo(dir.AsSpan(newEntryOff, EntrySize));

    // Update the volume header's file count.
    BinaryPrimitives.WriteUInt16LittleEndian(dir.AsSpan(16, 2), (ushort)(fileCount + 1));
    // `nextBlock` (first to access cached, +2) and `firstToAccess` (+18) reflect
    // where reading should start — convention has them pointing at the first
    // file. Leave them untouched: the WORM writer sets them to 6 and that holds
    // for every Apple-Pascal-conforming volume since data always starts at 6.

    WriteDirectory(image, dir);

    // Copy payload into the file's contiguous extent, zero the tail of the
    // last block so cluster-tip slack stays clean.
    if (data.Length > 0) {
      image.Position = (long)startBlockNew * BlockSize;
      image.Write(data);
    }
    var tail = blocksNeeded * BlockSize - data.Length;
    if (tail > 0) {
      image.Position = (long)startBlockNew * BlockSize + data.Length;
      image.Write(new byte[tail]);
    }
  }

  /// <summary>
  /// Removes the named file from the existing image. Zero-wipes the file's
  /// contiguous extent, shifts the trailing directory entries up to keep the
  /// live region packed (Apple Pascal walks entries 0..fileCount and stops —
  /// holes would orphan everything after), and decrements the file count.
  /// </summary>
  /// <returns>True if the file was found and removed, false otherwise.</returns>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new IOException("Apple Pascal modifier requires a readable, writable, seekable stream.");

    var leaf = NormalizeShortName(name);
    if (string.IsNullOrEmpty(leaf)) return false;

    var dir = ReadDirectory(image);
    var fileCount = (int)BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(16, 2));

    for (var i = 0; i < fileCount; i++) {
      var entryOff = EntrySize + i * EntrySize;
      var nameLen = dir[entryOff + 6];
      if (nameLen < 1 || nameLen > MaxNameLength) continue;
      var entryName = Encoding.ASCII.GetString(dir.AsSpan(entryOff + 7, nameLen));
      if (!string.Equals(entryName, leaf, StringComparison.Ordinal)) continue;

      var startBlock = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(entryOff, 2));
      var endBlock = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(entryOff + 2, 2));

      // Zero-wipe the data extent so no forensic recovery is possible.
      if (wipeData && endBlock > startBlock) {
        var extentBytes = (long)(endBlock - startBlock) * BlockSize;
        var dataOff = (long)startBlock * BlockSize;
        if (dataOff + extentBytes <= image.Length) {
          image.Position = dataOff;
          image.Write(new byte[extentBytes]);
        }
      }

      // Shift the trailing entries up to keep the live region packed.
      var tailStart = entryOff + EntrySize;
      var tailEnd = EntrySize + fileCount * EntrySize;
      var tailBytes = tailEnd - tailStart;
      if (tailBytes > 0)
        Buffer.BlockCopy(dir, tailStart, dir, entryOff, tailBytes);

      // Zero the now-empty trailing slot so no stale dirent leaks.
      var freedSlotOff = EntrySize + (fileCount - 1) * EntrySize;
      Array.Clear(dir, freedSlotOff, EntrySize);

      // Decrement the file count in the volume header.
      BinaryPrimitives.WriteUInt16LittleEndian(dir.AsSpan(16, 2), (ushort)(fileCount - 1));

      WriteDirectory(image, dir);
      return true;
    }
    return false;
  }

  /// <summary>
  /// Replaces the data of an existing file in place when the new payload fits
  /// inside the file's currently allocated extent. Returns false when the file
  /// is missing or the new payload exceeds the existing extent — the caller
  /// can fall back to <see cref="RemoveFile"/> + <see cref="AddFile"/> in that
  /// case. The directory entry's <c>bytesInLastBlock</c> field is updated so
  /// the reader reports the new logical size correctly.
  /// </summary>
  public static bool ReplaceFileIfFits(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new IOException("Apple Pascal modifier requires a readable, writable, seekable stream.");

    var leaf = NormalizeShortName(name);
    if (string.IsNullOrEmpty(leaf)) return false;

    var dir = ReadDirectory(image);
    var fileCount = (int)BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(16, 2));

    for (var i = 0; i < fileCount; i++) {
      var entryOff = EntrySize + i * EntrySize;
      var nameLen = dir[entryOff + 6];
      if (nameLen < 1 || nameLen > MaxNameLength) continue;
      var entryName = Encoding.ASCII.GetString(dir.AsSpan(entryOff + 7, nameLen));
      if (!string.Equals(entryName, leaf, StringComparison.Ordinal)) continue;

      var startBlock = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(entryOff, 2));
      var endBlock = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(entryOff + 2, 2));
      var extentBlocks = endBlock - startBlock;
      var blocksNeeded = data.Length == 0 ? 1 : (data.Length + BlockSize - 1) / BlockSize;
      if (blocksNeeded > extentBlocks) return false; // doesn't fit, caller falls back

      // Write the new payload + zero the slack so the cluster tip stays clean.
      var extentBytes = (long)extentBlocks * BlockSize;
      var dataOff = (long)startBlock * BlockSize;
      if (dataOff + extentBytes > image.Length) return false;

      image.Position = dataOff;
      if (data.Length > 0)
        image.Write(data);
      var tail = (int)(extentBytes - data.Length);
      if (tail > 0) {
        image.Position = dataOff + data.Length;
        image.Write(new byte[tail]);
      }

      // Update bytes-in-last-block in the directory entry.
      var bytesInLast = data.Length == 0 ? BlockSize : data.Length - (blocksNeeded - 1) * BlockSize;
      if (bytesInLast <= 0) bytesInLast = BlockSize;
      BinaryPrimitives.WriteUInt16LittleEndian(dir.AsSpan(entryOff + 22, 2), (ushort)bytesInLast);

      WriteDirectory(image, dir);
      return true;
    }
    return false;
  }

  // ── Directory I/O ──────────────────────────────────────────────────────

  /// <summary>
  /// Reads the 2048-byte volume directory region (blocks 2-5). The modifier
  /// works on a single in-memory copy and flushes it back via
  /// <see cref="WriteDirectory"/> after every mutation — the region is small
  /// (4 KB max) so the copy cost is negligible.
  /// </summary>
  private static byte[] ReadDirectory(Stream image) {
    const int DirBytes = 4 * BlockSize; // blocks 2..5 = 2048 bytes
    if (image.Length < DirectoryOffset + DirBytes)
      throw new IOException(
        $"Apple Pascal modifier: image too small for volume directory ({image.Length} < {DirectoryOffset + DirBytes}).");
    var buf = new byte[DirBytes];
    image.Position = DirectoryOffset;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteDirectory(Stream image, byte[] dir) {
    image.Position = DirectoryOffset;
    image.Write(dir);
  }

  // ── Allocation helpers ─────────────────────────────────────────────────

  /// <summary>
  /// Finds the lowest-block-index contiguous run of <paramref name="needed"/>
  /// free blocks in <c>[FirstDataBlock, totalBlocks)</c>. The occupied list
  /// is the union of every live directory entry's extent; gaps between
  /// extents are valid free runs. Used for both fresh appends and slot reuse
  /// after removes — the simple sort-and-scan is O(N log N) for N ≤ 77.
  /// </summary>
  private static int? FindFreeContiguousExtent(List<(int Start, int End)> occupied, int totalBlocks, int needed) {
    if (needed <= 0) needed = 1;
    if (totalBlocks <= FirstDataBlock) return null;

    // Sort by start block; coalesce overlapping/adjacent ranges so the gap
    // walk is straightforward.
    var sorted = occupied.OrderBy(o => o.Start).ToList();
    var cursor = FirstDataBlock;
    foreach (var (start, end) in sorted) {
      if (start >= cursor + needed) return cursor;     // gap before this extent fits
      if (end > cursor) cursor = end;                  // advance past the extent
    }
    // Tail after the last live extent.
    if (cursor + needed <= totalBlocks) return cursor;
    return null;
  }

  // ── Name handling ──────────────────────────────────────────────────────

  /// <summary>
  /// Flattens path-style names down to the leaf (Apple Pascal R/W writer does
  /// not emit subdirs — same scope as the WORM writer) and truncates to the
  /// 15-byte short-name slot. Uppercases per Apple Pascal convention so the
  /// on-disk name matches what <see cref="ApplePascalWriter.AddFile"/> would
  /// have written.
  /// </summary>
  private static string NormalizeShortName(string name) {
    var leaf = name.Replace('\\', '/');
    var slash = leaf.LastIndexOf('/');
    if (slash >= 0) leaf = leaf[(slash + 1)..];
    if (string.IsNullOrEmpty(leaf)) return "";
    if (leaf.Length > MaxNameLength) leaf = leaf[..MaxNameLength];
    return leaf.ToUpperInvariant();
  }
}
