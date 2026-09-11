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
///   <item>64+maxEntries*32 .. EOF - file payloads addressed by each slot's absolute <c>dataOffset</c> field.</item>
/// </list>
/// <para><b>Add</b>: if a directory slot is currently free (entryType=0) the
/// new entry fills that slot and the payload is appended at EOF. If the
/// directory is full the directory grows by one 32-byte slot: every file
/// payload shifts forward by 32 bytes, every existing slot's absolute
/// <c>dataOffset</c> field is patched by +32, then the new slot is written and
/// the new payload appended at the new EOF.</para>
/// <para><b>Remove</b>: shifts the later directory slots up by 32 bytes, wipes
/// the removed payload, closes the payload gap, patches all surviving absolute
/// data offsets, and truncates the stream.</para>
/// </remarks>
public static class T64InPlaceModifier {

  private const int HeaderSize = 64;
  private const int EntrySize = 32;
  private const int MaxEntriesOffset = 34;

  /// <summary>
  /// Adds (or replaces by name, case-insensitive) a single file inside an
  /// existing T64 stream. The image is mutated in-place — no full rebuild.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data, ushort startAddress = 0x0801) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    EnsureMutable(image);
    ValidatePayload(name, startAddress, data.Length);

    // Validate the image before replacement semantics can remove anything.
    image.Position = 0;
    using (var reader = new T64Reader(image)) { }

    var existingIndex = FindEntryIndex(image, name);
    if (existingIndex >= 0)
      RemoveEntryAt(image, existingIndex);

    var (maxEntries, _) = ReadHeaderCounts(image);
    var usedEntries = CountUsedEntries(image, maxEntries);

    var freeSlot = FindFreeSlot(image, maxEntries);
    if (freeSlot >= 0) {
      AppendDataAndFillSlot(image, freeSlot, name, data, startAddress);
      WriteHeaderCounts(image, maxEntries, checked((ushort)(usedEntries + 1)));
      return;
    }

    if (maxEntries == ushort.MaxValue)
      throw new IOException("T64: directory cannot grow beyond 65535 slots.");

    // Directory is full: insert one slot immediately before the payload region.
    var payloadStart = (long)HeaderSize + maxEntries * EntrySize;
    var payloadLength = image.Length - payloadStart;
    ShiftRangeForward(image, payloadStart, payloadLength, EntrySize);

    Span<byte> zero = stackalloc byte[EntrySize];
    image.Position = payloadStart;
    image.Write(zero);

