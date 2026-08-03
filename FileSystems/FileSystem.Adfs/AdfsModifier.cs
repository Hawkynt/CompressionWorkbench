#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Adfs;

/// <summary>
/// In-place R/W modifier for Acorn ADFS-L (old-map, 256-byte sectors, 640 KiB)
/// images. Performs Add / Remove against the existing on-disk Hugo-bracketed
/// root directory and the two-sector free-space map (FSM).
/// </summary>
/// <remarks>
/// <para>
/// The modifier operates on the same canonical layout emitted by
/// <see cref="AdfsWriter"/>: sectors 0+1 are the old-map FSM (start sectors
/// in sector 0, fragment lengths in sector 1, trailers at 0xFB-0xFF with the
/// rotate-and-add check byte at 0xFF), sectors 2-6 are the 1280-byte
/// "Hugo"-bracketed root directory (up to 47 entries of 26 bytes each
/// starting at offset 5, with a XOR check byte at 0x4FD), and sectors 7..N
/// hold file data. Both the FSM and root directory's check bytes are
/// recomputed after every mutation so the resulting image round-trips
/// cleanly through <see cref="AdfsReader"/>.
/// </para>
/// <para>
/// Scope: ADFS-L only (640 KiB, 80-track double-sided floppy). ADFS-S/M
/// (single-sided variants) use the same on-disk shape and would work with
/// minor adjustments, but the descriptor only emits ADFS-L from
/// <c>IArchiveCreatable.Create</c>, so the modifier matches that footprint.
/// The 47-entry root-directory cap is the natural limit for in-place mutation
/// — subdirectories are out of scope because the writer never emits them.
/// </para>
/// </remarks>
public static class AdfsModifier {

  private const int SectorSize = AdfsWriter.SectorSize;       // 256
  private const int RootDirSector = 2;
  private const int RootDirOffset = RootDirSector * SectorSize; // 0x200
  private const int DirectorySize = 1280;                       // 5 sectors
  private const int DirectorySectors = DirectorySize / SectorSize; // 5
  private const int FirstDataSector = RootDirSector + DirectorySectors; // 7
  private const int MaxDirEntries = 47;
  private const int DirEntrySize = 26;
  private const int DirEntriesOffset = 5;                       // after "Hugo"
  private const int DirEndMagicOffset = 0x4CB;                  // trailing "Hugo"
  private const int DirCheckByteOffset = 0x4FD;
  private const int FsmFreeEndOffset = 0xFE;                    // end-pointer byte
  private const int FsmCheckByteOffset = 0xFF;
  private const int FsmEntryStride = 3;                         // 3-byte fields
  private const int FsmMaxEntries = 0xFE / FsmEntryStride;      // 82 free regions

