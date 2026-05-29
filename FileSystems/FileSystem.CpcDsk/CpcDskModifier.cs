#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.CpcDsk;

/// <summary>
/// Random-access in-place modifier for the AMSDOS / CP/M filesystem that lives
/// inside a Standard or Extended Amstrad CPC DSK disk image. Performs add /
/// remove with strict <b>O(touched bytes)</b> I/O — only the DSK header
/// (1 sector worth), the per-track headers along the path to the affected
/// sectors, the directory area on track 0, and the affected file's data
/// sectors are read or written. The full disk image is never paged in.
///
/// <para>The format has a <em>two-level</em> structure:
/// <list type="bullet">
///   <item>The DSK <em>container</em>: 256-byte Disk Info header + per-track
///         blocks (256-byte Track Info header followed by sector data).</item>
///   <item>The AMSDOS <em>filesystem</em>: 32-byte directory entries packed
///         into the sectors of track 0; file data lives in the sectors of
///         tracks 1+. <see cref="CpcDskWriter"/> uses a flat block-index of
///         "1 block = 1 sector" — this modifier is bug-compatible with that
///         convention so round-trips work.</item>
/// </list>
/// The <see cref="SectorLocator"/> hides the DSK→AMSDOS indirection: callers
/// ask for "track T, side H, sector ordinal N" and get an absolute file offset
/// without caring whether the underlying image is Standard or Extended.</para>
/// </summary>
public static class CpcDskModifier {
  // CP/M directory-entry conventions
  private const int DirEntrySize = 32;
  private const byte DirUnusedMarker = 0xE5;

  // DSK container offsets
  private const int DiskInfoSize = 256;
  private const int TrackInfoSize = 256;

  /// <summary>
  /// Adds a file to the AMSDOS filesystem inside an existing CPC DSK image.
  /// Locates a free directory slot on track 0, allocates as many free data
  /// sectors as needed on tracks 1+ (using the same flat block-numbering
  /// convention as <see cref="CpcDskWriter"/>), writes the data sectors, and
  /// fills in the directory entry.
  /// </summary>
  /// <exception cref="IOException">Disk full (no free sectors on tracks 1+) or
  /// directory full (no <c>0xE5</c> slot left on track 0).</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var loc = SectorLocator.Parse(image);

    // 1. Scan directory area (track 0 side 0) — collect existing block usage so we
    //    can choose new blocks that don't collide. This is a small read: at most
    //    sectorsPerTrack * sectorSize bytes (≈4.5 KB on a 9×512 disk).
    var dir = ReadDirectoryArea(image, loc);
    var usedBlocks = CollectUsedBlocks(dir);

    // 2. Allocate file data blocks. Block numbering here matches CpcDskWriter:
    //    block N = (track N/(sides*spt), side (N/spt)%sides, sector N%spt).
    //    Block 0 is the start of track 0 (= directory) so file blocks always
    //    start at the first track-1 block and skip any track-0 blocks.
    var blocksNeeded = data.Length == 0 ? 1 : (data.Length + loc.SectorSize - 1) / loc.SectorSize;
    if (blocksNeeded > 16)
      throw new IOException("CPC DSK: file too large for a single 16-block CP/M extent.");

    var allocated = new List<int>(blocksNeeded);
    // Skip block range [0 .. tracksides0Blocks) which is the directory area.
    var firstDataBlock = loc.SectorsPerTrack * loc.Sides;  // = blocks-per-(track*sides) for track 0
    var totalBlocks = loc.Tracks * loc.Sides * loc.SectorsPerTrack;
    for (var b = firstDataBlock; b < totalBlocks && allocated.Count < blocksNeeded; b++) {
      if (usedBlocks.Contains(b)) continue;
      allocated.Add(b);
    }
    if (allocated.Count < blocksNeeded)
      throw new IOException($"CPC DSK: out of space — needed {blocksNeeded} blocks, found {allocated.Count}.");

