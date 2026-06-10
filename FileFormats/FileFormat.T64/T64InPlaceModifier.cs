#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.T64;

/// <summary>
/// True in-place R/W modifier for Commodore 64 <c>.t64</c> tape images.
/// Performs O(touched bytes) byte-level region shifts against the raw stream
/// instead of read-extract-rebuild.
/// </summary>
/// <remarks>
/// <para>T64 layout:</para>
/// <list type="bullet">
///   <item>0..63 - 64-byte header (signature, version, max-entries, used-entries, tape name).</item>
///   <item>64..64+maxEntries*32 - directory: N * 32-byte slot records (entry type, C64 type, start/end addr, absolute data offset, filename).</item>
///   <item>64+maxEntries*32 .. EOF - concatenated file payloads addressed by each slot's absolute <c>dataOffset</c> field.</item>
/// </list>
/// <para><b>Add</b>: if a directory slot is currently free (entryType=0) the
/// new entry fills that slot and the payload is appended at EOF. If the
/// directory is full the directory grows by one 32-byte slot: every file
/// payload shifts forward by 32 bytes, every existing slot's absolute
/// <c>dataOffset</c> field is patched by +32, then the new slot is written and
/// the new payload appended at the new EOF. Header's <c>maxEntries</c> and
/// <c>usedEntries</c> are updated to match.</para>
/// <para><b>Remove</b>: shifts the later directory slots up by 32 bytes inside
/// the directory table (the vacated trailing slot is zero-filled to leave no
/// forensic trace), then wipes the removed file's payload bytes and shifts the
/// remaining payload region into the vacated payload range (updating each
/// affected slot's <c>dataOffset</c> field). The stream is truncated to the
/// new EOF. Both <c>maxEntries</c> and <c>usedEntries</c> are decremented so
/// the directory remains exactly sized.</para>
/// </remarks>
public static class T64InPlaceModifier {

  private const int HeaderSize = 64;
  private const int EntrySize = 32;
  private const int MaxEntriesOffset = 34;
  private const int UsedEntriesOffset = 36;

  /// <summary>
  /// Adds (or replaces by name, case-insensitive) a single file inside an
  /// existing T64 stream. The image is mutated in-place — no full rebuild.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data, ushort startAddress = 0x0801) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("T64: stream must be readable, writable and seekable.", nameof(image));

    // Replace-by-name semantic: drop any prior entry with the same name first.
    var existingIndex = FindEntryIndex(image, name);
    if (existingIndex >= 0)
      RemoveEntryAt(image, existingIndex);

    // Re-read header after possible removal.
    var (maxEntries, usedEntries) = ReadHeaderCounts(image);

    // Try to find a free slot first.
    var freeSlot = FindFreeSlot(image, maxEntries);
    if (freeSlot >= 0) {
      AppendDataAndFillSlot(image, freeSlot, name, data, startAddress);
      WriteHeaderCounts(image, maxEntries, (ushort)(usedEntries + 1));
      return;
    }

    // Directory is full → grow by one slot. Shift payload region right by 32
    // bytes and patch every existing slot's absolute dataOffset by +32.
    var payloadStart = (long)HeaderSize + maxEntries * EntrySize;
    var payloadLength = image.Length - payloadStart;
    ShiftRangeForward(image, payloadStart, payloadLength, EntrySize);

    // Zero-init the new slot's bytes so any future scan sees a clean slot.
    Span<byte> zero = stackalloc byte[EntrySize];
    image.Position = payloadStart;
    image.Write(zero);

