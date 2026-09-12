#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Iso;

/// <summary>
/// In-place ISO 9660 (ECMA-119) modifier — true random-access editing without
/// rebuilding the whole image. Touches only the PVD (sector 16), the root
/// directory's existing extent, and the new file's data sectors.
///
/// <para>Layout assumptions (matching <see cref="IsoWriter"/>):
/// <list type="bullet">
///   <item>2048-byte sectors, single-level root directory (no subdirectories).</item>
///   <item>PVD at LBA 16; an optional Joliet SVD follows before the VDST.</item>
///   <item>File data laid out sequentially after the directory extents.</item>
///   <item>No Rock Ridge System Use entries.</item>
/// </list>
/// New file data is appended after the current volume space; the PVD's volume
/// space size is updated and a directory record is inserted into a free slot
/// of the existing root directory extent. Removal shifts subsequent records
/// in-place within their sector and optionally wipes the data sectors. When the
/// image carries a Joliet SVD, the same add/remove is mirrored into the parallel
/// Joliet root directory (UCS-2BE long names) so both trees stay consistent.</para>
/// </summary>
public static class IsoModifier {
  private const int SectorSize = 2048;
  private const int PvdLba = 16;
  private const int PvdOffset = PvdLba * SectorSize;
  private const int PvdRootRecord = PvdOffset + 156;

