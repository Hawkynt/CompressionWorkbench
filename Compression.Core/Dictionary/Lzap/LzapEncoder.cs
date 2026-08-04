using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzap;

/// <summary>
/// Encodes data using the LZAP (Lempel-Ziv-All-Prefixes) algorithm with variable-width codes.
/// </summary>
/// <remarks>
/// Implemented from the published description of James Storer's 1988 LZMW derivative (as
/// summarized by the Wikibooks "Data Compression/Dictionary compression" article): instead of
/// adding just the concatenation of the previous match and the entire current match (LZMW's
/// rule), LZAP adds the previous match concatenated with EVERY prefix of the current match. For
/// example, if the previous match is "com" and the current match is "press", LZAP adds "comp",
/// "compr", "compre", "compres" and "compress" — five entries where LZW would add one ("comp")
/// and LZMW would add one ("compress"). This trades a much larger dictionary (and correspondingly
/// more frequent resets) for fewer emitted codes. No third-party source code was consulted or
/// ported.
/// </remarks>
public sealed class LzapEncoder {
  private readonly Stream _output;
  private readonly int _minBits;
  private readonly int _maxBits;
  private readonly BitOrder _bitOrder;

  /// <summary>
  /// Initializes a new <see cref="LzapEncoder"/>.
  /// </summary>
  /// <param name="output">The stream to write compressed data to.</param>
  /// <param name="minBits">Minimum (initial) code width in bits. Defaults to 9.</param>
  /// <param name="maxBits">
  /// Maximum code width in bits. Defaults to 12 — deliberately smaller than LZW/LZMW's
  /// customary 16, because "every prefix" makes LZAP's dictionary grow multiplicatively rather
  /// than linearly on highly repetitive input (a single step can add hundreds of entries at
  /// once); a 16-bit code space lets individual entries grow into the tens of thousands of
  /// bytes before a reset, which is needlessly expensive in time and memory for a benchmarking
  /// building block. Resetting sooner keeps entry lengths — and thus per-step cost — bounded.
  /// </param>
  /// <param name="bitOrder">The bit ordering to use for output.</param>
  public LzapEncoder(Stream output, int minBits = 9, int maxBits = 12, BitOrder bitOrder = BitOrder.LsbFirst) {
    this._output = output ?? throw new ArgumentNullException(nameof(output));
    this._minBits = minBits;
    this._maxBits = maxBits;
    this._bitOrder = bitOrder;
  }

  /// <summary>
  /// Gets the clear code value (2^(minBits-1)), emitted whenever the dictionary fills and is reset.
  /// </summary>
  public int ClearCode => 1 << (this._minBits - 1);

  /// <summary>
  /// Gets the stop code value (ClearCode + 1), emitted once at end of stream.
  /// </summary>
  public int StopCode => this.ClearCode + 1;

  /// <summary>
  /// Gets the first code available for dictionary entries beyond the 256 single-byte codes.
  /// </summary>
  public int FirstUsableCode => this.ClearCode + 2;

