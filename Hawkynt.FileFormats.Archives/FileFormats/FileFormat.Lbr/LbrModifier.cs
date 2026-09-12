#pragma warning disable CS1591
namespace FileFormat.Lbr;

/// <summary>
/// Random-access in-place modifier for CP/M LBR archives. The first
/// directory entry is self-referencing and reserves a fixed pool of
/// 32-byte directory slots; data sectors follow the directory.
/// Add reuses a deleted slot and appends data at the next free
/// sector. Remove marks the slot deleted (status 0xFE) and optionally
/// wipes the underlying data sectors.
/// </summary>
/// <remarks>
/// <para>Limitations: <see cref="AddFile"/> requires a free directory
/// slot (one with status 0xFE or never used). When the pre-allocated
/// directory pool is exhausted the operation throws — growing the
/// directory would shift all data sectors, which is a full rebuild
/// (use <see cref="LbrWriter"/> instead).</para>
/// </remarks>
public static class LbrModifier {

  /// <summary>
  /// Appends a file to the archive. Reuses a deleted directory slot;
  /// data is placed at the next free sector run.
  /// </summary>
  public static void AddFile(Stream lbr, string name, byte[] data, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(lbr);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var (dirSlots, slots) = ReadDirectory(lbr);

    // Find a free slot (skip slot 0 which is the self-referencing dir entry).
    var freeSlotIdx = -1;
    for (var i = 1; i < dirSlots; i++) {
      if (!slots[i].IsActive || slots[i].SectorCount == 0) {
        freeSlotIdx = i;
        break;
      }
    }
    if (freeSlotIdx < 0)
      throw new InvalidOperationException(
        "LBR directory is full; cannot add without rebuilding the archive.");

    // Compute placement: end of current data region. Find the highest
    // (sectorOffset + sectorCount) across all active entries. Fall back
    // to the end of the directory.
    ushort dataStartSector = (ushort)(slots[0].SectorCount);
    var maxEnd = (ushort)dataStartSector;
    for (var i = 1; i < dirSlots; i++) {
      if (!slots[i].IsActive) continue;
      var end = (ushort)(slots[i].SectorOffset + slots[i].SectorCount);
      if (end > maxEnd) maxEnd = end;
    }

    var sectorCount = (ushort)((data.Length + LbrConstants.SectorSize - 1) / LbrConstants.SectorSize);
    if (sectorCount == 0) sectorCount = 1;
    var padCount = (byte)((sectorCount * LbrConstants.SectorSize) - data.Length);

    // Truncate any trailing junk past the existing data region so the new
    // entry sits cleanly at maxEnd. (Existing payload is preserved.)
    var dataOffset = (long)maxEnd * LbrConstants.SectorSize;
    if (lbr.Length > dataOffset)
      lbr.SetLength(dataOffset);

    lbr.Position = dataOffset;
    lbr.Write(data);
    if (padCount > 0) {
      Span<byte> pad = stackalloc byte[LbrConstants.SectorSize];
      pad[..padCount].Fill(LbrConstants.FillByte);
      lbr.Write(pad[..padCount]);
    }

    // Compute CRC-16 (CCITT) for the data including pad bytes if any.
    var crc16 = ComputeCrc16(data);

    var entry = new LbrEntry {
      FileName = name,
      Status = LbrConstants.StatusActive,
      SectorOffset = maxEnd,
      SectorCount = sectorCount,
      Crc16 = crc16,
      PadCount = padCount,
      ModifiedDate = lastModified,
    };
    Span<byte> entryBuf = stackalloc byte[LbrConstants.DirectoryEntrySize];
    entry.WriteTo(entryBuf);
    lbr.Position = (long)freeSlotIdx * LbrConstants.DirectoryEntrySize;
    lbr.Write(entryBuf);
  }

  /// <summary>
  /// Removes a named entry. Returns true if found. Marks the directory
  /// slot deleted (status 0xFE) and optionally wipes the data sectors.
  /// </summary>
  public static bool RemoveFile(Stream lbr, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(lbr);
    ArgumentNullException.ThrowIfNull(name);

    var (dirSlots, slots) = ReadDirectory(lbr);

    for (var i = 1; i < dirSlots; i++) {
      var entry = slots[i];
      if (!entry.IsActive) continue;
      if (!string.Equals(entry.FileName, name, StringComparison.OrdinalIgnoreCase))
        continue;

      if (wipeData && entry.SectorCount > 0) {
        var dataLen = (long)entry.SectorCount * LbrConstants.SectorSize;
        var dataOff = (long)entry.SectorOffset * LbrConstants.SectorSize;
        if (dataOff + dataLen <= lbr.Length)
          ZeroRange(lbr, dataOff, dataLen);
      }

      // Mark slot deleted: status 0xFE, rest zeroed.
      Span<byte> deleted = stackalloc byte[LbrConstants.DirectoryEntrySize];
      deleted.Clear();
      deleted[0] = LbrConstants.StatusDeleted;
      lbr.Position = (long)i * LbrConstants.DirectoryEntrySize;
      lbr.Write(deleted);
      return true;
    }
    return false;
  }

  // ── Directory walking ──────────────────────────────────────────────────

  private static (int DirSlots, LbrEntry[] Slots) ReadDirectory(Stream lbr) {
    if (lbr.Length < LbrConstants.DirectoryEntrySize)
      throw new InvalidDataException("LBR file is too small.");

    lbr.Position = 0;
    Span<byte> buf = stackalloc byte[LbrConstants.DirectoryEntrySize];
    ReadFully(lbr, buf);
    var dirEntry = LbrEntry.Parse(buf);
    if (dirEntry.Status != LbrConstants.StatusActive || dirEntry.SectorOffset != 0 ||
        dirEntry.SectorCount == 0)
      throw new InvalidDataException("Not a valid LBR archive: malformed self-referencing dir entry.");

    var dirSlots = (dirEntry.SectorCount * LbrConstants.SectorSize) / LbrConstants.DirectoryEntrySize;
    var slots = new LbrEntry[dirSlots];
    slots[0] = dirEntry;
    for (var i = 1; i < dirSlots; i++) {
      lbr.Position = (long)i * LbrConstants.DirectoryEntrySize;
      ReadFully(lbr, buf);
      slots[i] = LbrEntry.Parse(buf);
    }
    return (dirSlots, slots);
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  /// <summary>
  /// Computes CRC-16 (CCITT, polynomial 0x1021, initial 0x0000) used by LBR.
  /// </summary>
  private static ushort ComputeCrc16(byte[] data) {
    return Crc16Ccitt.Compute(data);
  }

  private static void ReadFully(Stream s, Span<byte> buf) {
    var read = 0;
    while (read < buf.Length) {
      var n = s.Read(buf[read..]);
      if (n <= 0) throw new InvalidDataException("Unexpected end of LBR stream.");
      read += n;
    }
  }

  private static void ZeroRange(Stream s, long offset, long length) {
    var buf = new byte[(int)Math.Min(length, 8192)];
    s.Position = offset;
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buf.Length, remaining);
      s.Write(buf, 0, chunk);
      remaining -= chunk;
    }
  }
}

/// <summary>
/// Minimal CRC-16/CCITT (poly 0x1021, init 0x0000) used by LBR
/// directory entries. Implemented here to avoid a hard dependency
/// on a specific Compression.Core helper.
/// </summary>
internal static class Crc16Ccitt {
  public static ushort Compute(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (var b in data) {
      crc ^= (ushort)(b << 8);
      for (var i = 0; i < 8; i++)
        crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
    }
    return crc;
  }
}