  /// <summary>
  /// Adds (or replaces) a file at the root of an ISO 9660 image. The file
  /// data is appended after the current volume extent; a new directory record
  /// is written into a free slot of the root directory's existing extent.
  /// Throws <see cref="IOException"/> if the root directory has no free slot
  /// (extending the directory would clobber file data and is not supported).
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!image.CanSeek || !image.CanRead || !image.CanWrite)
      throw new ArgumentException("Image stream must be readable, writable, and seekable.", nameof(image));

    // Replace semantics: if a record with the same identifier already exists, drop it first.
    RemoveFile(image, name, wipeData: true);

    var pvd = ReadSector(image, PvdLba);
    var rootLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 2));
    var rootLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 10));
    var volumeSpace = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(80));

    var identifier = BuildIsoIdentifier(name);
    var recLen = 33 + identifier.Length;
    if ((recLen & 1) != 0) recLen++;

    // Allocate file data sectors at the tail of the image.
    var fileLba = volumeSpace;
    var fileSectorCount = data.Length == 0 ? 1 : (data.Length + SectorSize - 1) / SectorSize;
    var newVolumeSpace = volumeSpace + fileSectorCount;

    // Find a free slot inside the existing root directory extent.
    var slot = FindFreeRootSlot(image, rootLba, rootLen, recLen)
      ?? throw new IOException("ISO9660: root directory has no free slot for new entry; extending the directory is not supported in-place.");

    // Write data sectors first (idempotent re: directory state).
    image.Position = (long)fileLba * SectorSize;
    image.Write(data);
    var tail = fileSectorCount * SectorSize - data.Length;
    if (tail > 0) image.Write(new byte[tail]);

    // Build the directory record in a temp buffer and patch it into the root sector.
    var sectorBytes = ReadSector(image, slot.SectorLba);
    WriteDirectoryRecord(sectorBytes.AsSpan(slot.OffsetInSector), fileLba, data.Length, flags: 0x00, identifier);
    WriteSector(image, slot.SectorLba, sectorBytes);

    // Update the PVD volume space size (and matching BE copy).
    BinaryPrimitives.WriteUInt32LittleEndian(pvd.AsSpan(80), (uint)newVolumeSpace);
    BinaryPrimitives.WriteUInt32BigEndian(pvd.AsSpan(84), (uint)newVolumeSpace);
    WriteSector(image, PvdLba, pvd);

    // Mirror the new record into the Joliet tree (if present) so both trees
    // describe the same file-data extent. The Joliet record carries the long,
    // mixed-case name as UCS-2BE.
    var svdLba = FindJolietSvdLba(image);
    if (svdLba >= 0) {
      var svd = ReadSector(image, svdLba);
      var jRootLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(svd.AsSpan(156 + 2));
      var jRootLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(svd.AsSpan(156 + 10));
      var jIdentifier = BuildJolietIdentifier(name);
      var jRecLen = 33 + jIdentifier.Length;
      if ((jRecLen & 1) != 0) jRecLen++;
      var jSlot = FindFreeRootSlot(image, jRootLba, jRootLen, jRecLen);
      if (jSlot is not null) {
        var jSectorBytes = ReadSector(image, jSlot.SectorLba);
        WriteDirectoryRecord(jSectorBytes.AsSpan(jSlot.OffsetInSector), fileLba, data.Length, flags: 0x00, jIdentifier);
        WriteSector(image, jSlot.SectorLba, jSectorBytes);
      }
    }

    // Stream length must match the volume space; SetLength is harmless if already correct.
    var requiredLength = (long)newVolumeSpace * SectorSize;
    if (image.Length < requiredLength) image.SetLength(requiredLength);
  }

  /// <summary>
  /// Removes a named file from the root directory of an ISO 9660 image. Shifts
  /// later records within the same directory sector to fill the hole, zero-fills
  /// the trailing tail, and (by default) wipes the file's data sectors. The PVD
  /// volume space size is left unchanged: the freed sectors at the tail can be
  /// reclaimed by a subsequent <see cref="AddFile"/>, but truncating the image
  /// would shift offsets used by other entries.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    if (!image.CanSeek || !image.CanRead || !image.CanWrite)
      throw new ArgumentException("Image stream must be readable, writable, and seekable.", nameof(image));

    var pvd = ReadSector(image, PvdLba);
    var rootLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 2));
    var rootLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 10));

    var match = FindEntry(image, rootLba, rootLen, name);
    if (match is null) return false;

    var sectorBytes = ReadSector(image, match.SectorLba);
    RemoveRecordFromSector(sectorBytes, match.OffsetInSector, match.RecordLength);
    WriteSector(image, match.SectorLba, sectorBytes);

    // Mirror the removal into the Joliet tree (if present): match the parallel
    // record by the shared data extent LBA, since the Joliet name is UCS-2.
    var svdLba = FindJolietSvdLba(image);
    if (svdLba >= 0) {
      var svd = ReadSector(image, svdLba);
      var jRootLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(svd.AsSpan(156 + 2));
      var jRootLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(svd.AsSpan(156 + 10));
      var jMatch = FindEntryByDataLba(image, jRootLba, jRootLen, match.DataLba);
      if (jMatch is not null) {
        var jSectorBytes = ReadSector(image, jMatch.SectorLba);
        RemoveRecordFromSector(jSectorBytes, jMatch.OffsetInSector, jMatch.RecordLength);
        WriteSector(image, jMatch.SectorLba, jSectorBytes);
      }
    }

    if (wipeData && match.DataLength > 0) {
      var dataBytes = match.DataLength;
      var dataSectors = (dataBytes + SectorSize - 1) / SectorSize;
      var totalToWipe = (long)dataSectors * SectorSize;
      var startPos = (long)match.DataLba * SectorSize;
      if (startPos < image.Length) {
        var capped = Math.Min(totalToWipe, image.Length - startPos);
        image.Position = startPos;
        var zeros = new byte[Math.Min(SectorSize, capped)];
        var remaining = capped;
        while (remaining > 0) {
          var chunk = (int)Math.Min(zeros.Length, remaining);
          image.Write(zeros, 0, chunk);
          remaining -= chunk;
        }
      }
    }
    return true;
  }

  // ── Slot search ────────────────────────────────────────────────────────

  private sealed record SlotLocation(int SectorLba, int OffsetInSector);

  private static SlotLocation? FindFreeRootSlot(Stream image, int rootLba, int rootLen, int needed) {
    // Walk every sector of the root extent; in each sector find the offset
    // immediately after the last live record and check whether `needed` bytes
    // fit before the sector boundary.
    var sectorCount = (rootLen + SectorSize - 1) / SectorSize;
    for (var s = 0; s < sectorCount; s++) {
      var sectorBytes = ReadSector(image, rootLba + s);
      var pos = 0;
      while (pos < SectorSize) {
        var len = sectorBytes[pos];
        if (len == 0) break;
        pos += len;
        if (pos > SectorSize) { pos = SectorSize; break; }
      }
      if (SectorSize - pos >= needed)
        return new SlotLocation(rootLba + s, pos);
    }
    return null;
  }

  // ── Entry lookup ───────────────────────────────────────────────────────

  private sealed record EntryMatch(
    int SectorLba, int OffsetInSector, int RecordLength,
    int DataLba, int DataLength
  );

  private static EntryMatch? FindEntry(Stream image, int rootLba, int rootLen, string name) {
    var nameUpper = NormalizeName(name);
    var sectorCount = (rootLen + SectorSize - 1) / SectorSize;
    for (var s = 0; s < sectorCount; s++) {
      var sectorBytes = ReadSector(image, rootLba + s);
      var pos = 0;
      while (pos < SectorSize) {
        var len = sectorBytes[pos];
        if (len == 0) break;
        if (pos + len > SectorSize) break;
        if (len < 33) break;
        var nameLen = sectorBytes[pos + 32];
        if (nameLen > 0 && nameLen <= len - 33) {
          // Skip "." (0x00) and ".." (0x01) entries.
          var first = sectorBytes[pos + 33];
          if (!(nameLen == 1 && (first == 0 || first == 1))) {
            var raw = Encoding.ASCII.GetString(sectorBytes, pos + 33, nameLen);
            var canonical = StripVersion(raw);
            if (string.Equals(canonical, nameUpper, StringComparison.OrdinalIgnoreCase)) {
              var dataLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(sectorBytes.AsSpan(pos + 2));
              var dataLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(sectorBytes.AsSpan(pos + 10));
              return new EntryMatch(rootLba + s, pos, len, dataLba, dataLen);
            }
          }
        }
        pos += len;
      }
    }
    return null;
  }

  // Locates a directory record in a single-level root extent whose file-data
  // extent LBA matches the given value. Used to mirror removals across trees.
  private static EntryMatch? FindEntryByDataLba(Stream image, int rootLba, int rootLen, int dataLba) {
    var sectorCount = (rootLen + SectorSize - 1) / SectorSize;
    for (var s = 0; s < sectorCount; s++) {
      var sectorBytes = ReadSector(image, rootLba + s);
      var pos = 0;
      while (pos < SectorSize) {
        var len = sectorBytes[pos];
        if (len == 0) break;
        if (pos + len > SectorSize) break;
        if (len < 33) break;
        var nameLen = sectorBytes[pos + 32];
        var first = nameLen > 0 ? sectorBytes[pos + 33] : (byte)0xFF;
        var isDotEntry = nameLen == 1 && (first == 0 || first == 1);
        if (!isDotEntry) {
          var recDataLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(sectorBytes.AsSpan(pos + 2));
          if (recDataLba == dataLba) {
            var recDataLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(sectorBytes.AsSpan(pos + 10));
            return new EntryMatch(rootLba + s, pos, len, recDataLba, recDataLen);
          }
        }
        pos += len;
      }
    }
    return null;
  }

  // ── Joliet helpers ───────────────────────────────────────────────────────

  // Scans the volume-descriptor set for a type-2 descriptor carrying a UCS-2
  // escape sequence (Joliet) at offset 88; returns its LBA or -1 if absent.
  private static int FindJolietSvdLba(Stream image) {
    for (var lba = PvdLba + 1; lba < 64; lba++) {
      if ((long)(lba + 1) * SectorSize > image.Length) break;
      var sector = ReadSector(image, lba);
      if (sector[0] == 0xFF) break; // terminator
      if (!(sector[1] == 'C' && sector[2] == 'D' && sector[3] == '0' && sector[4] == '0' && sector[5] == '1'))
        continue;
      if (sector[0] != 2) continue;
      var e0 = sector[88];
      var e1 = sector[89];
      var e2 = sector[90];
      if (e0 == 0x25 && e1 == 0x2F && (e2 == 0x40 || e2 == 0x43 || e2 == 0x45))
        return lba;
    }
    return -1;
  }

  // The Joliet identifier is the long, mixed-case name as UCS-2BE, truncated to
  // 64 UCS-2 characters (128 bytes). No ';1' version suffix is appended.
  internal static byte[] BuildJolietIdentifier(string name) {
    var raw = StripVersion(name).TrimEnd();
    if (raw.Length > 64) raw = raw[..64];
    return Encoding.BigEndianUnicode.GetBytes(raw);
  }

  // ── Record shifting ────────────────────────────────────────────────────

  private static void RemoveRecordFromSector(byte[] sectorBytes, int recordOffset, int recordLength) {
    // Determine total length of live records in this sector (up to first 0-length terminator).
    var live = 0;
    var p = 0;
    while (p < SectorSize) {
      var len = sectorBytes[p];
      if (len == 0) break;
      if (p + len > SectorSize) { live = SectorSize; break; }
      live = p + len;
      p += len;
    }

    // Shift everything after the removed record down by `recordLength` bytes.
    var srcStart = recordOffset + recordLength;
    var copyLen = live - srcStart;
    if (copyLen > 0)
      Buffer.BlockCopy(sectorBytes, srcStart, sectorBytes, recordOffset, copyLen);

    // Zero the freed tail.
    var newLive = live - recordLength;
    var zeroFrom = newLive;
    if (zeroFrom < 0) zeroFrom = 0;
    Array.Clear(sectorBytes, zeroFrom, SectorSize - zeroFrom);
  }

  // ── Directory record writer ────────────────────────────────────────────

  private static void WriteDirectoryRecord(Span<byte> dst, int lba, int size, byte flags, byte[] identifier) {
    var idLen = identifier.Length;
    var recLen = 33 + idLen;
    if ((recLen & 1) != 0) recLen++;

    dst[0] = (byte)recLen;
    dst[1] = 0; // extended attribute record length
    BinaryPrimitives.WriteUInt32LittleEndian(dst[2..], (uint)lba);
    BinaryPrimitives.WriteUInt32BigEndian(dst[6..], (uint)lba);
    BinaryPrimitives.WriteUInt32LittleEndian(dst[10..], (uint)size);
    BinaryPrimitives.WriteUInt32BigEndian(dst[14..], (uint)size);

    var now = DateTime.UtcNow;
    dst[18] = (byte)(now.Year - 1900);
    dst[19] = (byte)now.Month;
    dst[20] = (byte)now.Day;
    dst[21] = (byte)now.Hour;
    dst[22] = (byte)now.Minute;
    dst[23] = (byte)now.Second;
    dst[24] = 0;

    dst[25] = flags;
    dst[26] = 0; // file unit size
    dst[27] = 0; // interleave gap
    BinaryPrimitives.WriteUInt16LittleEndian(dst[28..], 1);
    BinaryPrimitives.WriteUInt16BigEndian(dst[30..], 1);
    dst[32] = (byte)idLen;
    identifier.CopyTo(dst[33..]);
    if (recLen > 33 + idLen) dst[33 + idLen] = 0; // pad byte
  }

  // ── Name handling ──────────────────────────────────────────────────────

  /// <summary>
  /// Sanitizes a logical name into an ISO 9660 d-characters identifier (uppercase,
  /// 8.3, ';1' version). Invalid characters become '_'. The basename is truncated
  /// to 8 chars; the extension (after the last '.') to 3 chars. Empty-after-
  /// sanitize names become a single underscore.
  /// </summary>
  internal static byte[] BuildIsoIdentifier(string name) {
    var canon = NormalizeName(name);
    return Encoding.ASCII.GetBytes(canon + ";1");
  }

  private static string NormalizeName(string name) {
    // Strip any pre-existing version suffix and trailing whitespace/dots.
    var raw = StripVersion(name).TrimEnd('.', ' ');
    string baseName, ext;
    var dot = raw.LastIndexOf('.');
    if (dot < 0) { baseName = raw; ext = ""; }
    else { baseName = raw[..dot]; ext = raw[(dot + 1)..]; }
    baseName = SanitizeSegment(baseName, 8);
    ext = SanitizeSegment(ext, 3);
    if (baseName.Length == 0) baseName = "_";
    return ext.Length > 0 ? $"{baseName}.{ext}" : baseName;
  }

  private static string StripVersion(string s) {
    var semi = s.IndexOf(';');
    if (semi >= 0) s = s[..semi];
    return s;
  }

  private static string SanitizeSegment(string s, int max) {
    var sb = new StringBuilder(Math.Min(s.Length, max));
    foreach (var ch in s) {
      if (sb.Length >= max) break;
      var c = char.ToUpperInvariant(ch);
      var ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
      sb.Append(ok ? c : '_');
    }
    return sb.ToString();
  }

  // ── Sector I/O ─────────────────────────────────────────────────────────

  private static byte[] ReadSector(Stream image, int lba) {
    var buf = new byte[SectorSize];
    image.Position = (long)lba * SectorSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteSector(Stream image, int lba, byte[] data) {
    if (data.Length != SectorSize) throw new ArgumentException("sector data must be 2048 bytes", nameof(data));
    image.Position = (long)lba * SectorSize;
    image.Write(data);
  }
}
