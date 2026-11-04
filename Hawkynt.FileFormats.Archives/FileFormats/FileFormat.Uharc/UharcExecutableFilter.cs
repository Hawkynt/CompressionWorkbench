using System.Buffers.Binary;

namespace FileFormat.Uharc;

/// <summary>
/// Undoes UHARC's executable preprocessing (docs/UHARC-ON-DISK.md, "Executable filter"):
/// the MZ relocation table is stored as delta-coded byte planes, and the operands of
/// <c>E8</c>/<c>E9</c> (call/jmp rel32) were turned into absolute addresses, with high
/// 16-bit halves of far targets replaced by indices into a 128-entry move-to-front list.
/// </summary>
internal static class UharcExecutableFilter {

  private const int Tail = 15;
  private const int MinimumLength = 0x400;

  public static void Restore(Span<byte> data) {
    if (data.Length < MinimumLength || !Classify(data, out var withRelocations, out var start))
      return;

    if (withRelocations) {
      var table = BinaryPrimitives.ReadUInt16LittleEndian(data[0x18..]);
      var size = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) * 4;
      if (table + size > data.Length - Tail)
        return;

      RestoreRelocations(data.Slice(table, size));
      start = table + size;
    }

    RestoreBranches(data, start);
  }

  private static bool Classify(ReadOnlySpan<byte> data, out bool withRelocations, out int start) {
    withRelocations = false;
    start = 0;
    var signature = BinaryPrimitives.ReadUInt16LittleEndian(data);
    if (signature is 0x5A4D or 0x4D5A or 0x514D) {
      var relocations = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
      if (relocations is < 0x10 or > 0x2000) {
        start = 8;
        return true;
      }

      var table = data.Length >= 0x1A ? BinaryPrimitives.ReadUInt16LittleEndian(data[0x18..]) : 0;
      withRelocations = table >= 0x1C && table + relocations * 4 < data.Length;
      start = 0x1A;
      return true;
    }

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
    if (magic == 0x464C457F) {
      start = 0x14;
      return data.Length > 0x13 && data[0x12] == 3 && data[0x13] == 0;
    }

    return magic == 0x564F4246;
  }

  private static void RestoreRelocations(Span<byte> table) {
    var planes = table.ToArray();
    var read = 0;
    for (var plane = 0; plane < 4; ++plane)
      for (var i = plane; i < table.Length; i += 4)
        table[i] = planes[read++];

    for (var i = 4; i < table.Length; i += 2) {
      var value = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(table[i..]) + BinaryPrimitives.ReadUInt16LittleEndian(table[(i - 4)..]));
      BinaryPrimitives.WriteUInt16LittleEndian(table[i..], value);
    }
  }

  private static void RestoreBranches(Span<byte> data, int start) {
    var limit = data.Length;
    Span<ushort> recent = stackalloc ushort[128];
    for (var k = 0; k < recent.Length; ++k)
      recent[k] = (ushort)k;
    var end = data.Length - Tail;
    var i = start;
    while (i < end) {
      if (data[i] is not (0xE8 or 0xE9)) {
        ++i;
        continue;
      }

      var operand = data.Slice(i + 1, 4);
      var value = BinaryPrimitives.ReadUInt32LittleEndian(operand);
      if (value == 0) {
        i += 4;
        continue;
      }

      var signed = (int)value;
      if (signed > 0 && value < (uint)limit) {
        BinaryPrimitives.WriteUInt32LittleEndian(operand, (value == (uint)i ? 0u : value) - (uint)i);
        i += 5;
        continue;
      }

      if (signed < 0 && signed >= -i) {
        BinaryPrimitives.WriteUInt32LittleEndian(operand, value + (uint)limit);
        i += 5;
        continue;
      }

      var high = BinaryPrimitives.ReadUInt16LittleEndian(operand[2..]);
      if (high == 0 || high == (ushort)((limit - 1) >> 16) || high == (ushort)(-i >> 16)) {
        i += 4;
        continue;
      }

      var low = BinaryPrimitives.ReadUInt16LittleEndian(operand);
      if (low is >= 0x8000 and < 0x8080) {
        var index = low - 0x8000;
        var target = recent[index];
        BinaryPrimitives.WriteUInt16LittleEndian(operand, (ushort)((short)target - i));
        recent[..index].CopyTo(recent[1..(index + 1)]);
        recent[0] = target;
        i += 4;
        continue;
      }

      var key = (ushort)(low + i);
      while (true) {
        var found = recent.IndexOf(key);
        if (found >= 0) {
          BinaryPrimitives.WriteUInt16LittleEndian(operand, (ushort)(0x8000 | found));
          key = (ushort)((0x8000 | found) + i);
          continue;
        }

        if (key is < 0x8000 or >= 0x8080) {
          recent[..127].CopyTo(recent[1..]);
          recent[0] = key;
        }

        break;
      }

      i += 4;
    }
  }
}