    Span<byte> entry = stackalloc byte[EntrySize];
    for (var i = 0; i < maxEntries; i++) {
      var slotOff = HeaderSize + i * EntrySize;
      image.Position = slotOff;
      image.ReadExactly(entry);
      if (entry[0] == 0) continue;
      var oldDataOff = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
      if (oldDataOff > uint.MaxValue - EntrySize)
        throw new InvalidDataException("T64: payload offset cannot be shifted into the 32-bit offset field.");
      BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], oldDataOff + EntrySize);
      image.Position = slotOff;
      image.Write(entry);
    }

    var newSlotIndex = maxEntries;
    var newMaxEntries = checked((ushort)(maxEntries + 1));
    AppendDataAndFillSlot(image, newSlotIndex, name, data, startAddress);
    WriteHeaderCounts(image, newMaxEntries, checked((ushort)(usedEntries + 1)));
  }

  /// <summary>
  /// Removes a named entry from the T64 stream. Returns true if found and
  /// removed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    EnsureMutable(image);

    image.Position = 0;
    using (var reader = new T64Reader(image)) { }

    var index = FindEntryIndex(image, name);
    if (index < 0) return false;
    RemoveEntryAt(image, index);
    return true;
  }

  private static void RemoveEntryAt(Stream image, int index) {
    var (maxEntries, _) = ReadHeaderCounts(image);
    if (index < 0 || index >= maxEntries) return;

    image.Position = 0;
    using var reader = new T64Reader(image);
    var removed = reader.Entries.FirstOrDefault(e => e.DirectoryIndex == index);
    if (removed is null) return;

    var removedDataOffset = (long)removed.DataOffset;
    var removedDataLen = removed.Size;
    var usedEntries = reader.Entries.Count;

    if (removedDataLen > 0)
      ZeroRange(image, removedDataOffset, removedDataLen);

    // 1) Compact directory: shift later slots up by 32 bytes.
    var afterEntryOffset = (long)HeaderSize + (index + 1) * EntrySize;
    var laterSlotsLen = (maxEntries - index - 1) * (long)EntrySize;
    if (laterSlotsLen > 0)
      ShiftRangeBackward(image, afterEntryOffset, laterSlotsLen, EntrySize);

    // 2) Compact payload bytes. Every payload loses the 32-byte directory slot;
    // payloads after the removed member additionally lose that member's bytes.
    var oldPayloadStart = (long)HeaderSize + maxEntries * EntrySize;
    var oldEnd = image.Length;

    var beforeRemovedLength = removedDataOffset - oldPayloadStart;
    if (beforeRemovedLength > 0)
      ShiftRangeBackward(image, oldPayloadStart, beforeRemovedLength, EntrySize);

    var afterRemovedSource = checked(removedDataOffset + removedDataLen);
    var afterRemovedLength = oldEnd - afterRemovedSource;
    if (afterRemovedLength > 0)
      ShiftRangeBackward(image, afterRemovedSource, afterRemovedLength, EntrySize + removedDataLen);

    // 3) Patch surviving absolute payload offsets after the directory move.
    Span<byte> entry = stackalloc byte[EntrySize];
    var newMaxEntries = checked((ushort)(maxEntries - 1));
    for (var i = 0; i < newMaxEntries; i++) {
      var slotOff = HeaderSize + i * EntrySize;
      image.Position = slotOff;
      image.ReadExactly(entry);
      if (entry[0] == 0) continue;

      var dataOff = (long)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
      var newDataOff = dataOff < removedDataOffset
        ? dataOff - EntrySize
        : dataOff - EntrySize - removedDataLen;
      if (newDataOff < HeaderSize + (long)newMaxEntries * EntrySize || newDataOff > uint.MaxValue)
        throw new InvalidDataException("T64: compaction produced an invalid payload offset.");

      BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)newDataOff);
      image.Position = slotOff;
      image.Write(entry);
    }

    // 4) Truncate and repair both header counts from the actual live directory.
    var newEnd = oldEnd - EntrySize - removedDataLen;
    image.SetLength(newEnd);
    WriteHeaderCounts(image, newMaxEntries, checked((ushort)(usedEntries - 1)));
  }

  private static void AppendDataAndFillSlot(Stream image, int slotIndex, string name, byte[] data, ushort startAddress) {
    var dataOffset = image.Length;
    if (dataOffset > uint.MaxValue)
      throw new IOException("T64: payload offset exceeds the 32-bit directory field.");

    image.Position = dataOffset;
    image.Write(data);

    Span<byte> entry = stackalloc byte[EntrySize];
    entry[0] = 1;
    entry[1] = 0x82;
    BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], startAddress);
    BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], unchecked((ushort)(startAddress + data.Length)));
    BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)dataOffset);

    var trimmed = name.Length > 16 ? name[..16] : name;
    var nameBytes = Encoding.ASCII.GetBytes(trimmed);
    nameBytes.CopyTo(entry[16..]);
    entry[(16 + nameBytes.Length)..32].Fill(0x20);

    image.Position = HeaderSize + slotIndex * EntrySize;
    image.Write(entry);
  }

  private static int FindFreeSlot(Stream image, int maxEntries) {
    for (var i = 0; i < maxEntries; i++) {
      image.Position = HeaderSize + i * EntrySize;
      if (image.ReadByte() == 0) return i;
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
      var entryName = Encoding.ASCII.GetString(entry[16..32]).TrimEnd('\0', ' ');
      if (entryName.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return i;
    }
    return -1;
  }

  private static int CountUsedEntries(Stream image, int maxEntries) {
    var result = 0;
    for (var i = 0; i < maxEntries; i++) {
      image.Position = HeaderSize + i * EntrySize;
      if (image.ReadByte() > 0) ++result;
    }
    return result;
  }

  private static (ushort MaxEntries, ushort UsedEntries) ReadHeaderCounts(Stream image) {
    if (image.Length < HeaderSize)
      throw new InvalidDataException("T64: stream too small.");
    Span<byte> buffer = stackalloc byte[4];
    image.Position = MaxEntriesOffset;
    image.ReadExactly(buffer);
    var maxEntries = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    var usedEntries = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..]);
    if ((long)HeaderSize + maxEntries * EntrySize > image.Length)
      throw new InvalidDataException("T64: directory extends beyond end of image.");
    return (maxEntries, usedEntries);
  }

  private static void WriteHeaderCounts(Stream image, ushort maxEntries, ushort usedEntries) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(buffer, maxEntries);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..], usedEntries);
    image.Position = MaxEntriesOffset;
    image.Write(buffer);
  }

  private static void EnsureMutable(Stream image) {
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("T64: stream must be readable, writable and seekable.", nameof(image));
  }

  private static void ValidatePayload(string name, ushort startAddress, int length) {
    if (startAddress + (long)length > 0x10000)
      throw new InvalidOperationException(
        $"T64: '{name}' is {length:N0} bytes and loads at ${startAddress:X4}, past the C64 64 KB address space.");
  }

  private static void ZeroRange(Stream image, long offset, long length) {
    if (length <= 0) return;
    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      Array.Clear(buffer, 0, buffer.Length);
      var remaining = length;
      image.Position = offset;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        image.Write(buffer, 0, chunk);
        remaining -= chunk;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  private static void ShiftRangeForward(Stream image, long src, long length, long delta) {
    if (length <= 0 || delta == 0) return;
    if (delta < 0) throw new ArgumentOutOfRangeException(nameof(delta));

    var dstEnd = checked(src + delta + length);
    if (dstEnd > image.Length)
      image.SetLength(dstEnd);

    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        var readFrom = src + remaining - chunk;
        image.Position = readFrom;
        image.ReadExactly(buffer, 0, chunk);
        image.Position = readFrom + delta;
        image.Write(buffer, 0, chunk);
        remaining -= chunk;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  private static void ShiftRangeBackward(Stream image, long src, long length, long delta) {
    if (length <= 0 || delta == 0) return;
    if (delta < 0) throw new ArgumentOutOfRangeException(nameof(delta));

    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      var cursor = 0L;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        image.Position = src + cursor;
        image.ReadExactly(buffer, 0, chunk);
        image.Position = src + cursor - delta;
        image.Write(buffer, 0, chunk);
        cursor += chunk;
        remaining -= chunk;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }
}
