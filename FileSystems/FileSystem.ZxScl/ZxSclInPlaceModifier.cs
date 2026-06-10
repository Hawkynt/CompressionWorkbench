#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ZxScl;

/// <summary>
/// True in-place R/W modifier for ZX Spectrum <c>.scl</c> TR-DOS archives.
/// Performs O(touched bytes) byte-level region shifts against the raw stream
/// instead of read-extract-rebuild.
/// </summary>
/// <remarks>
/// <para>SCL layout:</para>
/// <list type="bullet">
///   <item>0..7  - "SINCLAIR" magic.</item>
///   <item>8     - 1-byte file count N.</item>
///   <item>9..9+N*14 - directory: N * 14-byte entries (8-char name, 1 type, 2 param1, 2 param2, 1 length-sectors).</item>
///   <item>9+N*14 .. end-4 - concatenated sector-padded payloads (each entry contributes LengthSectors*256 bytes).</item>
///   <item>end-4 .. end - 32-bit little-endian sum of every preceding byte.</item>
/// </list>
/// <para><b>Add</b>: shifts the payload region right by one 14-byte slot, fills
/// the freed gap with the new directory entry, appends the new sector-padded
/// data, bumps the count, recomputes the trailing CRC.</para>
/// <para><b>Remove</b>: shifts later directory entries up by 14 bytes, shifts
/// the remaining payload region back by 14 bytes (one less slot in the
/// directory) and by the removed payload's sector-padded length, truncates the
/// stream, decrements the count and recomputes the trailing CRC.</para>
/// </remarks>
public static class ZxSclInPlaceModifier {

  private const int MagicSize = 8;
  private const int CountOffset = 8;
  private const int DirectoryStart = 9;
  private const int EntrySize = ZxSclReader.HeaderSize;   // 14
  private const int SectorSize = ZxSclReader.SectorSize;  // 256
  private const int CrcSize = 4;
  private const int MaxEntries = ZxSclWriter.MaxEntries;

