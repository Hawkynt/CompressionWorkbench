// ACE Huffman trees, converted from acefile (https://github.com/droe/acefile, BSD-2-Clause,
// Copyright (c) 2017-2026 Daniel Roethlisberger and contributors). See THIRD-PARTY-NOTICE.acefile.txt.

namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// A canonical-ish ACE Huffman tree: a lookup table indexed by the next <see cref="MaxWidth"/> bits,
/// plus the code width of every symbol.
/// </summary>
internal sealed class AceHuffmanTree {
  private readonly int[] _codes;
  public int[] Widths { get; }
  public int MaxWidth { get; }

  public AceHuffmanTree(int[] codes, int[] widths, int maxWidth) {
    this._codes = codes;
    this.Widths = widths;
    this.MaxWidth = maxWidth;
  }

  public int ReadSymbol(AceBitStream bs) {
    var code = bs.PeekBits(this.MaxWidth);
    if (code >= this._codes.Length)
      throw new InvalidDataException("ACE Huffman code outside the tree.");
    var symbol = this._codes[code];
    bs.SkipBits(this.Widths[symbol]);
    return symbol;
  }
}

/// <summary>Reading, reconstructing and (for the encoder) writing ACE Huffman trees.</summary>
internal static class AceHuffman {
  private const int WidthWidthBits = 3;
  private const int MaxWidthWidth = (1 << WidthWidthBits) - 1;

