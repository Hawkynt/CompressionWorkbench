using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.SuffixTree;

/// <summary>
/// Exposes suffix-tree-indexed dictionary compression as a benchmarkable building block.
/// At every position the longest previously-started factor is emitted as a
/// (length, offset) token and unmatched bytes are batched into literal-run tokens
/// (a 1-byte count followed by the raw bytes), so that non-repetitive stretches
/// only cost a small, amortized header instead of two bytes per literal.
/// The dictionary is the set of positions the factorization has already visited.
/// Written out as a suffix trie, each visited position <c>j</c> inserts the whole
/// path <c>data[j .. j+min(255, n-j))</c> and stamps every node on it with
/// <c>j</c>, which makes the query at position <c>i</c> exactly
/// <c>length = min(255, n-i, max over visited j &lt; i of LCP(j, i))</c> paired with
/// the largest visited <c>j</c> reaching that length. Both are read off a suffix
/// array instead: a longest common prefix is the minimum of the LCP array between
/// two ranks, the maximum over a set of positions is attained at the nearest
/// visited rank to either side, and the positions sharing at least that many
/// characters form one contiguous rank interval whose most recent member answers
/// the offset. That is the same factorization a trie yields, in O(n log n) time
/// and O(n) memory rather than one heap object per distinct substring.
/// Reference: P. Weiner, "Linear Pattern Matching Algorithms", 1973 (suffix trees);
/// E. Ukkonen, "On-line construction of suffix trees", Algorithmica 14, 1995;
/// T. Kasai &amp; al., "Linear-Time Longest-Common-Prefix Computation in Suffix
/// Arrays and Its Applications", CPM 2001;
/// M. Crochemore &amp; al., "Algorithms on Strings", Cambridge University Press, 2007
/// (suffix-array-driven LZ factorization). See also
/// https://en.wikipedia.org/wiki/Suffix_tree
/// </summary>
public sealed class SuffixTreeBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_SuffixTree";
  /// <inheritdoc/>
  public string DisplayName => "Suffix Tree Compression";
  /// <inheritdoc/>
  public string Description => "LZ factorization driven by a suffix-array dictionary index";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MinMatchLength = 3;
  private const int MaxMatchLength = 255; // Fits a single control byte.

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var count = data.Length;
    var bytes = data.ToArray();

    var suffixArray = BuildSuffixArray(bytes, count);
    var rankOf = new int[count];
    for (var k = 0; k < count; k++)
      rankOf[suffixArray[k]] = k;

    var lcpTree = new MinimumTree(BuildLcpArray(bytes, count, suffixArray, rankOf));
    var visitedTree = new MaximumTree(count);

    Span<byte> offsetBuf = stackalloc byte[4];
    var literalRun = new List<byte>(255);

    void FlushLiteralRun() {
      if (literalRun.Count == 0)
        return;
      ms.WriteByte(0);
      ms.WriteByte((byte)literalRun.Count);
      foreach (var b in literalRun)
        ms.WriteByte(b);
      literalRun.Clear();
    }

    var i = 0;
    while (i < count) {
      var rank = rankOf[i];
      var cap = Math.Min(MaxMatchLength, count - i);

      // The best match is shared with the closest already-visited rank on either
      // side; anything further away in rank order shares strictly less.
      var matchLength = 0;
      var before = visitedTree.LastPresent(rank - 1);
      if (before >= 0) {
        var shared = lcpTree.Minimum(before + 1, rank);
        if (shared > matchLength)
          matchLength = shared;
      }

      var after = visitedTree.FirstPresent(rank + 1, count - 1);
      if (after >= 0) {
        var shared = lcpTree.Minimum(rank + 1, after);
        if (shared > matchLength)
          matchLength = shared;
      }

      if (matchLength > cap)
        matchLength = cap;

      if (matchLength >= MinMatchLength) {
        // Every position sharing at least matchLength characters sits in one
        // contiguous rank interval; its most recently visited member is the
        // position the trie would have remembered at that depth.
        var lowRank = lcpTree.LastBelow(rank, matchLength);
        var highRank = lcpTree.FirstBelow(rank + 1, matchLength) - 1;
        var matchPosition = visitedTree.Maximum(lowRank, highRank);

        FlushLiteralRun();
        ms.WriteByte((byte)matchLength);
        BinaryPrimitives.WriteInt32LittleEndian(offsetBuf, i - matchPosition);
        ms.Write(offsetBuf);
        visitedTree.Assign(rank, i);
        i += matchLength;
      } else {
        literalRun.Add(data[i]);
        visitedTree.Assign(rank, i);
        i++;
        if (literalRun.Count == 255)
          FlushLiteralRun();
      }
    }

    FlushLiteralRun();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalLength == 0)
      return [];

    var result = new byte[originalLength];
    var outPos = 0;
    var pos = 4;

    while (outPos < originalLength) {
      var control = data[pos++];

      if (control == 0) {
        var count = data[pos++];
        for (var k = 0; k < count; k++)
          result[outPos++] = data[pos++];
        continue;
      }

      var length = control;
      var offset = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
      pos += 4;

      var srcPos = outPos - offset;
      for (var k = 0; k < length; k++)
        result[outPos + k] = result[srcPos + k];
      outPos += length;
    }

    return result;
  }

  /// <summary>
  /// Builds the suffix array of the input by prefix doubling with a counting sort
  /// on each rank pair, which needs a handful of int arrays and no per-substring
  /// allocation at all. A sentinel below every byte value is appended so the
  /// cyclic sort coincides with the suffix order; its own entry is dropped.
  /// </summary>
  /// <param name="bytes">Input bytes.</param>
  /// <param name="count">Number of input bytes.</param>
  /// <returns>Start positions of the suffixes in lexical order.</returns>
  private static int[] BuildSuffixArray(byte[] bytes, int count) {
    var size = count + 1;
    var symbols = new int[size];
    for (var k = 0; k < count; k++)
      symbols[k] = bytes[k] + 1;

    var order = new int[size];
    var rank = new int[size];
    var nextRank = new int[size];
    var shifted = new int[size];
    const int Alphabet = 257;
    var counts = new int[Math.Max(Alphabet, size) + 1];

    for (var k = 0; k < size; k++)
      counts[symbols[k]]++;
    for (var c = 1; c < Alphabet; c++)
      counts[c] += counts[c - 1];
    for (var k = size - 1; k >= 0; k--)
      order[--counts[symbols[k]]] = k;

    var classes = 1;
    rank[order[0]] = 0;
    for (var k = 1; k < size; k++) {
      if (symbols[order[k]] != symbols[order[k - 1]])
        classes++;
      rank[order[k]] = classes - 1;
    }

    for (var step = 1; classes < size; step *= 2) {
      for (var k = 0; k < size; k++) {
        var start = order[k] - step;
        if (start < 0)
          start += size;
        shifted[k] = start;
      }

      Array.Clear(counts, 0, classes);
      for (var k = 0; k < size; k++)
        counts[rank[k]]++;
      for (var c = 1; c < classes; c++)
        counts[c] += counts[c - 1];
      for (var k = size - 1; k >= 0; k--)
        order[--counts[rank[shifted[k]]]] = shifted[k];

      var grown = 1;
      nextRank[order[0]] = 0;
      for (var k = 1; k < size; k++) {
        var currentHead = rank[order[k]];
        var currentTail = rank[(order[k] + step) % size];
        var previousHead = rank[order[k - 1]];
        var previousTail = rank[(order[k - 1] + step) % size];
        if (currentHead != previousHead || currentTail != previousTail)
          grown++;
        nextRank[order[k]] = grown - 1;
      }

      Array.Copy(nextRank, rank, size);
      classes = grown;
    }

    var suffixArray = new int[count];
    for (var k = 1; k < size; k++)
      suffixArray[k - 1] = order[k];
    return suffixArray;
  }

  /// <summary>
  /// Builds the LCP array with Kasai's linear-time scan. Entry <c>k</c> holds the
  /// longest common prefix of the suffixes at ranks <c>k-1</c> and <c>k</c>;
  /// entries 0 and <c>n</c> are sentinels below any achievable match length, so
  /// the interval searches terminate without a bounds test.
  /// </summary>
  /// <param name="bytes">Input bytes.</param>
  /// <param name="count">Number of input bytes.</param>
  /// <param name="suffixArray">Suffix start positions in lexical order.</param>
  /// <param name="rankOf">Inverse of the suffix array.</param>
  /// <returns>LCP values indexed by rank, of length <c>n+1</c>.</returns>
  private static int[] BuildLcpArray(byte[] bytes, int count, int[] suffixArray, int[] rankOf) {
    var lcp = new int[count + 1];
    lcp[0] = -1;
    lcp[count] = -1;

    var shared = 0;
    for (var position = 0; position < count; position++) {
      var rank = rankOf[position];
      if (rank == 0) {
        shared = 0;
        continue;
      }

      var previous = suffixArray[rank - 1];
      while (position + shared < count && previous + shared < count && bytes[position + shared] == bytes[previous + shared])
        ++shared;

      lcp[rank] = shared;
      if (shared > 0)
        --shared;
    }

    return lcp;
  }

  /// <summary>
  /// A perfect binary segment tree over immutable leaves aggregated by minimum,
  /// able to locate the nearest leaf below a threshold on either side of a point.
  /// </summary>
  private sealed class MinimumTree {
    private const int Infinite = int.MaxValue;

    private readonly int[] _nodes;
    private readonly int _size;
    private readonly int _leaves;
    private readonly int[] _leftCover = new int[64];
    private readonly int[] _rightCover = new int[64];
    private int _leftCount;
    private int _rightCount;

    /// <summary>Builds the tree over the given values.</summary>
    /// <param name="values">Leaf values, indexed from zero.</param>
    public MinimumTree(int[] values) {
      this._leaves = values.Length;
      var size = 1;
      while (size < values.Length)
        size *= 2;
      this._size = size;

      this._nodes = new int[size * 2];
      Array.Fill(this._nodes, Infinite);
      for (var k = 0; k < values.Length; k++)
        this._nodes[size + k] = values[k];
      for (var k = size - 1; k >= 1; k--)
        this._nodes[k] = Math.Min(this._nodes[k * 2], this._nodes[k * 2 + 1]);
    }

    /// <summary>Smallest leaf value in the inclusive index range.</summary>
    /// <param name="low">First index.</param>
    /// <param name="high">Last index.</param>
    /// <returns>The minimum, or <see cref="int.MaxValue"/> for an empty range.</returns>
    public int Minimum(int low, int high) {
      if (low > high)
        return Infinite;

      this.Cover(low, high);
      var best = Infinite;
      for (var t = 0; t < this._leftCount; t++)
        best = Math.Min(best, this._nodes[this._leftCover[t]]);
      for (var t = 0; t < this._rightCount; t++)
        best = Math.Min(best, this._nodes[this._rightCover[t]]);
      return best;
    }

    /// <summary>Rightmost index in <c>[0, high]</c> whose value is below the limit.</summary>
    /// <param name="high">Last index to consider.</param>
    /// <param name="limit">Exclusive upper bound on the value sought.</param>
    /// <returns>The index, or 0 when the range holds no such value.</returns>
    public int LastBelow(int high, int limit) {
      this.Cover(0, high);
      for (var t = 0; t < this._rightCount; t++) {
        var found = this.DescendRight(this._rightCover[t], limit);
        if (found >= 0)
          return found;
      }

      for (var t = this._leftCount - 1; t >= 0; t--) {
        var found = this.DescendRight(this._leftCover[t], limit);
        if (found >= 0)
          return found;
      }

      return 0;
    }

    /// <summary>Leftmost index in <c>[low, n]</c> whose value is below the limit.</summary>
    /// <param name="low">First index to consider.</param>
    /// <param name="limit">Exclusive upper bound on the value sought.</param>
    /// <returns>The index, or the last leaf when the range holds no such value.</returns>
    public int FirstBelow(int low, int limit) {
      this.Cover(low, this._leaves - 1);
      for (var t = 0; t < this._leftCount; t++) {
        var found = this.DescendLeft(this._leftCover[t], limit);
        if (found >= 0)
          return found;
      }

      for (var t = this._rightCount - 1; t >= 0; t--) {
        var found = this.DescendLeft(this._rightCover[t], limit);
        if (found >= 0)
          return found;
      }

      return this._leaves - 1;
    }

    private int DescendRight(int node, int limit) {
      if (this._nodes[node] >= limit)
        return -1;
      while (node < this._size)
        node = this._nodes[node * 2 + 1] < limit ? node * 2 + 1 : node * 2;
      return node - this._size;
    }

    private int DescendLeft(int node, int limit) {
      if (this._nodes[node] >= limit)
        return -1;
      while (node < this._size)
        node = this._nodes[node * 2] < limit ? node * 2 : node * 2 + 1;
      return node - this._size;
    }

    private void Cover(int low, int high) {
      var left = this._size + low;
      var right = this._size + high + 1;
      this._leftCount = 0;
      this._rightCount = 0;
      while (left < right) {
        if (left % 2 == 1)
          this._leftCover[this._leftCount++] = left++;
        if (right % 2 == 1)
          this._rightCover[this._rightCount++] = --right;
        left /= 2;
        right /= 2;
      }
    }
  }

  /// <summary>
  /// A perfect binary segment tree over mutable leaves aggregated by maximum,
  /// where a leaf below zero counts as absent. Answers "most recent value in a
  /// range" and "nearest present leaf to either side of a point".
  /// </summary>
  private sealed class MaximumTree {
    private readonly int[] _nodes;
    private readonly int _size;
    private readonly int[] _leftCover = new int[64];
    private readonly int[] _rightCover = new int[64];
    private int _leftCount;
    private int _rightCount;

    /// <summary>Builds an all-absent tree with room for the given number of leaves.</summary>
    /// <param name="leaves">Number of usable leaves.</param>
    public MaximumTree(int leaves) {
      var size = 1;
      while (size < leaves)
        size *= 2;
      this._size = size;
      this._nodes = new int[size * 2];
      Array.Fill(this._nodes, -1);
    }

    /// <summary>Stores a value at a leaf, making it present.</summary>
    /// <param name="index">Leaf index.</param>
    /// <param name="value">Value to store; must not be negative.</param>
    public void Assign(int index, int value) {
      var node = this._size + index;
      this._nodes[node] = value;
      for (node /= 2; node >= 1; node /= 2)
        this._nodes[node] = Math.Max(this._nodes[node * 2], this._nodes[node * 2 + 1]);
    }

    /// <summary>Largest value in the inclusive index range.</summary>
    /// <param name="low">First index.</param>
    /// <param name="high">Last index.</param>
    /// <returns>The maximum, or -1 when the range holds nothing.</returns>
    public int Maximum(int low, int high) {
      if (low > high)
        return -1;

      this.Cover(low, high);
      var best = -1;
      for (var t = 0; t < this._leftCount; t++)
        best = Math.Max(best, this._nodes[this._leftCover[t]]);
      for (var t = 0; t < this._rightCount; t++)
        best = Math.Max(best, this._nodes[this._rightCover[t]]);
      return best;
    }

    /// <summary>Rightmost present leaf in <c>[0, high]</c>.</summary>
    /// <param name="high">Last index to consider.</param>
    /// <returns>The index, or -1 when the range holds nothing.</returns>
    public int LastPresent(int high) {
      if (high < 0)
        return -1;

      this.Cover(0, high);
      for (var t = 0; t < this._rightCount; t++) {
        var found = this.DescendRight(this._rightCover[t]);
        if (found >= 0)
          return found;
      }

      for (var t = this._leftCount - 1; t >= 0; t--) {
        var found = this.DescendRight(this._leftCover[t]);
        if (found >= 0)
          return found;
      }

      return -1;
    }

    /// <summary>Leftmost present leaf in the inclusive index range.</summary>
    /// <param name="low">First index to consider.</param>
    /// <param name="high">Last index to consider.</param>
    /// <returns>The index, or -1 when the range holds nothing.</returns>
    public int FirstPresent(int low, int high) {
      if (low > high)
        return -1;

      this.Cover(low, high);
      for (var t = 0; t < this._leftCount; t++) {
        var found = this.DescendLeft(this._leftCover[t]);
        if (found >= 0)
          return found;
      }

      for (var t = this._rightCount - 1; t >= 0; t--) {
        var found = this.DescendLeft(this._rightCover[t]);
        if (found >= 0)
          return found;
      }

      return -1;
    }

    private int DescendRight(int node) {
      if (this._nodes[node] < 0)
        return -1;
      while (node < this._size)
        node = this._nodes[node * 2 + 1] >= 0 ? node * 2 + 1 : node * 2;
      return node - this._size;
    }

    private int DescendLeft(int node) {
      if (this._nodes[node] < 0)
        return -1;
      while (node < this._size)
        node = this._nodes[node * 2] >= 0 ? node * 2 : node * 2 + 1;
      return node - this._size;
    }

    private void Cover(int low, int high) {
      var left = this._size + low;
      var right = this._size + high + 1;
      this._leftCount = 0;
      this._rightCount = 0;
      while (left < right) {
        if (left % 2 == 1)
          this._leftCover[this._leftCount++] = left++;
        if (right % 2 == 1)
          this._rightCover[this._rightCount++] = --right;
        left /= 2;
        right /= 2;
      }
    }
  }
}
