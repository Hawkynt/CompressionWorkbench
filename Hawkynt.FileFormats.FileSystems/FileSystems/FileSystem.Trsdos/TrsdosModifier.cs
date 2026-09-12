#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Trsdos;

/// <summary>
/// In-place modifier for TRSDOS / LDOS disk images. Performs add / remove
/// with strict <b>O(touched bytes)</b> I/O — only the GAT (track 17 sector 0,
/// holding the granule bitmap + signature), the affected directory record
/// (one 32-byte slot in track 17 sectors 2..N), and the file's contiguous
/// granule-aligned data run are read or written. Existing files' data bytes
/// stay byte-identical at their original offsets, and a same-size update
/// never changes the image length.
///
/// <para>The companion <see cref="TrsdosWriter"/> rebuilds an image from
/// scratch; this is the "I have an existing image, mutate it" path.</para>
///
/// <para>Layout reminders (256-byte sectors, 5 sectors per granule, Model
/// III/4):
/// <list type="bullet">
///   <item>Track 17 is the directory track. Sector 0 = GAT (signature 0xFE @0xCD,
///         disk name @0xD0..0xD7, date @0xD8..0xDF; bytes 0..0xCC = granule bitmap).</item>
///   <item>Sectors 2..N of track 17 hold 32-byte directory records: attribute @0,
///         name @5..12, ext @13..15, first granule @24, sector count LE @28..29,
///         EOF byte-count low @27 + high @30.</item>
///   <item>File data lives at (firstGranule × 5) × 256, contiguous for the sector count.</item>
/// </list></para>
/// </summary>
public static class TrsdosModifier {
  private const int SectorSize = 256;
  private const int DirectoryTrack = 17;
  private const int DirEntrySize = 32;
  private const int GranuleSize = 5;
  private const byte SignatureByte = 0xFE;
  private const int SignatureOffset = 0xCD;

  /// <summary>Geometry derived from the image stream by probing the GAT signature.</summary>
  private readonly record struct Geometry(int Spt, int Tracks, int GranulesPerTrack, int TotalGranules, int DirTrackOffset) {
    public int DirEntriesStart => DirTrackOffset + SectorSize * 2;
    public int MaxEntries => (Spt - 2) * SectorSize / DirEntrySize;
  }

  /// <summary>True if the stream is a parseable TRSDOS image (GAT signature found).</summary>
  public static bool IsTrsdos(Stream image) => TryProbe(image, out _);

  private static bool TryProbe(Stream image, out Geometry geo) {
    geo = default;
    var len = image.Length;
    foreach (var spt in new[] { 18, 10, 9, 26 }) {
      var trackOffset = DirectoryTrack * spt * SectorSize;
      if (trackOffset + SectorSize * 2 + DirEntrySize > len) continue;
      image.Position = trackOffset + SignatureOffset;
      if (image.ReadByte() != SignatureByte) continue;
      var granulesPerTrack = Math.Max(1, spt / GranuleSize);
      var tracks = (int)(len / (long)(spt * SectorSize));
      if (tracks < 1) continue;
      geo = new Geometry(spt, tracks, granulesPerTrack, tracks * granulesPerTrack, trackOffset);
      return true;
    }
    return false;
  }

  /// <summary>
  /// Adds a file to the existing image. Allocates a contiguous granule run
  /// from the GAT, writes the data, fills a free directory record, and marks
  /// the granules used.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!TryProbe(image, out var geo))
      throw new InvalidDataException("TRSDOS: not a recognised image (no GAT signature).");

    var dir = ReadDirSectors(image, geo);
    var used = BuildUsedGranules(dir, geo);

    var slot = FindFreeDirSlot(dir, geo);
    if (slot < 0) throw new IOException("TRSDOS: directory full.");

    var sectorsNeeded = Math.Max(1, (data.Length + SectorSize - 1) / SectorSize);
    var granulesNeeded = Math.Max(1, (sectorsNeeded + GranuleSize - 1) / GranuleSize);
    var startGranule = AllocateContiguousGranules(used, geo, granulesNeeded);
    if (startGranule < 0)
      throw new IOException($"TRSDOS: out of space for {granulesNeeded} granule(s).");