  /// <summary>
  /// Encodes the input data and writes compressed LZAP codes to the output stream.
  /// </summary>
  /// <remarks>
  /// Code-width growth is deliberately applied two writes after the batch of insertions that
  /// triggered it, not the very next one — see the identical remark on <c>LzmwEncoder.Encode</c>.
  /// The decoder can only replicate a step's prefix insertions once it has decoded the next code
  /// (their content depends on that code's bytes), so it is always one insertion-batch behind the
  /// encoder; delaying width growth by one extra write keeps both sides synchronized.
  /// </remarks>
  /// <param name="data">The data to compress.</param>
  public void Encode(ReadOnlySpan<byte> data) {
    var writer = new BitWriter(this._output, this._bitOrder);
    var clearCode = this.ClearCode;
    var stopCode = this.StopCode;
    var firstUsable = this.FirstUsableCode;
    var maxCode = 1 << this._maxBits;

    // Two-deep width pipeline — see LzmwEncoder.Encode's remarks for why the
    // delay is exactly two writes.
    var activeBits = this._minBits;
    var queuedBits = this._minBits;

    if (data.IsEmpty) {
      writer.WriteBits((uint)stopCode, activeBits);
      writer.FlushBits();
      return;
    }

    var root = BuildInitialTrie();
    var nextCode = firstUsable;

    var (curNode, curCode, curLen) = FindLongestMatch(root, data, 0);
    var pos = 0;

    while (true) {
      writer.WriteBits((uint)curCode, activeBits);
      pos += curLen;
      if (pos >= data.Length)
        break;

      var (nextNode, nextMatchCode, nextLen) = FindLongestMatch(root, data, pos);

      // Add previousMatch + every prefix of the current match, walking forward
      // from curNode one byte at a time so each of the (up to nextLen) new
      // entries costs O(1) amortized instead of re-walking from the root.
      var assigned = InsertAllPrefixes(curNode, data.Slice(pos, nextLen), ref nextCode, maxCode);

      // The width queued two writes ago is promoted unconditionally — that
      // promotion reflects an EARLIER, already-completed insertion and is due
      // regardless of whether THIS iteration's own insertion succeeds.
      activeBits = queuedBits;

      if (assigned < nextLen) {
        // Dictionary filled up partway through (or before) adding this step's
        // prefixes: reset and re-derive the current match against the fresh
        // dictionary, exactly like LZMW/LZW's clear-code reset. The clear code
        // itself is written at the just-promoted `activeBits` — never at a
        // width grown from this abandoned, overflowed insertion.
        writer.WriteBits((uint)clearCode, activeBits);
        root = BuildInitialTrie();
        nextCode = firstUsable;
        activeBits = this._minBits;
        queuedBits = this._minBits;
        (curNode, curCode, curLen) = FindLongestMatch(root, data, pos);
        continue;
      }

      queuedBits = ComputeWidth(nextCode, this._minBits, this._maxBits);

      curNode = nextNode;
      curCode = nextMatchCode;
      curLen = nextLen;
    }

    writer.WriteBits((uint)stopCode, activeBits);
    writer.FlushBits();
  }

  /// <summary>
  /// Computes the code width needed to represent codes up to (but not including) <paramref name="nextCode"/>,
  /// the same monotonic growth rule LZW/LZMW/LZAP all share.
  /// </summary>
  private static int ComputeWidth(int nextCode, int minBits, int maxBits) {
    var w = minBits;
    while (nextCode >= (1 << w) && w < maxBits)
      ++w;
    return w;
  }

  private static TrieNode BuildInitialTrie() {
    var root = new TrieNode { Children = new Dictionary<byte, TrieNode>(256) };
    for (var b = 0; b < 256; ++b)
      root.Children[(byte)b] = new TrieNode { Code = b };
    return root;
  }

  /// <summary>
  /// Walks from <paramref name="root"/> matching the longest existing dictionary entry that is
  /// a prefix of <c>data[pos..]</c>. Every node beyond the root carries a code by construction
  /// (LZAP codes every prefix it inserts), so the walk simply stops at the first missing child.
  /// </summary>
  private static (TrieNode Node, int Code, int Length) FindLongestMatch(TrieNode root, ReadOnlySpan<byte> data, int pos) {
    var node = root;
    var len = 0;
    var p = pos;

    while (p < data.Length && node.Children != null && node.Children.TryGetValue(data[p], out var next)) {
      node = next;
      ++len;
      ++p;
    }

    return (node, node.Code, len);
  }

  /// <summary>
  /// Inserts one new dictionary entry per prefix of <paramref name="suffix"/> (the current
  /// match's bytes), walking forward from <paramref name="startNode"/> (the previous match's
  /// already-matched node) one byte at a time — length 1, then 2, then 3, and so on — assigning
  /// a fresh code to every node visited.
  /// </summary>
  /// <returns>The number of prefixes actually assigned before the dictionary filled up.</returns>
  private static int InsertAllPrefixes(TrieNode startNode, ReadOnlySpan<byte> suffix, ref int nextCode, int maxCode) {
    var node = startNode;
    var assigned = 0;

    foreach (var b in suffix) {
      if (nextCode >= maxCode)
        break;

      node.Children ??= new Dictionary<byte, TrieNode>();
      if (!node.Children.TryGetValue(b, out var next)) {
        next = new TrieNode();
        node.Children[b] = next;
      }
      node = next;
      node.Code = nextCode++;
      ++assigned;
    }

    return assigned;
  }

  private sealed class TrieNode {
    public Dictionary<byte, TrieNode>? Children;
    public int Code = -1;
  }
}
