#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.UefiFv;

internal static class UefiFvParser {
  internal static UefiFvLayout Parse(ReadOnlySpan<byte> data, int start) {
    if (start < 0 || start > data.Length - 56)
      throw new InvalidDataException("UefiFv: file shorter than minimum FV header.");
    if (!data.Slice(start + UefiFvReader.SignatureOffset, 4).SequenceEqual(UefiFvReader.Signature))
      throw new InvalidDataException($"UefiFv: '_FVH' signature not found at offset {start + UefiFvReader.SignatureOffset}.");

    var fsGuid = new Guid(data.Slice(start + 16, 16));
    var length64 = BinaryPrimitives.ReadUInt64LittleEndian(data[(start + 32)..]);
    if (length64 < 56 || length64 > int.MaxValue)
      throw new NotSupportedException($"UefiFv: unsupported firmware-volume length {length64} bytes.");
    var end = checked(start + (int)length64);
    if (end > data.Length) throw new InvalidDataException("UefiFv: firmware volume extends past the image.");

    var attributes = BinaryPrimitives.ReadUInt32LittleEndian(data[(start + 44)..]);
    var erasePolarity = (attributes & UefiFvConstants.ErasePolarityMask) != 0;
    var eraseByte = erasePolarity ? (byte)0xFF : (byte)0x00;
    var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(start + 48)..]);
    if (headerLength < 56 || start + headerLength > end) throw new InvalidDataException("UefiFv: invalid header length.");
    var dataStart = start + headerLength;
    int? usedSizeOffset = null;
    var signed = false;

    var extOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[(start + 52)..]);
    if (extOffset != 0) {
      var ext = checked(start + extOffset);
      if (ext < start || ext > end - 20) throw new InvalidDataException("UefiFv: invalid extended-header offset.");
      var extSizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(data[(ext + 16)..]);
      if (extSizeRaw < 20 || extSizeRaw > int.MaxValue) throw new InvalidDataException("UefiFv: invalid extended-header size.");
      var extEnd = checked(ext + (int)extSizeRaw);
      if (extEnd > end) throw new InvalidDataException("UefiFv: extended header extends past the volume.");
      dataStart = Math.Max(dataStart, extEnd);
      for (var p = ext + 20; p + 4 <= extEnd;) {
        var entrySize = BinaryPrimitives.ReadUInt16LittleEndian(data[p..]);
        var entryType = BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 2)..]);
        if (entrySize < 4 || p + entrySize > extEnd) throw new InvalidDataException("UefiFv: invalid extension entry.");
        if (entryType == 3 && entrySize >= 8) usedSizeOffset = p + 4;
        if (entryType == 2 && entrySize >= 20 && new Guid(data.Slice(p + 4, 16)) == UefiFvConstants.SignedContentsGuid)
          signed = true;
        p += entrySize;
      }
    }

    dataStart = UefiFvConstants.AlignOffset(start, dataStart);
    if (dataStart > end) throw new InvalidDataException("UefiFv: file area lies beyond the volume.");
    var slots = ReadSlots(data, start, end, dataStart, erasePolarity, eraseByte);
    return new UefiFvLayout(start, end, dataStart, fsGuid, attributes, erasePolarity, eraseByte, usedSizeOffset, signed, slots);
  }

  internal static IEnumerable<UefiFvSlot> LiveSlots(UefiFvLayout volume, bool includePad = true) {
    foreach (var slot in volume.Slots)
      if ((includePad || !slot.IsPad) && IsLive(volume, slot)) yield return slot;
  }

  internal static bool IsLive(UefiFvLayout volume, UefiFvSlot slot) {
    if (slot.State == UefiFvConstants.DataValid) return true;
    if (slot.State != UefiFvConstants.MarkedForUpdate) return false;
    return !volume.Slots.Any(other => other.Offset > slot.Offset && other.Name == slot.Name && other.State == UefiFvConstants.DataValid);
  }

  internal static bool IsDiscardable(UefiFvLayout volume, UefiFvSlot slot)
    => slot.State is UefiFvConstants.Deleted or UefiFvConstants.HeaderInvalid
       || slot.State == UefiFvConstants.MarkedForUpdate && !IsLive(volume, slot);

  internal static bool IsErased(ReadOnlySpan<byte> data, byte eraseByte) {
    foreach (var value in data) if (value != eraseByte) return false;
    return true;
  }

  internal static void WriteUsedSize(Span<byte> image, UefiFvLayout volume, int usedEnd) {
    if (volume.UsedSizeOffset is { } offset)
      BinaryPrimitives.WriteUInt32LittleEndian(image[offset..], checked((uint)(usedEnd - volume.Start)));
  }

  private static List<UefiFvSlot> ReadSlots(ReadOnlySpan<byte> data, int start, int end, int dataStart,
    bool erasePolarity, byte eraseByte) {
    var result = new List<UefiFvSlot>();
    for (var p = dataStart; p + 24 <= end;) {
      var quantum = Math.Min(8, end - p);
      if (IsErased(data.Slice(p, quantum), eraseByte)) { p += quantum; continue; }
      var attr = data[p + 19];
      var large = (attr & UefiFvConstants.LargeFile) != 0;
      var headerSize = large ? 32 : 24;
      if (p + headerSize > end) throw new InvalidDataException("UefiFv: truncated FFS header.");
      var size24 = (uint)(data[p + 20] | (data[p + 21] << 8) | (data[p + 22] << 16));
      var size64 = large ? BinaryPrimitives.ReadUInt64LittleEndian(data[(p + 24)..]) : size24;
      if (large && size24 != 0) throw new InvalidDataException("UefiFv: large FFS header has non-zero Size[3].");
      if (size64 < (ulong)headerSize || size64 > int.MaxValue || p + (long)size64 > end)
        throw new InvalidDataException($"UefiFv: invalid FFS size at FV offset 0x{p - start:X}.");
      var size = (int)size64;
      var aligned = UefiFvConstants.Align8(size);
      var footprint = p + (long)aligned <= end ? aligned : size;
      var rawState = data[p + 23];
      result.Add(new UefiFvSlot(p, headerSize, size, footprint, new Guid(data.Slice(p, 16)), data[p + 18], attr,
        rawState, UefiFvConstants.EffectiveState(rawState, erasePolarity)));
      p = checked(p + footprint);
    }
    return result;
  }
}
