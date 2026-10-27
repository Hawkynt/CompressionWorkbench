namespace FileFormat.StuffItX;

/// <summary>
/// Block transforms shared by Cyanide and Iron: the inverse Burrows-Wheeler transform, the inverse
/// Schindler (order-4 sort) transform and the "move one from front" recency list.
/// </summary>
/// <remarks>Converted from XADMaster's <c>BWT.c</c> (LGPL-2.1-or-later, see THIRD-PARTY-NOTICE.XADMaster.txt).</remarks>
internal static class StuffItXTransforms {

  /// <summary>Inverse BWT of <paramref name="source"/> starting from row <paramref name="firstIndex"/>.</summary>
  public static void UnsortBwt(Span<byte> destination, ReadOnlySpan<byte> source, int firstIndex, uint[] transform) {
    var length = source.Length;
    Span<int> counts = stackalloc int[256];
    Span<int> cumulative = stackalloc int[256];
    counts.Clear();
    foreach (var b in source) ++counts[b];
    var total = 0;
    for (var i = 0; i < 256; ++i) {
      cumulative[i] = total;
      total += counts[i];
      counts[i] = 0;
    }
    for (var i = 0; i < length; ++i) {
      var b = source[i];
      transform[cumulative[b] + counts[b]] = (uint)i;
      ++counts[b];
    }

    var index = firstIndex;
    for (var i = 0; i < length; ++i) {
      index = (int)transform[index];
      destination[i] = source[index];
    }
  }

  /// <summary>Inverse of the order-4 sort transform (ST4); false when the indices run out of the block.</summary>
  public static bool UnsortSt4(Span<byte> destination, ReadOnlySpan<byte> source, int firstIndex, uint[] transform) {
    var length = source.Length;
    var counts = new int[256];
    var pairs = new int[256 * 256];
    foreach (var b in source) ++counts[b];

    var total = 0;
    for (var i = 0; i < 256; ++i) {
      var count = counts[i];
      counts[i] = total;
      for (var j = 0; j < count; ++j) ++pairs[(source[total + j] << 8) | i];
      total += count;
    }

    var bitvec = new byte[(length + 7) / 8];
    var groupStart = new int[256];
    Array.Fill(groupStart, -1);
    var counts2 = (int[])counts.Clone();

    total = 0;
    for (var i = 0; i < 0x10000; ++i) {
      var count = pairs[i];
      for (var j = 0; j < count; ++j) {
        var b = source[total + j];
        if (groupStart[b] != total) {
          groupStart[b] = total;
          var x = counts[b];
          bitvec[x >> 3] |= (byte)(1 << (x & 7));
        }
        ++counts[b];
      }
      total += count;
    }

    Array.Clear(groupStart);
    var index = 0;
    for (var i = 0; i < length; ++i) {
      if ((bitvec[i >> 3] & (1 << (i & 7))) != 0) index = i;
      var b = source[i];
      if (index < groupStart[b]) transform[i] = (uint)(groupStart[b] - 1) | 0x800000;
      else {
        transform[i] = (uint)counts2[b];
        groupStart[b] = i + 1;
      }
      ++counts2[b];
      transform[i] |= (uint)b << 24;
    }

    index = firstIndex;
    var value = transform[firstIndex];
    for (var i = 0; i < length; ++i) {
      if ((value & 0x800000) != 0) {
        var newIndex = (int)(value & 0x7FFFFF);
        if (newIndex >= length) return false;
        index = (int)(transform[newIndex] & 0x7FFFFF);
        ++transform[newIndex];
      } else {
        if (index >= length) return false;
        ++transform[index];
        index = (int)(value & 0x7FFFFF);
      }
      if (index >= length) return false;
      value = transform[index];
      destination[i] = (byte)(value >> 24);
    }
    return true;
  }

  /// <summary>
  /// Inverse "move one from front" coding: rank 0 keeps the head, rank 1 swaps the first two only
  /// once the head has stood for <paramref name="order"/> symbols, higher ranks move to position 1.
  /// </summary>
  public static void DecodeM1ff(Span<byte> block, int order) {
    Span<byte> table = stackalloc byte[256];
    for (var i = 0; i < 256; ++i) table[i] = (byte)i;
    var lastHead = order - 1;
    for (var i = 0; i < block.Length; ++i) {
      int symbol = block[i];
      block[i] = table[symbol];
      if (symbol == 0) lastHead = 0;
      else if (symbol == 1) {
        if (lastHead >= order) (table[0], table[1]) = (table[1], table[0]);
      } else {
        var value = table[symbol];
        for (var k = symbol; k > 1; --k) table[k] = table[k - 1];
        table[1] = value;
      }
      ++lastHead;
    }
  }
}