    // 3. Find a free directory slot (user_number == 0xE5).
    var slotIndex = FindFreeDirSlot(dir);
    if (slotIndex < 0)
      throw new IOException("CPC DSK: directory full (no 0xE5 slot in track 0).");

    // 4. Write file data into the allocated blocks.
    var pos = 0;
    foreach (var blockNum in allocated) {
      var (t, side, secIdx) = BlockToTrackSideSector(blockNum, loc);
      var sectorOffset = loc.GetSectorOffset(t, side, secIdx);
      var chunk = new byte[loc.SectorSize];
      var copy = Math.Min(loc.SectorSize, data.Length - pos);
      if (copy > 0) data.AsSpan(pos, copy).CopyTo(chunk);
      image.Position = sectorOffset;
      image.Write(chunk);
      pos += copy;
    }

    // 5. Build the directory entry in the in-memory dir area.
    WriteDirEntry(dir, slotIndex, name, data.Length, allocated);

    // 6. Persist the modified directory sectors back to disk.
    WriteDirectoryArea(image, loc, dir);
  }

  /// <summary>
  /// Walks the AMSDOS directory in track 0 of an existing CPC DSK image and
  /// returns the <em>logical</em> files it describes — the same name+payload
  /// pairs that <see cref="CpcDskWriter.AddFile"/> consumes. Multi-extent
  /// files (same base+ext across several directory slots with different
  /// <c>EX</c> values) are stitched together in extent order, and trailing
  /// slack inside the last sector is trimmed to the byte-precise length
  /// implied by the <c>RC</c> field (records × 128).
  ///
  /// <para>This is the read side of the round-trip needed by
  /// <see cref="Compression.Registry.IArchiveDefragmentable"/>. The physical
  /// <see cref="CpcDskReader"/> exposes <c>T01S0_C1</c>-style sector names,
  /// which would corrupt file identity if fed to the writer; this method
  /// returns the AMSDOS filenames the writer rebuilds verbatim.</para>
  ///
  /// <para>I/O cost: one disk-info header read (256 B) + one geometry probe
  /// TIB read (256 B) + the full directory area on track 0 (sectorsPerTrack ×
  /// sectorSize, ≈4.5 KB on a 9×512 disk) + one sector read per allocated
  /// data block referenced by live entries. Unformatted track 0 returns no
  /// entries.</para>
  /// </summary>
  /// <returns>(name, data) pairs in directory-slot order. Multi-extent files
  /// appear once. Empty if the directory is all-0xE5 or track 0 is unformatted.</returns>
  public static IEnumerable<(string Name, byte[] Data)> EnumerateLogicalFiles(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var loc = SectorLocator.Parse(image);
    var dir = ReadDirectoryArea(image, loc);

    // Group dir slots by (base, ext) — each extent of a multi-extent file is a
    // separate 32-byte directory record sharing the same name with a different
    // EX value. Preserve first-seen ordering so consumers see deterministic
    // output across runs.
    var groups = new Dictionary<(string Base, string Ext), List<(int ExtentNumber, int RecordCount, int[] Blocks)>>();
    var order = new List<(string Base, string Ext)>();

    var maxEntries = dir.Length / DirEntrySize;
    for (var slot = 0; slot < maxEntries; slot++) {
      var off = slot * DirEntrySize;
      var userNumber = dir[off];
      if (userNumber == DirUnusedMarker) continue;
      // CP/M reserves user numbers > 0x0F for non-file purposes (label, time-stamp).
      // AMSDOS only emits user 0..15 for real files; the writer stores 0.
      if (userNumber > 0x0F) continue;

      var entryBase = StripAttributeBits(Encoding.ASCII.GetString(dir, off + 1, 8)).TrimEnd();
      var entryExt = StripAttributeBits(Encoding.ASCII.GetString(dir, off + 9, 3)).TrimEnd();
      // Skip slots whose name is empty — defensive against fuzzed images.
      if (entryBase.Length == 0 && entryExt.Length == 0) continue;

      var ex = dir[off + 12];          // extent number low byte
      var s2 = dir[off + 14];          // extent number high byte (bits 5..)
      var extentNumber = ex | (s2 << 5);
      var rc = dir[off + 15];

      var blocks = new int[16];
      for (var b = 0; b < 16; b++)
        blocks[b] = dir[off + 16 + b];

      var key = (entryBase, entryExt);
      if (!groups.TryGetValue(key, out var list)) {
        list = [];
        groups[key] = list;
        order.Add(key);
      }
      list.Add((extentNumber, rc, blocks));
    }

    if (groups.Count == 0) yield break;

    var totalBlocks = loc.Tracks * loc.Sides * loc.SectorsPerTrack;
    foreach (var key in order) {
      var extents = groups[key];
      // Sort extents by EX so blocks come out in logical order regardless of
      // directory-slot layout. CP/M normally writes them in order, but rebuilds
      // and free-slot reuse can interleave them.
      extents.Sort(static (a, b) => a.ExtentNumber.CompareTo(b.ExtentNumber));

      // Concatenate every referenced block in extent-then-allocation order.
      // Block 0 in CP/M's allocation list means "no block here" — stop reading
      // that extent. Blocks beyond the image (corrupt entries) are skipped.
      var data = new List<byte>();
      var lastRc = 0;
      foreach (var (_, rc, blocks) in extents) {
        for (var i = 0; i < blocks.Length; i++) {
          var blockNum = blocks[i];
          if (blockNum == 0) break;
          if (blockNum >= totalBlocks) continue;
          var (t, side, secIdx) = BlockToTrackSideSector(blockNum, loc);
          var offset = loc.GetSectorOffset(t, side, secIdx);
          if (offset < 0) continue;
          var sectorBuf = new byte[loc.SectorSize];
          image.Position = offset;
          image.ReadExactly(sectorBuf);
          data.AddRange(sectorBuf);
        }
        lastRc = rc;
      }

      // Trim trailing slack to byte-precise length. RC counts 128-byte CP/M
      // records used in the *last* extent; everything before that is full
      // 128-record extents (16 384 B at 1-block-per-sector × 512 = full alloc list).
      // Computing the exact length needs to know how many extents preceded:
      //     fullExtents = extents.Count - 1
      //     totalRecords = fullExtents × 128 + (lastRc == 0 ? 128 : lastRc)
      // and the final byte length is totalRecords × 128. If the writer's
      // CP/M math undercounts (rc==0 with empty file) we still cap at the
      // actual block bytes we read.
      var fullExtents = extents.Count - 1;
      var effectiveRc = lastRc == 0 ? 128 : lastRc;
      var byteLength = (long)fullExtents * 128 * 128 + (long)effectiveRc * 128;
      if (byteLength > data.Count) byteLength = data.Count;
      if (byteLength < 0) byteLength = 0;

      var payload = byteLength == data.Count ? data.ToArray() : data.GetRange(0, (int)byteLength).ToArray();

      var fullName = key.Ext.Length == 0 ? key.Base : key.Base + "." + key.Ext;
      yield return (fullName, payload);
    }
  }

  /// <summary>
  /// Removes the named file from the AMSDOS filesystem. Returns true if the
  /// file was found and deleted. Walks the directory to locate the entry,
  /// optionally wipes the data blocks it references, then sets the
  /// user-number byte to <c>0xE5</c> (CP/M unused marker).
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var loc = SectorLocator.Parse(image);
    var dir = ReadDirectoryArea(image, loc);

    var (shortBase, shortExt) = SplitName(name);

    var found = false;
    var dirty = false;
    var blocksToWipe = new HashSet<int>();

    var maxEntries = dir.Length / DirEntrySize;
    for (var slot = 0; slot < maxEntries; slot++) {
      var off = slot * DirEntrySize;
      if (dir[off] == DirUnusedMarker) continue;

      var entryBase = Encoding.ASCII.GetString(dir, off + 1, 8).TrimEnd();
      var entryExt = Encoding.ASCII.GetString(dir, off + 9, 3).TrimEnd();

      // Strip CP/M attribute bits (top bit of ext bytes encode read-only / system / archive).
      var cleanedBase = StripAttributeBits(entryBase);
      var cleanedExt = StripAttributeBits(entryExt);
      if (!cleanedBase.Equals(shortBase, StringComparison.OrdinalIgnoreCase)) continue;
      if (!cleanedExt.Equals(shortExt, StringComparison.OrdinalIgnoreCase)) continue;

      // Collect blocks referenced by this extent's allocation list.
      if (wipeData) {
        for (var b = 0; b < 16; b++) {
          var blk = dir[off + 16 + b];
          if (blk != 0) blocksToWipe.Add(blk);
        }
      }

      // Mark slot as deleted.
      dir[off] = DirUnusedMarker;
      dirty = true;
      found = true;
      // Don't break — same file may have multiple extents (EX > 0).
    }

    if (!found) return false;

    // Wipe data blocks if requested. Each block index is interpreted with the
    // same convention CpcDskWriter uses: block = flat sector index across the
    // whole image.
    if (wipeData) {
      var totalBlocks = loc.Tracks * loc.Sides * loc.SectorsPerTrack;
      var zero = new byte[loc.SectorSize];
      foreach (var blockNum in blocksToWipe) {
        if (blockNum >= totalBlocks) continue;
        var (t, side, secIdx) = BlockToTrackSideSector(blockNum, loc);
        var sectorOffset = loc.GetSectorOffset(t, side, secIdx);
        if (sectorOffset < 0) continue;
        image.Position = sectorOffset;
        image.Write(zero);
      }
    }

    if (dirty) WriteDirectoryArea(image, loc, dir);
    return true;
  }

  // ── Directory area I/O ────────────────────────────────────────────────

  /// <summary>
  /// Reads every sector of track 0, side 0 — that's the AMSDOS directory area
  /// per <see cref="CpcDskWriter"/>'s convention. Returns a single contiguous
  /// buffer (sectorsPerTrack × sectorSize bytes).
  /// </summary>
  private static byte[] ReadDirectoryArea(Stream image, SectorLocator loc) {
    var dir = new byte[loc.SectorsPerTrack * loc.SectorSize];
    var sectorBuf = new byte[loc.SectorSize];
    for (var s = 0; s < loc.SectorsPerTrack; s++) {
      var off = loc.GetSectorOffset(0, 0, s);
      if (off < 0) {
        // Unformatted track: leave that slice zeroed (safe — won't match any name).
        continue;
      }
      image.Position = off;
      image.ReadExactly(sectorBuf);
      sectorBuf.AsSpan().CopyTo(dir.AsSpan(s * loc.SectorSize));
    }
    return dir;
  }

  private static void WriteDirectoryArea(Stream image, SectorLocator loc, byte[] dir) {
    for (var s = 0; s < loc.SectorsPerTrack; s++) {
      var off = loc.GetSectorOffset(0, 0, s);
      if (off < 0) continue;
      image.Position = off;
      image.Write(dir, s * loc.SectorSize, loc.SectorSize);
    }
  }

  // ── Directory helpers ─────────────────────────────────────────────────

  private static int FindFreeDirSlot(byte[] dir) {
    var maxEntries = dir.Length / DirEntrySize;
    for (var i = 0; i < maxEntries; i++) {
      if (dir[i * DirEntrySize] == DirUnusedMarker) return i;
    }
    return -1;
  }

  private static HashSet<int> CollectUsedBlocks(byte[] dir) {
    var used = new HashSet<int>();
    var maxEntries = dir.Length / DirEntrySize;
    for (var i = 0; i < maxEntries; i++) {
      var off = i * DirEntrySize;
      if (dir[off] == DirUnusedMarker) continue;
      for (var b = 0; b < 16; b++) {
        var blk = dir[off + 16 + b];
        if (blk != 0) used.Add(blk);
      }
    }
    return used;
  }

  private static void WriteDirEntry(byte[] dir, int slot, string name, int dataLength, IReadOnlyList<int> blocks) {
    var off = slot * DirEntrySize;
    var (shortBase, shortExt) = SplitName(name);

    // user number (0 = default user)
    dir[off + 0] = 0x00;
    // 8-char base, 3-char ext, ASCII, space-padded.
    var basePadded = shortBase.PadRight(8).Substring(0, 8);
    var extPadded = shortExt.PadRight(3).Substring(0, 3);
    Encoding.ASCII.GetBytes(basePadded).CopyTo(dir, off + 1);
    Encoding.ASCII.GetBytes(extPadded).CopyTo(dir, off + 9);

    // EX, S1, S2 (extent number / fillers) — 0 for first extent.
    dir[off + 12] = 0;
    dir[off + 13] = 0;
    dir[off + 14] = 0;
    // RC: number of 128-byte records used in this extent.
    dir[off + 15] = (byte)Math.Min(128, (dataLength + 127) / 128);

    // AL: 16-byte allocation list. CpcDskWriter stores 1-byte block numbers; mirror that.
    for (var b = 0; b < 16; b++)
      dir[off + 16 + b] = b < blocks.Count ? (byte)(blocks[b] & 0xFF) : (byte)0;
  }

  // ── Block-number ↔ (track, side, sector) translation ──────────────────
  // Mirrors CpcDskWriter's flat block-number convention exactly.

  private static (int Track, int Side, int Sector) BlockToTrackSideSector(int blockNum, SectorLocator loc) {
    var blocksPerTrack = loc.SectorsPerTrack * loc.Sides;
    var track = blockNum / blocksPerTrack;
    var withinTrack = blockNum % blocksPerTrack;
    var side = withinTrack / loc.SectorsPerTrack;
    var sector = withinTrack % loc.SectorsPerTrack;
    return (track, side, sector);
  }

  // ── Name helpers ──────────────────────────────────────────────────────

  private static (string ShortBase, string ShortExt) SplitName(string name) {
    var fileName = Path.GetFileName(name);
    var dot = fileName.LastIndexOf('.');
    var basePart = dot >= 0 ? fileName[..dot] : fileName;
    var extPart = dot >= 0 ? fileName[(dot + 1)..] : "";
    var shortBase = basePart.ToUpperInvariant();
    var shortExt = extPart.ToUpperInvariant();
    if (shortBase.Length > 8) shortBase = shortBase[..8];
    if (shortExt.Length > 3) shortExt = shortExt[..3];
    return (shortBase, shortExt);
  }

  private static string StripAttributeBits(string raw) {
    // CP/M sets the top bit of extension chars to flag R/O, system, archive.
    // We don't want to compare those bits. Whitespace was already trimmed.
    var chars = new char[raw.Length];
    for (var i = 0; i < raw.Length; i++)
      chars[i] = (char)(raw[i] & 0x7F);
    return new string(chars).TrimEnd();
  }

  // ── Sector locator: hides the DSK→AMSDOS indirection ──────────────────

  /// <summary>
  /// Caches the absolute file offset of each (track, side) and the sector
  /// geometry, so callers can map (track, side, sector-ordinal) → byte offset
  /// in O(1). Built once per Add/Remove call (one lazy header read pass).
  /// Handles both Standard ("MV - CPC") and Extended ("EXTENDED") layouts.
  /// </summary>
  private sealed class SectorLocator {
    public required int Tracks { get; init; }
    public required int Sides { get; init; }
    public required int SectorsPerTrack { get; init; }
    public required int SectorSize { get; init; }
    /// <summary>
    /// Absolute file offset of each track block (Track Info Block start).
    /// Indexed as <c>[track * Sides + side]</c>. Negative = unformatted.
    /// </summary>
    public required long[] TrackBlockOffsets { get; init; }

    public long GetSectorOffset(int track, int side, int sectorIndexInTrack) {
      if (track < 0 || track >= Tracks) return -1;
      if (side < 0 || side >= Sides) return -1;
      if (sectorIndexInTrack < 0 || sectorIndexInTrack >= SectorsPerTrack) return -1;
      var trackBase = TrackBlockOffsets[track * Sides + side];
      if (trackBase < 0) return -1;
      return trackBase + TrackInfoSize + (long)sectorIndexInTrack * SectorSize;
    }

    public static SectorLocator Parse(Stream image) {
      // Read disk info header — 256 bytes is enough for both magic + extended size table.
      var diskInfo = new byte[DiskInfoSize];
      image.Position = 0;
      image.ReadExactly(diskInfo);

      var magic = Encoding.ASCII.GetString(diskInfo, 0, 8);
      bool isExtended;
      if (magic.StartsWith("EXTENDED", StringComparison.Ordinal)) isExtended = true;
      else if (magic.StartsWith("MV - CPC", StringComparison.Ordinal)) isExtended = false;
      else throw new InvalidDataException($"CPC DSK: unrecognised magic '{magic}'.");

      var tracks = diskInfo[48];
      var sides = diskInfo[49];

      var offsets = new long[tracks * sides];
      var current = (long)DiskInfoSize;
      int sectorsPerTrack = 0, sectorSize = 0;

      if (!isExtended) {
        var trackSize = BinaryPrimitives.ReadUInt16LittleEndian(diskInfo.AsSpan(50));
        for (var t = 0; t < tracks; t++) {
          for (var s = 0; s < sides; s++) {
            offsets[t * sides + s] = current;
            // Probe the first track's TIB to learn geometry. All tracks are
            // assumed uniform in Standard layout.
            if (sectorsPerTrack == 0) (sectorsPerTrack, sectorSize) = ReadTrackGeometry(image, current);
            current += trackSize;
          }
        }
      } else {
        // Extended track size table at offset 52: one byte per (track,side), value × 256 = block size.
        for (var t = 0; t < tracks; t++) {
          for (var s = 0; s < sides; s++) {
            var idx = t * sides + s;
            var highByte = diskInfo[52 + idx];
            if (highByte == 0) {
              offsets[idx] = -1; // unformatted
              continue;
            }
            offsets[idx] = current;
            if (sectorsPerTrack == 0) (sectorsPerTrack, sectorSize) = ReadTrackGeometry(image, current);
            current += highByte * 256;
          }
        }
      }

      if (sectorsPerTrack == 0)
        throw new InvalidDataException("CPC DSK: no formatted tracks — cannot determine geometry.");

      return new SectorLocator {
        Tracks = tracks,
        Sides = sides,
        SectorsPerTrack = sectorsPerTrack,
        SectorSize = sectorSize,
        TrackBlockOffsets = offsets,
      };
    }

    private static (int SectorsPerTrack, int SectorSize) ReadTrackGeometry(Stream image, long trackBlockOffset) {
      // Read just the Track Info Block (256 bytes).
      var tib = new byte[TrackInfoSize];
      image.Position = trackBlockOffset;
      image.ReadExactly(tib);
      // Validate marker — gracefully fall back if missing.
      var marker = Encoding.ASCII.GetString(tib, 0, 10);
      if (!marker.StartsWith("Track-Info", StringComparison.Ordinal))
        throw new InvalidDataException($"CPC DSK: missing Track-Info marker at offset 0x{trackBlockOffset:X}.");
      var sizeCode = tib[20];
      var sectorCount = tib[21];
      var sectorSize = 128 << sizeCode;
      if (sectorSize < 128 || sectorSize > 8192) sectorSize = 512;
      if (sectorCount == 0) sectorCount = 9;
      return (sectorCount, sectorSize);
    }
  }
}