    // Patch every existing slot's absolute dataOffset += EntrySize.
    Span<byte> entry = stackalloc byte[EntrySize];
    for (var i = 0; i < maxEntries; i++) {
      var slotOff = HeaderSize + i * EntrySize;
      image.Position = slotOff;
      image.ReadExactly(entry);
      if (entry[0] == 0) continue;
      var oldDataOff = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8));
      BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(8), oldDataOff + (uint)EntrySize);
      image.Position = slotOff;
      image.Write(entry);
    }

    // Write new slot at the freed gap (the now-zeroed bytes at payloadStart).
    var newSlotIndex = maxEntries;
    var newMaxEntries = (ushort)(maxEntries + 1);
    AppendDataAndFillSlot(image, newSlotIndex, name, data, startAddress);

    WriteHeaderCounts(image, newMaxEntries, (ushort)(usedEntries + 1));
  }

  /// <summary>
  /// Removes a named entry from the T64 stream. Returns true if found and
  /// removed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("T64: stream must be readable, writable and seekable.", nameof(image));

    var index = FindEntryIndex(image, name);
    if (index < 0) return false;
    RemoveEntryAt(image, index);
    return true;
  }

  // ── Internals ────────────────────────────────────────────────────────────

  private static void RemoveEntryAt(Stream image, int index) {
    var (maxEntries, usedEntries) = ReadHeaderCounts(image);
    if (index < 0 || index >= maxEntries) return;

    // Read the slot we're removing to learn the data extent.
    Span<byte> entry = stackalloc byte[EntrySize];
    var slotOff = HeaderSize + index * EntrySize;
    image.Position = slotOff;
    image.ReadExactly(entry);

    var startAddr = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(2));
    var endAddr = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(4));
    var removedDataOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8));
    var removedDataLen = endAddr > startAddr ? endAddr - startAddr : 0;
    var wasOccupied = entry[0] != 0;

    // Wipe the removed payload bytes for the forensic guarantee.
    if (wasOccupied && removedDataLen > 0)
      ZeroRange(image, removedDataOffset, removedDataLen);

    // 1) Compact directory: shift later slots up by 32 bytes.
    var afterEntryOffset = (long)HeaderSize + (index + 1) * EntrySize;
    var laterSlotsLen = (maxEntries - index - 1) * (long)EntrySize;
    if (laterSlotsLen > 0)
      ShiftRangeBackward(image, afterEntryOffset, laterSlotsLen, EntrySize);

    // 2) Compact payload region: the directory shrank by 32 bytes, so all file
    //    payloads shift back by 32. Additionally, the removed payload's bytes
    //    must be excised.
    //    Old payload region:  [oldPayloadStart .. image.Length)
    //    Within it, removedDataOffset .. removedDataOffset+removedDataLen is gone.
    //    New payload region: shifted back by 32 bytes, and the removed slice is closed up.
    var oldPayloadStart = (long)HeaderSize + maxEntries * EntrySize;
    var newPayloadStart = oldPayloadStart - EntrySize;
    var oldEnd = image.Length;

    // Sub-region A: [oldPayloadStart .. removedDataOffset) shifts back by 32.
    var aLen = (wasOccupied ? removedDataOffset : oldEnd) - oldPayloadStart;
    if (aLen > 0)
      ShiftRangeBackward(image, oldPayloadStart, aLen, EntrySize);

    // Sub-region B: [removedDataOffset+removedDataLen .. oldEnd) shifts back by
    // (32 + removedDataLen) so the removed-payload slice closes up.
    var bSrc = removedDataOffset + removedDataLen;
    var bLen = oldEnd - bSrc;
    if (wasOccupied && bLen > 0)
      ShiftRangeBackward(image, bSrc, bLen, EntrySize + removedDataLen);

    // 3) Patch every remaining slot's absolute dataOffset.
    //    Slots whose dataOffset was < removedDataOffset shift by -32.
    //    Slots whose dataOffset was > removedDataOffset shift by -(32 + removedDataLen).
    //    (The removed slot itself is already gone after directory compaction.)
    var newMaxEntries = (ushort)(maxEntries - 1);
    for (var i = 0; i < newMaxEntries; i++) {
      var so = HeaderSize + i * EntrySize;
      image.Position = so;
      image.ReadExactly(entry);
      if (entry[0] == 0) continue;
      var dataOff = (long)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8));
      long newDataOff;
      if (!wasOccupied || dataOff < removedDataOffset)
        newDataOff = dataOff - EntrySize;
      else
        newDataOff = dataOff - EntrySize - removedDataLen;
      BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(8), (uint)newDataOff);
      image.Position = so;
      image.Write(entry);
    }

    // 4) Truncate the stream to the new EOF.
    var newEnd = oldEnd - EntrySize - (wasOccupied ? removedDataLen : 0);
    image.SetLength(newEnd);

    // 5) Patch header counts.
    var newUsedEntries = wasOccupied && usedEntries > 0 ? (ushort)(usedEntries - 1) : usedEntries;
    WriteHeaderCounts(image, newMaxEntries, newUsedEntries);
  }

  /// <summary>
  /// Writes data at EOF, then fills the given directory slot with type,
  /// addresses, dataOffset and filename.
  /// </summary>
  private static void AppendDataAndFillSlot(Stream image, int slotIndex, string name, byte[] data, ushort startAddress) {
    var dataOffset = image.Length;
    image.Position = dataOffset;
    image.Write(data);

    Span<byte> entry = stackalloc byte[EntrySize];
    entry[0] = 1;       // entry type: normal
    entry[1] = 0x82;    // C64 file type: PRG
    BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(2), startAddress);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(4), (ushort)(startAddress + data.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(8), (uint)dataOffset);

    var trimmed = name.Length > 16 ? name[..16] : name;
    var nameBytes = Encoding.ASCII.GetBytes(trimmed);
    nameBytes.CopyTo(entry.Slice(16));
    for (var j = nameBytes.Length; j < 16; j++)
      entry[16 + j] = 0x20;

    image.Position = HeaderSize + slotIndex * EntrySize;
    image.Write(entry);
  }

  private static int FindFreeSlot(Stream image, int maxEntries) {
    for (var i = 0; i < maxEntries; i++) {
      image.Position = HeaderSize + i * EntrySize;
      var typeByte = image.ReadByte();
      if (typeByte == 0) return i;
    }
    return -1;
  }

  private static int FindEntryIndex(Stream image, string name) {
    var (maxEntries, _) = ReadHeaderCounts(image);
    Span<byte> entry = stackalloc byte[EntrySize];
    var trimmed = name.Length > 16 ? name[..16] : name;
    for (var i = 0; i < maxEntries; i++) {
      image.Position = HeaderSize + i * EntrySize;
      image.ReadExactly(entry);
      if (entry[0] == 0) continue;
      var entryName = Encoding.ASCII.GetString(entry.Slice(16, 16)).TrimEnd('\0', ' ');
      if (entryName.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return i;
    }
    return -1;
  }

  private static (ushort MaxEntries, ushort UsedEntries) ReadHeaderCounts(Stream image) {
    if (image.Length < HeaderSize)
      throw new InvalidDataException("T64: stream too small.");
    Span<byte> buf = stackalloc byte[4];
    image.Position = MaxEntriesOffset;
    image.ReadExactly(buf);
    return (
      BinaryPrimitives.ReadUInt16LittleEndian(buf),
      BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(2)));
  }

  private static void WriteHeaderCounts(Stream image, ushort maxEntries, ushort usedEntries) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(buf, maxEntries);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(2), usedEntries);
    image.Position = MaxEntriesOffset;
    image.Write(buf);
  }

  private static void ZeroRange(Stream image, long offset, long length) {
    if (length <= 0) return;
    var buf = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      Array.Clear(buf);
      var remaining = length;
      image.Position = offset;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buf.Length);
        image.Write(buf, 0, chunk);
        remaining -= chunk;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buf);
    }
  }

  /// <summary>
  /// Shifts bytes [src .. src+length) to [src+delta .. src+delta+length), where
  /// delta &gt; 0. Copies high-to-low so overlap doesn't corrupt the data.
  /// </summary>
  private static void ShiftRangeForward(Stream image, long src, long length, long delta) {
    if (length <= 0 || delta == 0) return;
    if (delta < 0) throw new ArgumentOutOfRangeException(nameof(delta));

    var dstEnd = src + delta + length;
    if (dstEnd > image.Length)
      image.SetLength(dstEnd);

    var buf = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buf.Length);
        var readFrom = src + remaining - chunk;
        var writeTo = readFrom + delta;
        image.Position = readFrom;
        image.ReadExactly(buf, 0, chunk);
        image.Position = writeTo;
        image.Write(buf, 0, chunk);
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
  private static void ShiftRangeBackward(Stream image, long src, long length, long delta) {
    if (length <= 0 || delta == 0) return;
    if (delta < 0) throw new ArgumentOutOfRangeException(nameof(delta));

    var buf = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      var cursor = 0L;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buf.Length);
        image.Position = src + cursor;
        image.ReadExactly(buf, 0, chunk);
        image.Position = src + cursor - delta;
        image.Write(buf, 0, chunk);
        cursor += chunk;
        remaining -= chunk;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buf);
    }
  }
}
