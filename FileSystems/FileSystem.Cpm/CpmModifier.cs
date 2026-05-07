#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Cpm;

/// <summary>
/// True random-access in-place modifier for CP/M 2.2 disk images using the
/// 8" SSSD reference geometry. Performs add / remove on an existing image with
/// <b>O(touched bytes)</b> I/O — reads only the directory area (2 KB) once to
/// learn which blocks are in use (CP/M tracks block usage <i>implicitly</i> via
/// the union of directory-entry block lists; there's no separate bitmap), then
/// writes only the affected directory entries plus the file's data blocks.
///
/// <para>CP/M 2.2 uses 8-bit block numbers when the disk has ≤ 256 allocation
/// blocks (our reference 243-block geometry qualifies); larger DPBs use
/// 16-bit pointers, which this modifier does not currently emit. Each
/// directory entry tracks 16 block pointers ⇒ a single extent covers up to
/// 16 KB of file data; larger files fan out across additional directory
/// entries (extents) keyed by <c>(userCode, name.ext)</c> with the extent
/// counter spliced across <c>S1 (entry[12])</c> and <c>S2 (entry[14])</c>.</para>
///
/// <para>Companion <see cref="CpmWriter"/> rebuilds an image from scratch;
/// this class is for the "I have an existing image, mutate it" path that
/// <c>IArchiveModifiable</c> exposes. Multi-extent files are handled by both
/// <see cref="AddFile"/> (allocates as many directory slots as needed) and
/// <see cref="RemoveFile"/> (walks every <c>(user,name,ext)</c>-matching entry
/// and frees its blocks).</para>
/// </summary>
public static class CpmModifier {

  /// <summary>
  /// Adds a file to the existing CP/M image. Performs in-place modification:
  /// scans the directory area (2 KB) to discover free blocks, allocates the
  /// required number of 1024-byte data blocks, fills directory entries
  /// (one per 16 KB extent), and writes the data. Bytes touched: 2 KB
  /// directory read + ⌈len/1024⌉ × 1024 data writes + ⌈extents⌉ × 32-byte
  /// directory writes.
  /// </summary>
  /// <param name="image">CP/M image stream — must be at least 256 256 bytes.</param>
  /// <param name="name">8.3-style filename ("HELLO.TXT"). Will be upper-cased.</param>
  /// <param name="data">File contents. May be empty.</param>
  /// <param name="userCode">CP/M user area (0..15). Defaults to 0.</param>
  /// <exception cref="InvalidOperationException">Disk full (not enough free blocks)
  /// or directory full (not enough free entry slots for the required extents).</exception>
  /// <exception cref="ArgumentException">Filename stem &gt; 8 chars or extension &gt; 3 chars.</exception>
  public static void AddFile(Stream image, string name, byte[] data, byte userCode = 0) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (userCode > 0x1F) throw new ArgumentOutOfRangeException(nameof(userCode), "CP/M user codes are 0..31.");

    var (baseName, ext) = SplitName(name);
    if (baseName.Length > 8) throw new ArgumentException("CP/M filename stem exceeds 8 characters.", nameof(name));
    if (ext.Length > 3) throw new ArgumentException("CP/M extension exceeds 3 characters.", nameof(name));

    // 1. Read the entire directory area (2 KB) — this is our metadata working set.
    var directory = ReadDirectory(image);

    // 2. Compute block layout for the new file.
    var totalRecords = (int)Math.Ceiling(data.Length / (double)CpmLayout.SectorSize);
    var totalBlocks = (int)Math.Ceiling(data.Length / (double)CpmLayout.BlockSize);
    if (totalBlocks == 0) totalBlocks = 1; // empty file still gets one block (matches writer)

    var extentsNeeded = (totalBlocks + CpmLayout.BlocksPerExtent - 1) / CpmLayout.BlocksPerExtent;
    if (extentsNeeded == 0) extentsNeeded = 1;

    // 3. Discover which blocks are already allocated across all live directory entries.
    var inUse = ScanAllocatedBlocks(directory);

    // 4. Pick `totalBlocks` free data blocks (block index >= DataBlockStart, < TotalBlocks).
    var allocated = new int[totalBlocks];
    var allocatedCount = 0;
    for (var b = CpmLayout.DataBlockStart; b < CpmLayout.TotalBlocks && allocatedCount < totalBlocks; b++) {
      if (inUse.Contains(b)) continue;
      allocated[allocatedCount++] = b;
    }
    if (allocatedCount < totalBlocks)
      throw new InvalidOperationException(
        $"CP/M: disk full — needed {totalBlocks} free blocks, found {allocatedCount}.");