  /// <summary>
  /// Adds a single file (no path component — root-level only) to an existing
  /// ADFS-L image. The image stream must be seekable, writable, and exactly
  /// <see cref="AdfsWriter.DiskSizeL"/> bytes long. If the name already exists
  /// the caller should <see cref="RemoveFile"/> first; this method does not
  /// dedupe (matches AppleDOS modifier semantics).
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) =>
    AddFile(image, name, data, loadAddr: 0x0000FFFFu, execAddr: 0x0000FFFFu, attrs: 0);

  /// <summary>
  /// Adds a file with explicit load/exec addresses and attribute byte.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data, uint loadAddr, uint execAddr, byte attrs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    EnsureSeekableImage(image);

    var sanitized = SanitizeName(name);
    var sectorsNeeded = data.Length == 0 ? 0 : (data.Length + SectorSize - 1) / SectorSize;

    // Read FSM and root directory.
    var fsm0 = ReadSector(image, 0);
    var fsm1 = ReadSector(image, 1);
    var dir = ReadDirectory(image);

    var entryCount = CountDirectoryEntries(dir);
    if (entryCount >= MaxDirEntries)
      throw new InvalidOperationException(
        $"ADFS-L: root directory full ({MaxDirEntries} entries — old-map cap).");

    var free = DecodeFsm(fsm0, fsm1);
    uint startSector;
    if (sectorsNeeded == 0) {
      // Zero-byte file: don't allocate; point at first data sector for compatibility.
      startSector = FirstDataSector;
    } else {
      startSector = AllocateFirstFit(free, (uint)sectorsNeeded)
        ?? throw new InvalidOperationException(
          $"ADFS-L: no contiguous free region for {sectorsNeeded} sectors ({data.Length} bytes).");
    }

    // Write file data into the allocated sectors.
    if (data.Length > 0) {
      // We don't zero-pad the tail sector: round-trip integrity comes from
      // the directory entry's length field, not the disk bytes past that.
      // But for parity with the writer (which zero-inits the whole disc),
      // pad the last sector explicitly so freshly-allocated space never
      // exposes whatever was there before (slack from a prior file).
      WriteRange(image, (long)startSector * SectorSize, data);
      var tail = data.Length % SectorSize;
      if (tail != 0) {
        var pad = new byte[SectorSize - tail];
        WriteRange(image, (long)startSector * SectorSize + data.Length, pad);
      }
    }

    // Insert directory entry (append at end — reader doesn't care about order).
    var entryIdx = entryCount;
    var entryOff = DirEntriesOffset + entryIdx * DirEntrySize;
    var entry = dir.AsSpan(entryOff, DirEntrySize);
    entry.Clear();
    EncodeNameAndAttrs(entry[..10], sanitized, attrs);
    BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(0x0A, 4), loadAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(0x0E, 4), execAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(0x12, 4), (uint)data.Length);
    WriteUInt24LittleEndian(entry.Slice(0x16, 3), startSector);
    entry[0x19] = 0;

    // Re-emit FSM from the modified free list, recompute check bytes, write back.
    EncodeFsm(fsm0, fsm1, free);
    RecomputeDirectoryCheckByte(dir);
    fsm0[FsmCheckByteOffset] = ComputeOldMapCheckByte(fsm0);
    fsm1[FsmCheckByteOffset] = ComputeOldMapCheckByte(fsm1);

    WriteSector(image, 0, fsm0);
    WriteSector(image, 1, fsm1);
    WriteDirectory(image, dir);
  }

  /// <summary>
  /// Removes a named file from an existing ADFS-L image. Returns <c>true</c>
  /// if a matching entry was found and removed. The file's data sectors are
  /// wiped (zeroed) and returned to the FSM with adjacent-region merging so
  /// the free list stays compact.
  /// </summary>
  public static bool RemoveFile(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    EnsureSeekableImage(image);

    var sanitized = SanitizeName(name);
    var fsm0 = ReadSector(image, 0);
    var fsm1 = ReadSector(image, 1);
    var dir = ReadDirectory(image);

    var (foundIdx, entryCount) = FindEntry(dir, sanitized);
    if (foundIdx < 0) return false;

    var entryOff = DirEntriesOffset + foundIdx * DirEntrySize;
    var length = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(entryOff + 0x12, 4));
    var startSector = ReadUInt24LittleEndian(dir.AsSpan(entryOff + 0x16, 3));
    var sectorsUsed = length == 0 ? 0u : (length + SectorSize - 1) / SectorSize;

    // Wipe the file's data sectors (no forensic recovery from the slack).
    if (sectorsUsed > 0) {
      var blank = new byte[SectorSize * (int)sectorsUsed];
      WriteRange(image, (long)startSector * SectorSize, blank);
    }

    // Return the sectors to the FSM (merge with adjacent free regions).
    var free = DecodeFsm(fsm0, fsm1);
    if (sectorsUsed > 0)
      ReturnRegionToFreeList(free, startSector, sectorsUsed);

    // Shift later entries down by one slot to fill the hole; zero the trailing slot
    // so the end-of-directory sentinel (first byte of next entry = 0) re-engages.
    for (var i = foundIdx; i < entryCount - 1; i++) {
      var src = DirEntriesOffset + (i + 1) * DirEntrySize;
      var dst = DirEntriesOffset + i * DirEntrySize;
      Array.Copy(dir, src, dir, dst, DirEntrySize);
    }
    var lastOff = DirEntriesOffset + (entryCount - 1) * DirEntrySize;
    Array.Clear(dir, lastOff, DirEntrySize);

    EncodeFsm(fsm0, fsm1, free);
    RecomputeDirectoryCheckByte(dir);
    fsm0[FsmCheckByteOffset] = ComputeOldMapCheckByte(fsm0);
    fsm1[FsmCheckByteOffset] = ComputeOldMapCheckByte(fsm1);

    WriteSector(image, 0, fsm0);
    WriteSector(image, 1, fsm1);
    WriteDirectory(image, dir);
    return true;
  }

  // ── Free-space map decode / encode ────────────────────────────────────

  /// <summary>
  /// A single free region (start sector, length in sectors). Encoded as a
  /// 3-byte start in sector 0 and a matching 3-byte length in sector 1.
  /// </summary>
  internal readonly record struct FreeRegion(uint Start, uint Length) {
    public uint End => this.Start + this.Length;
  }

  /// <summary>
  /// Decodes the FSM into the in-memory free-region list. The end pointer
  /// (sector 0 byte 0xFE) gives the total bytes consumed by entries; we walk
  /// 3 bytes at a time and pair each (sector 0 start) with the matching
  /// (sector 1 length). Regions with zero length are dropped.
  /// </summary>
  internal static List<FreeRegion> DecodeFsm(byte[] fsm0, byte[] fsm1) {
    var freeEnd = fsm0[FsmFreeEndOffset];
    var list = new List<FreeRegion>();
    for (var off = 0; off + FsmEntryStride <= freeEnd; off += FsmEntryStride) {
      var start = ReadUInt24LittleEndian(fsm0.AsSpan(off, 3));
      var len = ReadUInt24LittleEndian(fsm1.AsSpan(off, 3));
      if (len == 0) continue;
      list.Add(new FreeRegion(start, len));
    }
    list.Sort(static (a, b) => a.Start.CompareTo(b.Start));
    return list;
  }

  /// <summary>
  /// Re-emits the free-region list into the two FSM sectors, preserving the
  /// existing trailer bytes (disc id at 0xFB-0xFC, boot opt at sector 1
  /// 0xFD), updating only the entry list, the end pointer, and the check
  /// byte (the latter is the caller's responsibility, recomputed last).
  /// </summary>
  internal static void EncodeFsm(byte[] fsm0, byte[] fsm1, List<FreeRegion> free) {
    if (free.Count > FsmMaxEntries)
      throw new InvalidOperationException(
        $"ADFS-L FSM: too many free regions ({free.Count}) — old map caps at {FsmMaxEntries}.");
    // Clear the entry area only (bytes 0..0xFA), preserve the trailer.
    Array.Clear(fsm0, 0, FsmFreeEndOffset - 3);
    Array.Clear(fsm1, 0, FsmFreeEndOffset - 3);
    for (var i = 0; i < free.Count; i++) {
      var off = i * FsmEntryStride;
      WriteUInt24LittleEndian(fsm0.AsSpan(off, 3), free[i].Start);
      WriteUInt24LittleEndian(fsm1.AsSpan(off, 3), free[i].Length);
    }
    var endPtr = (byte)(free.Count * FsmEntryStride);
    fsm0[FsmFreeEndOffset] = endPtr;
    fsm1[FsmFreeEndOffset] = endPtr;
  }

  /// <summary>
  /// First-fit allocation from the free-region list. Removes the allocated
  /// run from the list (or shrinks the host region when partial), returning
  /// the allocated start sector.
  /// </summary>
  internal static uint? AllocateFirstFit(List<FreeRegion> free, uint sectors) {
    for (var i = 0; i < free.Count; i++) {
      if (free[i].Length < sectors) continue;
      var start = free[i].Start;
      if (free[i].Length == sectors) {
        free.RemoveAt(i);
      } else {
        free[i] = new FreeRegion(start + sectors, free[i].Length - sectors);
      }
      return start;
    }
    return null;
  }

  /// <summary>
  /// Returns a sector run to the free list, merging with adjacent regions
  /// (both predecessor and successor) so the FSM stays compact and the
  /// 82-entry cap is hard to hit through ordinary use.
  /// </summary>
  internal static void ReturnRegionToFreeList(List<FreeRegion> free, uint start, uint length) {
    var end = start + length;
    // Find the insertion point (first region whose Start > new start).
    var idx = 0;
    while (idx < free.Count && free[idx].Start < start) idx++;

    // Merge with predecessor if adjacent.
    if (idx > 0 && free[idx - 1].End == start) {
      start = free[idx - 1].Start;
      length = end - start;
      free.RemoveAt(idx - 1);
      idx--;
    }
    // Merge with successor if adjacent.
    if (idx < free.Count && end == free[idx].Start) {
      length = (free[idx].Start + free[idx].Length) - start;
      free.RemoveAt(idx);
    }
    free.Insert(idx, new FreeRegion(start, length));
  }

  // ── Directory navigation ─────────────────────────────────────────────

  internal static int CountDirectoryEntries(byte[] dir) {
    for (var i = 0; i < MaxDirEntries; i++) {
      var off = DirEntriesOffset + i * DirEntrySize;
      if ((dir[off] & 0x7F) == 0) return i;
    }
    return MaxDirEntries;
  }

  private static (int Index, int Count) FindEntry(byte[] dir, string name) {
    var count = CountDirectoryEntries(dir);
    for (var i = 0; i < count; i++) {
      var off = DirEntriesOffset + i * DirEntrySize;
      var (entryName, _) = DecodeNameAndAttrs(dir.AsSpan(off, 10));
      if (string.Equals(entryName, name, StringComparison.Ordinal))
        return (i, count);
    }
    return (-1, count);
  }

  // ── Check-byte algorithms (must match the writer + the Linux driver) ──

  /// <summary>
  /// Rotate-and-add (with carry-fold) over bytes 0..0xFE; stored at 0xFF.
  /// Matches <see cref="AdfsWriter.ComputeOldMapCheckByte"/>.
  /// </summary>
  internal static byte ComputeOldMapCheckByte(ReadOnlySpan<byte> sector) {
    uint sum = 0;
    for (var i = 0; i < 255; i++) {
      sum += sector[i];
      if (sum > 0xFF)
        sum = (sum + 1) & 0xFF;
    }
    return (byte)sum;
  }

  /// <summary>
  /// Recomputes the directory check byte at offset 0x4FD: XOR over the first
  /// 0x4FD bytes. Matches the writer's <c>WriteRootDirectory</c> tail.
  /// </summary>
  internal static void RecomputeDirectoryCheckByte(byte[] dir) {
    byte check = 0;
    for (var i = 0; i < DirCheckByteOffset; i++) check ^= dir[i];
    dir[DirCheckByteOffset] = check;
  }

  // ── Name + attribute codec (mirrors AdfsWriter / AdfsReader) ──────────

  /// <summary>
  /// Encodes a 10-byte name + attribute slot. The high bit of each of the
  /// first 9 characters carries an attribute flag; the 10th byte is unused.
  /// </summary>
  private static void EncodeNameAndAttrs(Span<byte> dst, string name, byte attrs) {
    dst.Clear();
    var len = Math.Min(name.Length, 9);
    for (var i = 0; i < len; i++) {
      var c = (byte)(name[i] & 0x7F);
      if (i < 9 && (attrs & (1 << i)) != 0) c |= 0x80;
      dst[i] = c;
    }
    if (len < 9) dst[len] = 0x0D;
  }

  /// <summary>
  /// Inverse of <see cref="EncodeNameAndAttrs"/>. The reader uses the same
  /// algorithm but we duplicate it here so the modifier doesn't have to
  /// page in the whole reader to look up entries.
  /// </summary>
  private static (string Name, byte Attrs) DecodeNameAndAttrs(ReadOnlySpan<byte> raw) {
    byte attrs = 0;
    Span<byte> nameBytes = stackalloc byte[10];
    var n = 0;
    for (var i = 0; i < 10; i++) {
      var c = raw[i];
      if (i < 9 && (c & 0x80) != 0) attrs |= (byte)(1 << i);
      var lo = (byte)(c & 0x7F);
      if (lo == 0 || lo == 0x0D) break;
      nameBytes[n++] = lo;
    }
    return (Encoding.ASCII.GetString(nameBytes[..n]), attrs);
  }

  /// <summary>
  /// Same sanitisation as <see cref="AdfsWriter"/>: 9-char cap, uppercase
  /// ASCII, dollar/colon/hash/dot/star become '_', tail kept on truncation.
  /// </summary>
  private static string SanitizeName(string raw) {
    if (string.IsNullOrEmpty(raw)) return "UNNAMED";
    var s = Path.GetFileName(raw).ToUpperInvariant();
    var chars = new char[s.Length];
    for (var i = 0; i < s.Length; i++) {
      var c = s[i];
      chars[i] = (c >= 0x20 && c < 0x7F && c != '.' && c != '$' && c != ':' && c != '#' && c != '*') ? c : '_';
    }
    var clean = new string(chars).TrimEnd('_');
    if (clean.Length == 0) clean = "UNNAMED";
    if (clean.Length > 9) clean = clean[^9..];
    return clean;
  }

  // ── Image I/O ─────────────────────────────────────────────────────────

  private static void EnsureSeekableImage(Stream s) {
    if (!s.CanSeek) throw new ArgumentException("ADFS modifier requires a seekable stream.", nameof(s));
    if (!s.CanWrite) throw new ArgumentException("ADFS modifier requires a writable stream.", nameof(s));
    if (s.Length != AdfsWriter.DiskSizeL)
      throw new InvalidDataException(
        $"ADFS-L modifier expects a {AdfsWriter.DiskSizeL}-byte image (got {s.Length}).");
  }

  internal static byte[] ReadSector(Stream s, int sectorIndex) {
    var buf = new byte[SectorSize];
    s.Position = (long)sectorIndex * SectorSize;
    var read = 0;
    while (read < SectorSize) {
      var n = s.Read(buf, read, SectorSize - read);
      if (n <= 0) break;
      read += n;
    }
    return buf;
  }

  internal static void WriteSector(Stream s, int sectorIndex, byte[] data) {
    s.Position = (long)sectorIndex * SectorSize;
    s.Write(data, 0, SectorSize);
  }

  internal static byte[] ReadDirectory(Stream s) {
    var buf = new byte[DirectorySize];
    s.Position = RootDirOffset;
    var read = 0;
    while (read < DirectorySize) {
      var n = s.Read(buf, read, DirectorySize - read);
      if (n <= 0) break;
      read += n;
    }
    return buf;
  }

  internal static void WriteDirectory(Stream s, byte[] dir) {
    s.Position = RootDirOffset;
    s.Write(dir, 0, DirectorySize);
  }

  private static void WriteRange(Stream s, long offset, byte[] data) {
    s.Position = offset;
    s.Write(data, 0, data.Length);
  }

  // ── 24-bit LE helpers ─────────────────────────────────────────────────

  internal static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> src) =>
    (uint)(src[0] | (src[1] << 8) | (src[2] << 16));

  internal static void WriteUInt24LittleEndian(Span<byte> dst, uint v) {
    dst[0] = (byte)(v & 0xFF);
    dst[1] = (byte)((v >> 8) & 0xFF);
    dst[2] = (byte)((v >> 16) & 0xFF);
  }
}