  /// <summary>
  /// The descending in-place quicksort the reference implementation uses. Tree reconstruction depends
  /// on its exact (unstable) ordering among equal widths, so it is reproduced step for step.
  /// </summary>
  internal static void Quicksort(int[] keys, int[] values) {
    if (keys.Length > 0)
      Subrange(keys, values, 0, keys.Length - 1);

    static void Swap(int[] a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

    static void Subrange(int[] keys, int[] values, int left, int right) {
      var newLeft = left;
      var newRight = right;
      var m = keys[right];
      while (true) {
        while (keys[newLeft] > m)
          ++newLeft;
        while (keys[newRight] < m)
          --newRight;
        if (newLeft <= newRight) {
          Swap(keys, newLeft, newRight);
          Swap(values, newLeft, newRight);
          ++newLeft;
          --newRight;
        }
        if (newLeft >= newRight)
          break;
      }
      if (left < newRight) {
        if (left < newRight - 1)
          Subrange(keys, values, left, newRight);
        else if (keys[left] < keys[newRight]) {
          Swap(keys, left, newRight);
          Swap(values, left, newRight);
        }
      }
      if (right > newLeft) {
        if (newLeft < right - 1)
          Subrange(keys, values, newLeft, right);
        else if (keys[newLeft] < keys[right]) {
          Swap(keys, newLeft, right);
          Swap(values, newLeft, right);
        }
      }
    }
  }

  /// <summary>
  /// Orders the used symbols as the decoder does: descending width under <see cref="Quicksort"/>, then
  /// reversed, so the shortest codes come first. A single used symbol is promoted to width 1.
  /// </summary>
  private static (int[] Symbols, int[] Widths) Order(int[] widths) {
    var symbols = new int[widths.Length];
    for (var i = 0; i < symbols.Length; ++i)
      symbols[i] = i;
    var sorted = (int[])widths.Clone();
    Quicksort(sorted, symbols);
    var used = 0;
    while (used < sorted.Length && sorted[used] != 0)
      ++used;
    if (used < 2) {
      // The decoder skips 1 bit for the lone symbol but fills the table from its original width.
      widths[symbols[0]] = 1;
      if (used == 0)
        ++used;
    }
    var orderedSymbols = symbols[..used];
    var orderedWidths = sorted[..used];
    Array.Reverse(orderedSymbols);
    Array.Reverse(orderedWidths);
    return (orderedSymbols, orderedWidths);
  }

  public static AceHuffmanTree MakeTree(int[] widths, int maxWidth) {
    var (symbols, sortedWidths) = Order(widths);
    var maxCodes = 1 << maxWidth;
    var codes = new List<int>(maxCodes);
    for (var i = 0; i < symbols.Length; ++i) {
      var w = sortedWidths[i];
      if (w > maxWidth)
        throw new InvalidDataException("ACE Huffman width exceeds the maximum.");
      var repeat = 1 << (maxWidth - w);
      for (var r = 0; r < repeat; ++r)
        codes.Add(symbols[i]);
      if (codes.Count > maxCodes)
        throw new InvalidDataException("ACE Huffman tree overfull.");
    }
    return new AceHuffmanTree([.. codes], widths, maxWidth);
  }

  public static AceHuffmanTree ReadTree(AceBitStream bs, int maxWidth, int numCodes) {
    var numWidths = (int)bs.ReadBits(9) + 1;
    if (numWidths > numCodes + 1)
      numWidths = numCodes + 1;
    var lowerWidth = (int)bs.ReadBits(4);
    var upperWidth = (int)bs.ReadBits(4);

    var widthWidths = new int[upperWidth + 1];
    for (var i = 0; i < widthWidths.Length; ++i)
      widthWidths[i] = (int)bs.ReadBits(WidthWidthBits);
    var widthTree = MakeTree(widthWidths, MaxWidthWidth);

    var widths = new List<int>(numWidths);
    while (widths.Count < numWidths) {
      var symbol = widthTree.ReadSymbol(bs);
      if (symbol < upperWidth)
        widths.Add(symbol);
      else {
        var length = Math.Min((int)bs.ReadBits(4) + 4, numWidths - widths.Count);
        for (var i = 0; i < length; ++i)
          widths.Add(0);
      }
    }
    if (upperWidth > 0)
      for (var i = 1; i < widths.Count; ++i)
        widths[i] = (widths[i] + widths[i - 1]) % upperWidth;
    for (var i = 0; i < widths.Count; ++i)
      if (widths[i] > 0)
        widths[i] += lowerWidth;
    return MakeTree([.. widths], maxWidth);
  }

  // ── encoder side ──────────────────────────────────────────────────────────

  /// <summary>
  /// The (code, width) pair the decoder will reconstruct for every symbol from <paramref name="widths"/>.
  /// </summary>
  public static (uint Code, int Width)[] Codes(int[] widths, int maxWidth, bool transmittedTrimmed = true) {
    // The decoder sorts exactly the widths that were transmitted (trailing zeros trimmed); the
    // quicksort's tie order depends on that array, so the codes are derived from the same one.
    var copy = transmittedTrimmed ? widths[..TransmittedLength(widths)] : (int[])widths.Clone();
    var (symbols, sortedWidths) = Order(copy);
    var result = new (uint, int)[widths.Length];
    var position = 0;
    for (var i = 0; i < symbols.Length; ++i) {
      var w = sortedWidths[i];
      result[symbols[i]] = ((uint)(position >> (maxWidth - w)), w);
      position += 1 << (maxWidth - w);
    }
    return result;
  }

  /// <summary>
  /// Length-limited Huffman widths for <paramref name="frequencies"/> (zero frequency = unused). At least
  /// two symbols are always given a width, so the reconstructed tree never degenerates.
  /// </summary>
  public static int[] BuildWidths(long[] frequencies, int maxWidth) {
    var n = frequencies.Length;
    var widths = new int[n];
    var used = new List<int>();
    for (var i = 0; i < n; ++i)
      if (frequencies[i] > 0)
        used.Add(i);
    for (var i = 0; used.Count < 2 && i < n; ++i)
      if (!used.Contains(i))
        used.Add(i);
    if (used.Count == 2) {
      widths[used[0]] = widths[used[1]] = 1;
      return widths;
    }

    // Plain Huffman via a priority queue of (weight, node).
    var parent = new int[2 * used.Count];
    var pq = new PriorityQueue<int, long>();
    var weight = new long[2 * used.Count];
    for (var i = 0; i < used.Count; ++i) {
      weight[i] = Math.Max(1, frequencies[used[i]]);
      pq.Enqueue(i, weight[i]);
    }
    var next = used.Count;
    while (pq.Count > 1) {
      var a = pq.Dequeue();
      var b = pq.Dequeue();
      weight[next] = weight[a] + weight[b];
      parent[a] = parent[b] = next;
      pq.Enqueue(next, weight[next]);
      ++next;
    }
    var root = next - 1;
    var depth = new int[used.Count];
    for (var i = 0; i < used.Count; ++i) {
      var d = 0;
      for (var node = i; node != root; node = parent[node])
        ++d;
      depth[i] = d;
    }

    // Enforce the limit, then repair the Kraft inequality by lengthening the deepest legal codes.
    var kraft = 0L;
    for (var i = 0; i < used.Count; ++i) {
      depth[i] = Math.Min(depth[i], maxWidth);
      kraft += 1L << (maxWidth - depth[i]);
    }
    var capacity = 1L << maxWidth;
    while (kraft > capacity) {
      var best = -1;
      for (var i = 0; i < used.Count; ++i)
        if (depth[i] < maxWidth && (best < 0 || depth[i] > depth[best]))
          best = i;
      kraft -= 1L << (maxWidth - depth[best] - 1);
      ++depth[best];
    }
    for (var i = 0; i < used.Count; ++i)
      widths[used[i]] = depth[i];
    return widths;
  }

  /// <summary>Writes <paramref name="widths"/> in the form <see cref="ReadTree"/> reads.</summary>
  private static int TransmittedLength(int[] widths) {
    var n = widths.Length;
    while (n > 1 && widths[n - 1] == 0)
      --n;
    return n;
  }

  public static void WriteTree(AceBitWriter w, int[] widths) {
    var numWidths = TransmittedLength(widths);

    var min = int.MaxValue;
    var max = 0;
    for (var i = 0; i < numWidths; ++i)
      if (widths[i] > 0) {
        min = Math.Min(min, widths[i]);
        max = Math.Max(max, widths[i]);
      }
    var lower = min - 1;
    var reduced = new int[numWidths];
    for (var i = 0; i < numWidths; ++i)
      reduced[i] = widths[i] > 0 ? widths[i] - lower : 0;
    var upper = max - lower + 1; // reduced values 0..upper-1
    if (upper > 15)
      throw new InvalidOperationException("ACE Huffman width range too wide.");

    // Delta coding mod upper; runs of zero deltas (4..19) use the escape symbol 'upper'.
    var deltas = new int[numWidths];
    deltas[0] = reduced[0];
    for (var i = 1; i < numWidths; ++i)
      deltas[i] = ((reduced[i] - reduced[i - 1]) % upper + upper) % upper;

    var tokens = new List<(int Symbol, int Run)>();
    for (var i = 0; i < numWidths;) {
      if (deltas[i] == 0) {
        var run = 1;
        while (i + run < numWidths && deltas[i + run] == 0 && run < 19)
          ++run;
        if (run >= 4) {
          tokens.Add((upper, run));
          i += run;
          continue;
        }
      }
      tokens.Add((deltas[i], 0));
      ++i;
    }

    var freq = new long[upper + 1];
    foreach (var (s, _) in tokens)
      ++freq[s];
    var widthWidths = BuildWidths(freq, MaxWidthWidth);
    var widthCodes = Codes(widthWidths, MaxWidthWidth, transmittedTrimmed: false);

    w.WriteBits((uint)(numWidths - 1), 9);
    w.WriteBits((uint)lower, 4);
    w.WriteBits((uint)upper, 4);
    for (var i = 0; i <= upper; ++i)
      w.WriteBits((uint)widthWidths[i], WidthWidthBits);
    foreach (var (s, run) in tokens) {
      var (code, width) = widthCodes[s];
      w.WriteBits(code, width);
      if (s == upper)
        w.WriteBits((uint)(run - 4), 4);
    }
  }
}