    // Write the data run at the granule-aligned sector offset.
    var firstSector = startGranule * GranuleSize;
    var dataOffset = (long)firstSector * SectorSize;
    image.Position = dataOffset;
    if (data.Length > 0) image.Write(data, 0, data.Length);
    var pad = sectorsNeeded * SectorSize - data.Length;
    if (pad > 0) image.Write(new byte[pad], 0, pad);

    // Fill the directory record in the in-memory dir buffer.
    var (fname, ext) = SplitName(name);
    var recOff = slot * DirEntrySize;
    Array.Clear(dir, recOff, DirEntrySize);
    dir[recOff] = 0x10; // attribute: non-system, visible.
    WriteFixedAscii(dir.AsSpan(recOff + 5, 8), fname);
    WriteFixedAscii(dir.AsSpan(recOff + 13, 3), ext);
    dir[recOff + 24] = (byte)startGranule;
    dir[recOff + 28] = (byte)(sectorsNeeded & 0xFF);
    dir[recOff + 29] = (byte)((sectorsNeeded >> 8) & 0xFF);
    dir[recOff + 27] = (byte)(data.Length & 0xFF);
    dir[recOff + 30] = (byte)((data.Length >> 8) & 0xFF);

    // Persist: only the dir sector that holds this slot + the GAT.
    WriteDirSectorForSlot(image, geo, dir, slot);
    UpdateGat(image, geo, used);
  }

  /// <summary>
  /// Removes the named file: frees its granules in the GAT, optionally wipes
  /// the data run, and clears the directory record. Returns true if found.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    if (!TryProbe(image, out var geo)) return false;

    var dir = ReadDirSectors(image, geo);
    var used = BuildUsedGranules(dir, geo);
    var needle = JoinName(SplitName(name));

    for (var i = 0; i < geo.MaxEntries; i++) {
      var recOff = i * DirEntrySize;
      var attr = dir[recOff];
      if (attr == 0x00 || (attr & 0x80) != 0) continue;
      var entryName = JoinName((ReadAsciiTrim(dir.AsSpan(recOff + 5, 8)), ReadAsciiTrim(dir.AsSpan(recOff + 13, 3))));
      if (!string.Equals(entryName, needle, StringComparison.OrdinalIgnoreCase)) continue;

      var firstGranule = dir[recOff + 24];
      var sectorCount = dir[recOff + 28] | (dir[recOff + 29] << 8);
      var granulesUsed = Math.Max(1, (sectorCount + GranuleSize - 1) / GranuleSize);

      // Free granules.
      for (var g = 0; g < granulesUsed; g++) {
        var gi = firstGranule + g;
        if (gi >= 0 && gi < used.Length) used[gi] = false;
      }

      // Optionally wipe the data run.
      if (wipeData && sectorCount > 0) {
        var firstSector = firstGranule * GranuleSize;
        var off = (long)firstSector * SectorSize;
        var bytes = (long)sectorCount * SectorSize;
        if (off >= 0 && off + bytes <= image.Length) {
          image.Position = off;
          image.Write(new byte[bytes], 0, (int)bytes);
        }
      }

      // Clear the directory record (mark slot empty).
      Array.Clear(dir, recOff, DirEntrySize);
      WriteDirSectorForSlot(image, geo, dir, i);
      UpdateGat(image, geo, used);
      return true;
    }
    return false;
  }

  // ── Directory sector I/O ────────────────────────────────────────────

  /// <summary>Reads the directory records area (track 17 sectors 2..N) into a buffer.</summary>
  private static byte[] ReadDirSectors(Stream image, Geometry geo) {
    var len = (geo.Spt - 2) * SectorSize;
    var buf = new byte[len];
    image.Position = geo.DirEntriesStart;
    image.ReadExactly(buf);
    return buf;
  }

  /// <summary>Writes back only the single 256-byte directory sector containing the slot.</summary>
  private static void WriteDirSectorForSlot(Stream image, Geometry geo, byte[] dir, int slot) {
    var byteOffsetInDir = slot * DirEntrySize;
    var sectorInDir = byteOffsetInDir / SectorSize;
    var sectorStartInDir = sectorInDir * SectorSize;
    image.Position = geo.DirEntriesStart + sectorStartInDir;
    image.Write(dir, sectorStartInDir, SectorSize);
  }

  private static int FindFreeDirSlot(byte[] dir, Geometry geo) {
    for (var i = 0; i < geo.MaxEntries; i++) {
      var attr = dir[i * DirEntrySize];
      if (attr == 0x00 || (attr & 0x80) != 0) return i;
    }
    return -1;
  }

  // ── Granule allocation ──────────────────────────────────────────────

  /// <summary>Reconstructs the set of used granules from the live directory
  /// records, plus the reserved directory track.</summary>
  private static bool[] BuildUsedGranules(byte[] dir, Geometry geo) {
    var used = new bool[geo.TotalGranules];
    for (var g = 0; g < geo.GranulesPerTrack; g++) {
      var gi = DirectoryTrack * geo.GranulesPerTrack + g;
      if (gi < used.Length) used[gi] = true;
    }
    for (var i = 0; i < geo.MaxEntries; i++) {
      var recOff = i * DirEntrySize;
      var attr = dir[recOff];
      if (attr == 0x00 || (attr & 0x80) != 0) continue;
      var firstGranule = dir[recOff + 24];
      var sectorCount = dir[recOff + 28] | (dir[recOff + 29] << 8);
      var granulesUsed = Math.Max(1, (sectorCount + GranuleSize - 1) / GranuleSize);
      for (var g = 0; g < granulesUsed; g++) {
        var gi = firstGranule + g;
        if (gi >= 0 && gi < used.Length) used[gi] = true;
      }
    }
    return used;
  }

  /// <summary>Lowest contiguous free granule run of the requested length.
  /// Marks them used. Returns the start granule, or -1.</summary>
  private static int AllocateContiguousGranules(bool[] used, Geometry geo, int count) {
    for (var g = 0; g + count <= geo.TotalGranules; g++) {
      var ok = true;
      for (var k = 0; k < count; k++) {
        if (used[g + k]) { ok = false; g += k; break; }
      }
      if (!ok) continue;
      for (var k = 0; k < count; k++) used[g + k] = true;
      return g;
    }
    return -1;
  }

  /// <summary>Rewrites the GAT sector (track 17 sector 0): granule bitmap bytes
  /// 0..0xCC (bit per granule, 1 = used) plus the preserved signature, disk name
  /// and date.</summary>
  private static void UpdateGat(Stream image, Geometry geo, bool[] used) {
    var gat = new byte[SectorSize];
    image.Position = geo.DirTrackOffset;
    image.ReadExactly(gat);
    // Rebuild the bitmap region 0..0xCC from the used set.
    Array.Clear(gat, 0, SignatureOffset);
    for (var g = 0; g < used.Length; g++) {
      if (!used[g]) continue;
      var byteIdx = g / 8;
      if (byteIdx < SignatureOffset) gat[byteIdx] |= (byte)(1 << (g & 7));
    }
    gat[SignatureOffset] = SignatureByte;
    image.Position = geo.DirTrackOffset;
    image.Write(gat, 0, SectorSize);
  }

  // ── Name helpers ────────────────────────────────────────────────────

  private static (string Name, string Ext) SplitName(string raw) {
    var safe = (raw ?? "").Replace('\\', '/');
    var slash = safe.LastIndexOf('/');
    if (slash >= 0) safe = safe[(slash + 1)..];
    safe = safe.ToUpperInvariant();
    var dot = safe.LastIndexOf('.');
    string name, ext;
    if (dot > 0) { name = safe[..dot]; ext = safe[(dot + 1)..]; }
    else { name = safe; ext = ""; }
    if (name.Length > 8) name = name[..8];
    if (ext.Length > 3) ext = ext[..3];
    return (name, ext);
  }

  private static string JoinName((string Name, string Ext) parts)
    => string.IsNullOrEmpty(parts.Ext) ? parts.Name : $"{parts.Name}.{parts.Ext}";

  private static void WriteFixedAscii(Span<byte> dst, string value) {
    dst.Fill(0x20);
    var n = Math.Min(value.Length, dst.Length);
    for (var i = 0; i < n; i++) {
      var c = value[i];
      dst[i] = c < 0x80 ? (byte)c : (byte)'?';
    }
  }

  private static string ReadAsciiTrim(ReadOnlySpan<byte> span) {
    Span<char> chars = stackalloc char[span.Length];
    var len = 0;
    foreach (var b in span) {
      var c = (byte)(b & 0x7F);
      if (c is 0 or 0x20) { if (len == 0) continue; break; }
      chars[len++] = (char)c;
    }
    return new string(chars[..len]);
  }
}