    // 5. Find `extentsNeeded` free directory slots.
    var freeSlots = new int[extentsNeeded];
    var freeSlotCount = 0;
    for (var i = 0; i < CpmLayout.DirectoryEntries && freeSlotCount < extentsNeeded; i++) {
      if (directory[i * CpmLayout.DirectoryEntrySize] == CpmLayout.EmptyEntryUserCode)
        freeSlots[freeSlotCount++] = i;
    }
    if (freeSlotCount < extentsNeeded)
      throw new InvalidOperationException(
        $"CP/M: directory full — needed {extentsNeeded} free entries, found {freeSlotCount}.");

    // 6. Write data blocks (only the blocks we touch).
    for (var b = 0; b < totalBlocks; b++) {
      var blkOffset = CpmLayout.ReservedBytes + allocated[b] * CpmLayout.BlockSize;
      var srcStart = b * CpmLayout.BlockSize;
      var srcLen = Math.Min(CpmLayout.BlockSize, data.Length - srcStart);
      var blockBuf = new byte[CpmLayout.BlockSize];
      if (srcLen > 0)
        Buffer.BlockCopy(data, srcStart, blockBuf, 0, srcLen);
      image.Position = blkOffset;
      image.Write(blockBuf);
    }

    // 7. Build and write each extent's directory entry (only touched 32-byte slices).
    for (var e = 0; e < extentsNeeded; e++) {
      var entry = new byte[CpmLayout.DirectoryEntrySize];
      entry[0] = userCode;
      WriteAsciiField(entry, 1, baseName, 8);
      WriteAsciiField(entry, 9, ext, 3);
      var extNum = e;
      entry[12] = (byte)(extNum & 0x1F);
      entry[13] = 0;
      entry[14] = (byte)((extNum >> 5) & 0x3F);

      var isLast = e == extentsNeeded - 1;
      var blocksInThis = isLast
        ? totalBlocks - e * CpmLayout.BlocksPerExtent
        : CpmLayout.BlocksPerExtent;
      var recordsInThis = isLast
        ? totalRecords - e * CpmLayout.RecordsPerExtent
        : CpmLayout.RecordsPerExtent;
      if (recordsInThis > CpmLayout.RecordsPerExtent) recordsInThis = CpmLayout.RecordsPerExtent;
      if (recordsInThis < 0) recordsInThis = 0;
      entry[15] = (byte)recordsInThis;

      for (var b = 0; b < blocksInThis; b++)
        entry[16 + b] = (byte)allocated[e * CpmLayout.BlocksPerExtent + b];
      // Remaining block-pointer bytes stay zero.

      var slot = freeSlots[e];
      var slotOffset = CpmLayout.ReservedBytes + slot * CpmLayout.DirectoryEntrySize;
      image.Position = slotOffset;
      image.Write(entry);
    }
  }

  /// <summary>
  /// Removes the named file from the existing CP/M image. Walks the directory
  /// to find every extent matching <c>(userCode, name, ext)</c>, marks each as
  /// deleted by setting the user-code byte to <c>0xE5</c>, optionally wipes
  /// the data blocks. Returns true if at least one extent was found.
  /// Bytes touched: 2 KB directory read + N × 32-byte directory writes +
  /// (optional) N × 1024-byte block writes.
  /// </summary>
  /// <param name="image">CP/M image stream.</param>
  /// <param name="name">Filename (e.g. "HELLO.TXT").</param>
  /// <param name="userCode">User code to match, or null to match any (0..31).</param>
  /// <param name="wipeData">When true (default), data blocks are zeroed for forensic safety.</param>
  public static bool RemoveFile(Stream image, string name, byte? userCode = null, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var (baseName, ext) = SplitName(name);
    var directory = ReadDirectory(image);
    var blocksToWipe = new List<int>();
    var slotsToDelete = new List<int>();

    for (var i = 0; i < CpmLayout.DirectoryEntries; i++) {
      var entryOff = i * CpmLayout.DirectoryEntrySize;
      var u = directory[entryOff];
      if (u == CpmLayout.EmptyEntryUserCode) continue;
      if (u > 0x1F) continue;
      if (userCode is byte uc && u != uc) continue;

      // Decode and compare name (strip high bit; trim spaces).
      var entryNameBytes = new byte[8];
      var entryExtBytes = new byte[3];
      for (var k = 0; k < 8; k++) entryNameBytes[k] = (byte)(directory[entryOff + 1 + k] & 0x7F);
      for (var k = 0; k < 3; k++) entryExtBytes[k] = (byte)(directory[entryOff + 9 + k] & 0x7F);
      var entryName = Encoding.ASCII.GetString(entryNameBytes).TrimEnd(' ');
      var entryExt = Encoding.ASCII.GetString(entryExtBytes).TrimEnd(' ');
      if (!string.Equals(entryName, baseName, StringComparison.Ordinal)) continue;
      if (!string.Equals(entryExt, ext, StringComparison.Ordinal)) continue;

      // Collect block numbers for wiping.
      for (var b = 0; b < CpmLayout.BlocksPerExtent; b++) {
        var blk = directory[entryOff + 16 + b];
        if (blk == 0) continue;
        if (blk < CpmLayout.DataBlockStart || blk >= CpmLayout.TotalBlocks) continue;
        blocksToWipe.Add(blk);
      }
      slotsToDelete.Add(i);
    }

    if (slotsToDelete.Count == 0) return false;

    // Wipe data blocks if requested (each is 1024 bytes — only touched blocks).
    if (wipeData) {
      var zero = new byte[CpmLayout.BlockSize];
      foreach (var blk in blocksToWipe) {
        image.Position = CpmLayout.ReservedBytes + blk * CpmLayout.BlockSize;
        image.Write(zero);
      }
    }

    // Mark each affected directory slot as empty. The CP/M empty-entry convention
    // sets the entire 32 bytes to 0xE5 (matching freshly-formatted directory area
    // produced by CpmWriter and the BDOS itself).
    var deletedEntry = new byte[CpmLayout.DirectoryEntrySize];
    Array.Fill(deletedEntry, CpmLayout.EmptyEntryUserCode);
    foreach (var slot in slotsToDelete) {
      var slotOffset = CpmLayout.ReservedBytes + slot * CpmLayout.DirectoryEntrySize;
      image.Position = slotOffset;
      image.Write(deletedEntry);
    }

    return true;
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  /// <summary>
  /// Reads the 2 KB directory area in a single seek+read. CP/M has no separate
  /// allocation bitmap — block usage is derived from the union of live entries —
  /// so the directory <i>is</i> the metadata.
  /// </summary>
  private static byte[] ReadDirectory(Stream image) {
    var buf = new byte[CpmLayout.DirectoryBytes];
    image.Position = CpmLayout.ReservedBytes;
    var read = 0;
    while (read < buf.Length) {
      var n = image.Read(buf, read, buf.Length - read);
      if (n <= 0) throw new EndOfStreamException("CP/M: directory area truncated.");
      read += n;
    }
    return buf;
  }

  /// <summary>
  /// Walks every live directory entry, collecting the union of block pointers
  /// referenced by any extent. Returns the set of allocated block indices.
  /// </summary>
  private static HashSet<int> ScanAllocatedBlocks(byte[] directory) {
    var inUse = new HashSet<int>();
    for (var i = 0; i < CpmLayout.DirectoryEntries; i++) {
      var entryOff = i * CpmLayout.DirectoryEntrySize;
      var u = directory[entryOff];
      if (u == CpmLayout.EmptyEntryUserCode) continue;
      if (u > 0x1F) continue;
      for (var b = 0; b < CpmLayout.BlocksPerExtent; b++) {
        var blk = directory[entryOff + 16 + b];
        if (blk == 0) continue;
        inUse.Add(blk);
      }
    }
    return inUse;
  }

  private static (string Name, string Ext) SplitName(string fullName) {
    var file = Path.GetFileName(fullName);
    var dot = file.LastIndexOf('.');
    if (dot < 0) return (Truncate(file, 8).ToUpperInvariant(), "");
    var name = Truncate(file[..dot], 8).ToUpperInvariant();
    var ext = Truncate(file[(dot + 1)..], 3).ToUpperInvariant();
    return (name, ext);
  }

  private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

  private static void WriteAsciiField(byte[] dest, int offset, string s, int width) {
    var bytes = Encoding.ASCII.GetBytes(s);
    for (var i = 0; i < width; i++) {
      var b = i < bytes.Length ? bytes[i] : (byte)' ';
      if (b < 0x20 || b > 0x7E) b = (byte)'_';
      if (b is (byte)'<' or (byte)'>' or (byte)'.' or (byte)',' or (byte)';'
            or (byte)':' or (byte)'=' or (byte)'?' or (byte)'*' or (byte)'[' or (byte)']')
        b = (byte)'_';
      dest[offset + i] = b;
    }
  }
}
