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

  /// <summary>Undoes the preprocessing in place.</summary>
  /// <param name="data">One file as decoded.</param>
  /// <param name="swapOperandBytes">PPM streams: operands that look like targets once bytes 0 and 2 are swapped were stored swapped.</param>
  public static void Restore(Span<byte> data, bool swapOperandBytes = false) {
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

    RestoreBranches(data, start, swapOperandBytes);
  }

  /// <summary>
  /// The writer's inverse of <see cref="Restore"/>: applies the preprocessing in place so that
  /// the restore gives the file back. Returns whether the file is one the restore will touch; a
  /// file it cannot express is left alone and reported through <paramref name="encodable"/> (the
  /// restore would then damage it, so the caller must not set the filter bit).
  /// </summary>
  /// <param name="data">One file, as it will be packed.</param>
  /// <param name="encodable">False when an operand cannot be expressed.</param>
  /// <param name="swapOperandBytes">
  /// 0.6 PPM streams: store an operand with its bytes 0 and 2 swapped exactly when the restore
  /// swaps it back, that is when the value or its swap is a target.
  /// </param>
  public static bool Apply(Span<byte> data, out bool encodable, bool swapOperandBytes = false) {
    encodable = true;
    if (data.Length < MinimumLength || !Classify(data, out var withRelocations, out var start))
      return false;

    var copy = data.ToArray();
    if (withRelocations) {
      var table = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(0x18));
      var size = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(6)) * 4;
      if (table + size > copy.Length - Tail)
        return false;

      ApplyRelocations(copy.AsSpan(table, size));
      start = table + size;
    }

    if (!ApplyBranches(copy, start, swapOperandBytes)) {
      encodable = false;
      return true;
    }

    copy.CopyTo(data);
    return true;
  }

  private static void ApplyRelocations(Span<byte> table) {
    for (var i = table.Length - 2 - (table.Length & 1); i >= 4; i -= 2) {
      var value = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(table[i..]) - BinaryPrimitives.ReadUInt16LittleEndian(table[(i - 4)..]));
      BinaryPrimitives.WriteUInt16LittleEndian(table[i..], value);
    }

    var interleaved = table.ToArray();
    var write = 0;
    for (var plane = 0; plane < 4; ++plane)
      for (var i = plane; i < table.Length; i += 4)
        table[write++] = interleaved[i];
  }

  /// <summary>
  /// Walks the file as the restore will and stores each operand so that the restore's branch,
  /// its step and its list of recent targets give back the original: a target inside the file as
  /// its absolute address, one just past the end as a negative distance, a trivial high half as
  /// it is, and otherwise the low half as an index into the recent targets or as itself (an
  /// original low half that looks like an index is reached through a chain of the list's
  /// entries). False when an operand cannot be expressed.
  /// With <paramref name="swapOperandBytes"/> each stored value then has its bytes 0 and 2
  /// swapped whenever it or its swap is a target (the same condition for both, so the restore
  /// swaps exactly those back), and an operand that is otherwise left alone advances the walk by
  /// 5 when it is swapped, as the restore steps past it.
  /// </summary>
  private static bool ApplyBranches(Span<byte> data, int start, bool swapOperandBytes) {
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
      var original = BinaryPrimitives.ReadInt32LittleEndian(operand);
      if (original == 0) {
        i += 4;
        continue;
      }

      var absolute = (long)original + i;
      if (absolute > 0 && absolute < limit) {
        BinaryPrimitives.WriteInt32LittleEndian(operand, (int)absolute);
        SwapForPpm(operand, i);
        i += 5;
        continue;
      }

      if (original == -i) {
        BinaryPrimitives.WriteInt32LittleEndian(operand, i);
        SwapForPpm(operand, i);
        i += 5;
        continue;
      }

      if (original < limit && (long)original >= (long)limit - i) {
        BinaryPrimitives.WriteInt32LittleEndian(operand, original - limit);
        SwapForPpm(operand, i);
        i += 5;
        continue;
      }

      var high = BinaryPrimitives.ReadUInt16LittleEndian(operand[2..]);
      if (high == 0 || high == (ushort)((limit - 1) >> 16) || high == (ushort)(-i >> 16)) {
        i += SwapForPpm(operand, i) ? 5 : 4;
        continue;
      }

      var low = BinaryPrimitives.ReadUInt16LittleEndian(operand);
      var target = (ushort)(low + i);
      var found = recent.IndexOf(target);
      if (found >= 0) {
        BinaryPrimitives.WriteUInt16LittleEndian(operand, (ushort)(0x8000 | found));
        recent[..found].CopyTo(recent[1..(found + 1)]);
        recent[0] = target;
        SwapForPpm(operand, i);
        i += 4;
        continue;
      }

      if (low is >= 0x8000 and < 0x8080) {
        // The restore follows index-looking values through the list: store the start of a chain
        // that ends in the original.
        var stored = (ushort)(recent[low - 0x8000] - i);
        for (var hops = 0; stored is >= 0x8000 and < 0x8080; ++hops) {
          if (hops == recent.Length)
            return false;
          stored = (ushort)(recent[stored - 0x8000] - i);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(operand, stored);
      }

      if (target is < 0x8000 or >= 0x8080) {
        recent[..127].CopyTo(recent[1..]);
        recent[0] = target;
      }

      SwapForPpm(operand, i);
      i += 4;
    }

    return true;

    // Swaps the stored value's bytes 0 and 2 when the PPM restore will swap them back.
    bool SwapForPpm(Span<byte> operand, int position) {
      if (!swapOperandBytes)
        return false;
      var value = BinaryPrimitives.ReadUInt32LittleEndian(operand);
      var swapped = SwapBytes02(value);
      if (!IsTarget((int)value, position, limit) && !IsTarget((int)swapped, position, limit))
        return false;
      BinaryPrimitives.WriteUInt32LittleEndian(operand, swapped);
      return true;
    }
  }

  private static uint SwapBytes02(uint value) => (value & 0xFF00FF00) | ((value & 0xFF) << 16) | ((value >> 16) & 0xFF);

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

  /// <summary>
  /// Undoes UHARC 0.2's executable preprocessing in place: in MZ/ZM files of at least 1 KiB, every
  /// <c>E8</c>/<c>E9</c> operand that is a target within the file was made absolute.
  /// </summary>
  public static void RestoreLegacy(Span<byte> data) {
    if (data.Length < MinimumLength || BinaryPrimitives.ReadUInt16LittleEndian(data) is not (0x5A4D or 0x4D5A))
      return;

    var limit = data.Length;
    var end = data.Length - Tail;
    var i = 0;
    while (i < end) {
      if (data[i] is not (0xE8 or 0xE9)) {
        ++i;
        continue;
      }

      var operand = data.Slice(i + 1, 4);
      var value = BinaryPrimitives.ReadInt32LittleEndian(operand);
      if (value > 0 && value < limit) {
        BinaryPrimitives.WriteInt32LittleEndian(operand, (value == i ? 0 : value) - i);
        i += 5;
      } else if (value < 0 && value >= -i) {
        BinaryPrimitives.WriteInt32LittleEndian(operand, value + limit);
        i += 5;
      } else
        i += 4;
    }
  }

  /// <summary>
  /// The inverse of <see cref="RestoreLegacy"/>, for the 0.2 writer: walks the file as the
  /// restore will and stores each operand so that the restore's choice of branch, and with it the
  /// next position it looks at, gives back the original. An operand <c>o</c> at position <c>i</c>
  /// within the file (<c>0 &lt; o + i &lt; size</c>) is stored as <c>o + i</c>, <c>−i</c> as <c>i</c>,
  /// one reaching just past the end (<c>size − i ≤ o &lt; size</c>) as <c>o − size</c>; every other
  /// operand is left as it is, which the restore skips.
  /// </summary>
  public static void ApplyLegacy(Span<byte> data) {
    if (data.Length < MinimumLength || BinaryPrimitives.ReadUInt16LittleEndian(data) is not (0x5A4D or 0x4D5A))
      return;

    var limit = data.Length;
    var end = data.Length - Tail;
    var i = 0;
    while (i < end) {
      if (data[i] is not (0xE8 or 0xE9)) {
        ++i;
        continue;
      }

      var operand = data.Slice(i + 1, 4);
      var original = BinaryPrimitives.ReadInt32LittleEndian(operand);
      var absolute = (long)original + i;
      if (original != 0 && absolute > 0 && absolute < limit) {
        BinaryPrimitives.WriteInt32LittleEndian(operand, (int)absolute);
        i += 5;
      } else if (original == -i && i > 0) {
        BinaryPrimitives.WriteInt32LittleEndian(operand, i);
        i += 5;
      } else if (original < limit && (long)original >= (long)limit - i) {
        BinaryPrimitives.WriteInt32LittleEndian(operand, original - limit);
        i += 5;
      } else
        i += 4;
    }
  }

  private static bool IsTarget(int value, int position, int limit) => value >= -position && value < limit;

  private static void RestoreBranches(Span<byte> data, int start, bool swapOperandBytes) {
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

      var step = 4;
      if (swapOperandBytes) {
        var swapped = SwapBytes02(value);
        if (IsTarget((int)value, i, limit) || IsTarget((int)swapped, i, limit)) {
          BinaryPrimitives.WriteUInt32LittleEndian(operand, swapped);
          value = swapped;
          step = 5;
        }
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
        i += step;
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
