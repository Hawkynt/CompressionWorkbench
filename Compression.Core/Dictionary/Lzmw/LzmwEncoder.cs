using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzmw;

/// <summary>
/// Encodes data using the LZMW (Lempel-Ziv-Miller-Wegman) algorithm with variable-width codes.
/// </summary>
/// <remarks>
/// Implemented from the published description of Miller &amp; Wegman's 1985 variant of LZW
/// (V.S. Miller, M.N. Wegman, "Variations on a theme by Ziv and Lempel", 1984/1985), as
/// summarized by Bell/Cleary/Witten, "Text Compression" (1990) and the Wikibooks "Data
/// Compression/Dictionary compression" article: instead of adding "the match just coded" plus
/// "one raw byte" to the dictionary (LZW's rule), LZMW adds the concatenation of the match just
/// coded (the previous match) and the ENTIRE match coded next (the current match). Dictionary
/// entries therefore grow by whole matches at a time rather than one byte at a time, so the
/// dictionary fills — and needs resetting — far sooner than LZW's for the same input.
/// No third-party source code was consulted or ported.
/// </remarks>
public sealed class LzmwEncoder {
  private readonly Stream _output;
  private readonly int _minBits;
  private readonly int _maxBits;
  private readonly BitOrder _bitOrder;

  /// <summary>
  /// Initializes a new <see cref="LzmwEncoder"/>.
  /// </summary>
  /// <param name="output">The stream to write compressed data to.</param>
  /// <param name="minBits">Minimum (initial) code width in bits. Defaults to 9.</param>
  /// <param name="maxBits">Maximum code width in bits. Defaults to 16.</param>
  /// <param name="bitOrder">The bit ordering to use for output.</param>
  public LzmwEncoder(Stream output, int minBits = 9, int maxBits = 16, BitOrder bitOrder = BitOrder.LsbFirst) {
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
  /// Encodes the input data and writes compressed LZMW codes to the output stream.
  /// </summary>
  /// <remarks>
  /// Code-width growth is deliberately applied two writes after the insertion that triggered
  /// it, not the very next one. The encoder discovers a new dictionary entry (previous match +
  /// next match) as soon as it has found the next match — before that match's code has even
  /// been written. The decoder cannot mirror that: it can only perform the matching insertion
  /// once it has decoded the NEXT code (it needs that code's bytes to build the concatenation),
  /// so its width tracking is always one insertion behind whatever the encoder just did. Growing
  /// the encoder's width immediately (for the very next write) desyncs the two the first time an
  /// input is large enough to cross a code-width boundary; delaying it by one extra write keeps
  /// both sides working from the same insertion history.
  /// </remarks>
  /// <param name="data">The data to compress.</param>
  public void Encode(ReadOnlySpan<byte> data) {
    var writer = new BitWriter(this._output, this._bitOrder);
    var clearCode = this.ClearCode;
    var stopCode = this.StopCode;
    var firstUsable = this.FirstUsableCode;
    var maxCode = 1 << this._maxBits;

    // Two-deep width pipeline: `activeBits` is used for the write happening
    // right now; `queuedBits` is already committed for the NEXT write. A
    // width growth computed by inserting entry N only becomes `activeBits`
    // two writes later — see the remarks on why this delay is required.
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

      // Add the concatenation of the previous match (curNode) and the entire
      // next match (the nextLen bytes just found) as one new dictionary entry.
      var assigned = InsertSuffix(curNode, data.Slice(pos, nextLen), ref nextCode, maxCode);

      // The width queued two writes ago is promoted unconditionally — that
      // promotion reflects an EARLIER, already-completed insertion and is due
      // regardless of whether THIS iteration's own insertion succeeds.
      activeBits = queuedBits;

      if (assigned < 0) {
        // Dictionary is full: reset and re-derive the current match against the
        // fresh (singles-only) dictionary so the next emitted code always fits
        // in minBits, exactly like LZW's clear-code reset. The clear code
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

      // See remarks: this insertion's resulting width applies starting two
      // writes from now, not the very next one — queue it instead of
      // assigning directly.
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
  /// a prefix of <c>data[pos..]</c>. The walk continues through structural (uncoded) nodes —
  /// created as intermediate steps of earlier single-entry insertions — to find a possibly
  /// deeper coded descendant, tracking the deepest node that actually carries a code.
  /// </summary>
  private static (TrieNode Node, int Code, int Length) FindLongestMatch(TrieNode root, ReadOnlySpan<byte> data, int pos) {
    var node = root;
    TrieNode? bestNode = null;
    var bestCode = -1;
    var bestLen = 0;
    var len = 0;
    var p = pos;

    while (p < data.Length && node.Children != null && node.Children.TryGetValue(data[p], out var next)) {
      node = next;
      ++len;
      ++p;
      if (node.Code < 0)
        continue;
      bestNode = node;
      bestCode = node.Code;
      bestLen = len;
    }

    return (bestNode!, bestCode, bestLen);
  }

  /// <summary>
  /// Inserts one new dictionary entry: the string reached by walking from
  /// <paramref name="startNode"/> (an already-matched entry) through every byte of
  /// <paramref name="suffix"/>. Intermediate nodes created along the way are left uncoded;
  /// only the final node receives the newly assigned code.
  /// </summary>
  /// <returns>The assigned code, or -1 if the dictionary is already full.</returns>
  private static int InsertSuffix(TrieNode startNode, ReadOnlySpan<byte> suffix, ref int nextCode, int maxCode) {
    if (nextCode >= maxCode)
      return -1;

    var node = startNode;
    foreach (var b in suffix) {
      node.Children ??= new Dictionary<byte, TrieNode>();
      if (!node.Children.TryGetValue(b, out var next)) {
        next = new TrieNode();
        node.Children[b] = next;
      }
      node = next;
    }

    var code = nextCode++;
    node.Code = code;
    return code;
  }

  private sealed class TrieNode {
    public Dictionary<byte, TrieNode>? Children;
    public int Code = -1;
  }
}