  /// <summary>
  /// Adds (or replaces by name, case-insensitive) a single file inside an
  /// existing SCL stream. The image is mutated in-place — no full rebuild.
  /// </summary>
  public static void AddFile(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("SCL: stream must be readable, writable and seekable.", nameof(archive));

    var (baseName, fileType) = NormaliseName(name);

    // Replace-by-name semantic: if an entry with that name + type already exists,
    // remove it first so the new bytes win without leaving stale slot data behind.
    var existingIndex = FindEntryIndex(archive, baseName, fileType);
    if (existingIndex >= 0)
      RemoveEntryAt(archive, existingIndex);

    var (count, _, payloadStart, payloadLength) = ReadGeometry(archive);
    if (count >= MaxEntries)
      throw new IOException($"SCL: cannot add more files ({MaxEntries}-entry directory full).");

    // Pad payload to sector boundary.
    var sectors = (data.Length + SectorSize - 1) / SectorSize;
    if (sectors > 255)
      throw new IOException($"SCL: file '{name}' requires {sectors} sectors; TR-DOS max is 255.");
    if (sectors == 0) sectors = 1;
    var padded = new byte[sectors * SectorSize];
    if (data.Length > 0) Buffer.BlockCopy(data, 0, padded, 0, data.Length);

    // 1) Shift existing payload region right by 14 bytes to open a new directory slot.
    //    Source: [payloadStart .. payloadStart + payloadLength)
    //    Dest:   [payloadStart + 14 .. payloadStart + 14 + payloadLength)
    //    Overlap with dest > source by 14 bytes → use high-to-low forward shift.
    var newCount = count + 1;
    var newPayloadStart = payloadStart + EntrySize;
    ShiftRangeForward(archive, payloadStart, payloadLength, EntrySize);

    // 2) Write the new directory entry at the freed gap (old payloadStart).
    Span<byte> entry = stackalloc byte[EntrySize];
    for (var j = 0; j < 8; j++)
      entry[j] = (byte)(j < baseName.Length ? baseName[j] : ' ');
    entry[8] = (byte)fileType;
    // param1: default TR-DOS start / BASIC autorun line (matches writer default).
    BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(9), (ushort)0x8000);
    // param2: length in bytes (matches writer default behaviour).
    BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(11), (ushort)Math.Min(data.Length, 0xFFFF));
    entry[13] = (byte)sectors;

    archive.Position = payloadStart;  // freed slot location
    archive.Write(entry);

    // 3) Append the new sector-padded payload at the end of the shifted payload region.
    var newDataOffset = newPayloadStart + payloadLength;
    archive.Position = newDataOffset;
    archive.Write(padded);

    // 4) Patch count byte.
    archive.Position = CountOffset;
    archive.WriteByte((byte)newCount);

    // 5) Recompute and write trailing CRC. Image length grew by 14 + padded.Length.
    var newImageLength = archive.Length + 0;  // no truncation here; ShiftRangeForward already grew the stream.
    // Verify: ShiftRangeForward grew the stream to fit shifted bytes (payloadStart + 14 + payloadLength).
    // After our payload append, stream is now at newDataOffset + padded.Length. CRC sits past that.
    var newEnd = newDataOffset + padded.Length;
    archive.SetLength(newEnd + CrcSize);
    WriteCrc(archive, newEnd);
  }

  /// <summary>
  /// Removes a named entry from the SCL stream. Matching is case-insensitive
  /// against the display name (with TR-DOS extension) or the raw 8-char base
  /// name. Returns true if an entry was found and removed.
  /// </summary>
  public static bool RemoveFile(Stream archive, string name) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("SCL: stream must be readable, writable and seekable.", nameof(archive));

    var (baseName, fileType) = NormaliseName(name);
    var index = FindEntryIndex(archive, baseName, fileType);
    if (index < 0) {
      // Fall back: ignore the type and match by base name alone.
      index = FindEntryIndex(archive, baseName, fileType: null);
      if (index < 0) return false;
    }
    RemoveEntryAt(archive, index);
    return true;
  }

  // ── Internals ────────────────────────────────────────────────────────────

  private static void RemoveEntryAt(Stream archive, int index) {
    var (count, sectorsByEntry, payloadStart, payloadLength) = ReadGeometry(archive);
    if (index < 0 || index >= count)
      throw new ArgumentOutOfRangeException(nameof(index));

    // Cumulative payload offset for entry `index` (relative to payloadStart).
    long preceedingDataBytes = 0;
    for (var i = 0; i < index; i++)
      preceedingDataBytes += sectorsByEntry[i] * (long)SectorSize;
    var removedDataBytes = sectorsByEntry[index] * (long)SectorSize;

    // 1) Compact directory: shift later entries up by 14 bytes.
    //    Source: [DirectoryStart + (index+1)*14 .. DirectoryStart + count*14)
    //    Dest:   [DirectoryStart +  index   *14 .. DirectoryStart + (count-1)*14)
    var afterEntryOffset = DirectoryStart + (long)(index + 1) * EntrySize;
    var laterEntriesLen = (count - index - 1) * (long)EntrySize;
    if (laterEntriesLen > 0)
      ShiftRangeBackward(archive, afterEntryOffset, laterEntriesLen, EntrySize);

    // 2) Compact payload:
    //    - bytes [payloadStart .. payloadStart + preceedingDataBytes) shift back 14 bytes
    //      (directory shrank by one slot).
    //    - bytes [payloadStart + preceedingDataBytes + removedDataBytes .. payloadStart + payloadLength)
    //      shift back (14 + removedDataBytes) bytes.
    if (preceedingDataBytes > 0)
      ShiftRangeBackward(archive, payloadStart, preceedingDataBytes, EntrySize);

    var afterRemovedStart = payloadStart + preceedingDataBytes + removedDataBytes;
    var afterRemovedLen = payloadLength - preceedingDataBytes - removedDataBytes;
    if (afterRemovedLen > 0)
      ShiftRangeBackward(archive, afterRemovedStart, afterRemovedLen, EntrySize + removedDataBytes);

    // 3) Patch count byte.
    archive.Position = CountOffset;
    archive.WriteByte((byte)(count - 1));

    // 4) Truncate stream and rewrite CRC.
    var newPayloadEnd = (payloadStart - EntrySize) + (payloadLength - removedDataBytes);
    archive.SetLength(newPayloadEnd + CrcSize);
    WriteCrc(archive, newPayloadEnd);
  }

  private static int FindEntryIndex(Stream archive, string baseName, char? fileType) {
    archive.Position = CountOffset;
    var count = archive.ReadByte();
    if (count <= 0) return -1;

    Span<byte> entry = stackalloc byte[EntrySize];
    for (var i = 0; i < count; i++) {
      archive.Position = DirectoryStart + i * EntrySize;
      archive.ReadExactly(entry);
      var nameLen = 8;
      while (nameLen > 0 && (entry[nameLen - 1] == 0x20 || entry[nameLen - 1] == 0x00))
        nameLen--;
      var name = Encoding.ASCII.GetString(entry.Slice(0, nameLen));
      var type = (char)entry[8];
      if (!name.Equals(baseName, StringComparison.OrdinalIgnoreCase)) continue;
      if (fileType is char ft && type != ft) continue;
      return i;
    }

    return -1;
  }

  /// <summary>
  /// Reads enough of the directory to compute (count, per-entry sector counts,
  /// payload start offset, total payload byte length).
  /// </summary>
  private static (int Count, byte[] SectorsByEntry, long PayloadStart, long PayloadLength) ReadGeometry(Stream archive) {
    if (archive.Length < MagicSize + 1 + CrcSize)
      throw new InvalidDataException("SCL: stream too small.");

    archive.Position = 0;
    Span<byte> magicBuf = stackalloc byte[MagicSize];
    archive.ReadExactly(magicBuf);
    for (var i = 0; i < MagicSize; i++)
      if (magicBuf[i] != ZxSclReader.Magic[i])
        throw new InvalidDataException("SCL: missing SINCLAIR magic.");

    var count = archive.ReadByte();
    if (count < 0) throw new InvalidDataException("SCL: truncated count byte.");

    var sectorsByEntry = new byte[count];
    Span<byte> entry = stackalloc byte[EntrySize];
    var totalSectors = 0L;
    for (var i = 0; i < count; i++) {
      archive.Position = DirectoryStart + i * EntrySize;
      archive.ReadExactly(entry);
      sectorsByEntry[i] = entry[13];
      totalSectors += entry[13];
    }

    var payloadStart = DirectoryStart + (long)count * EntrySize;
    var payloadLength = totalSectors * SectorSize;
    var expectedEnd = payloadStart + payloadLength + CrcSize;
    if (expectedEnd > archive.Length)
      throw new InvalidDataException(
        $"SCL: directory says {payloadLength} payload bytes but stream is only {archive.Length}.");

    return (count, sectorsByEntry, payloadStart, payloadLength);
  }

  /// <summary>
  /// Shifts bytes [src .. src+length) to [src+delta .. src+delta+length), where
  /// delta &gt; 0. Copies high-to-low so the overlapping range doesn't corrupt
  /// itself. Grows the stream if the destination range extends past EOF.
  /// </summary>
  private static void ShiftRangeForward(Stream archive, long src, long length, long delta) {
    if (length <= 0 || delta == 0) return;
    if (delta < 0) throw new ArgumentOutOfRangeException(nameof(delta));

    var dstEnd = src + delta + length;
    if (dstEnd > archive.Length)
      archive.SetLength(dstEnd);

    var buf = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buf.Length);
        var readFrom = src + remaining - chunk;
        var writeTo = readFrom + delta;
        archive.Position = readFrom;
        archive.ReadExactly(buf, 0, chunk);
        archive.Position = writeTo;
        archive.Write(buf, 0, chunk);
        remaining -= chunk;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buf);
    }
  }

  /// <summary>
  /// Shifts bytes [src .. src+length) to [src-delta .. src-delta+length), where
  /// delta &gt; 0. Copies low-to-high since destination &lt; source.
  /// </summary>
  private static void ShiftRangeBackward(Stream archive, long src, long length, long delta) {
    if (length <= 0 || delta == 0) return;
    if (delta < 0) throw new ArgumentOutOfRangeException(nameof(delta));

    var buf = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      var cursor = 0L;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buf.Length);
        archive.Position = src + cursor;
        archive.ReadExactly(buf, 0, chunk);
        archive.Position = src + cursor - delta;
        archive.Write(buf, 0, chunk);
        cursor += chunk;
        remaining -= chunk;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buf);
    }
  }

  /// <summary>
  /// Computes the 32-bit little-endian sum of bytes [0 .. preCrcEnd) and writes
  /// it at offset preCrcEnd (the 4-byte CRC trailer).
  /// </summary>
  private static void WriteCrc(Stream archive, long preCrcEnd) {
    var sum = 0u;
    archive.Position = 0;
    var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
    try {
      var remaining = preCrcEnd;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buf.Length);
        archive.ReadExactly(buf, 0, chunk);
        for (var i = 0; i < chunk; i++) sum += buf[i];
        remaining -= chunk;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buf);
    }

    Span<byte> crcBuf = stackalloc byte[CrcSize];
    BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, sum);
    archive.Position = preCrcEnd;
    archive.Write(crcBuf);
  }

  /// <summary>
  /// Normalises a caller-supplied name into an 8-char TR-DOS base name and
  /// type character. Mirrors <see cref="ZxSclWriter"/>'s SanitizeName so a
  /// name added in-place matches what the writer would emit.
  /// </summary>
  private static (string BaseName, char Type) NormaliseName(string raw) {
    const char defaultType = 'C';
    if (string.IsNullOrEmpty(raw)) return ("UNNAMED", defaultType);
    var file = Path.GetFileName(raw);
    var dot = file.LastIndexOf('.');
    string baseName;
    var type = defaultType;
    if (dot > 0) {
      baseName = file[..dot];
      var ext = file[(dot + 1)..].ToUpperInvariant();
      type = ext switch {
        "BAS" => 'B',
        "COD" => 'C',
        "DAT" => 'D',
        "SEQ" => '#',
        _ => defaultType,
      };
    } else {
      baseName = file;
    }

    var chars = new char[baseName.Length];
    for (var i = 0; i < baseName.Length; i++) {
      var c = baseName[i];
      chars[i] = (c >= 0x20 && c < 0x7F) ? c : '_';
    }
    var clean = new string(chars);
    if (clean.Length > 8) clean = clean[^8..];
    if (clean.Length == 0) clean = "UNNAMED";
    return (clean, type);
  }
}
