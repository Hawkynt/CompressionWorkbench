using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.SuffixTree;

/// <summary>
/// Exposes suffix-tree-indexed dictionary compression as a benchmarkable building block.
/// Builds an incremental suffix trie over the already-seen input — the uncompacted
/// sibling of a suffix tree, where every root-to-node path is a distinct substring
/// that has occurred before, and each node remembers the most recent starting
/// position of that substring. At every position the trie is descended to find the
/// longest previously-seen prefix in O(match length), which is exactly the classic
/// "longest previous factor" query a suffix tree/array answers for LZ factorization.
/// Matches are emitted as (length, offset) tokens; runs of unmatched bytes are
/// batched into literal-run tokens (a 1-byte count followed by the raw bytes) so
/// that non-repetitive stretches only cost a small, amortized header instead of
/// two bytes per literal.
/// Reference: P. Weiner, "Linear Pattern Matching Algorithms", 1973 (suffix trees);
/// E. Ukkonen, "On-line construction of suffix trees", Algorithmica 14, 1995;
/// M. Crochemore &amp; al., "Algorithms on Strings", Cambridge University Press, 2007
/// (suffix-tree-driven LZ factorization). See also
/// https://en.wikipedia.org/wiki/Suffix_tree
/// </summary>
public sealed class SuffixTreeBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_SuffixTree";
  /// <inheritdoc/>
  public string DisplayName => "Suffix Tree Compression";
  /// <inheritdoc/>
  public string Description => "LZ factorization driven by an incremental suffix trie dictionary";
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

    var root = new TrieNode();
    var i = 0;
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

    while (i < data.Length) {
      var (matchLength, matchPosition) = FindAndInsert(root, data, i);

      if (matchLength >= MinMatchLength) {
        FlushLiteralRun();
        ms.WriteByte((byte)matchLength);
        BinaryPrimitives.WriteInt32LittleEndian(offsetBuf, i - matchPosition);
        ms.Write(offsetBuf);
        i += matchLength;
      } else {
        literalRun.Add(data[i]);
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
  /// Descends the suffix trie from the root along <c>data[i..]</c>, returning the
  /// longest previously-recorded match found along the way (length + start
  /// position), then extends the trie with the remainder of this suffix (bounded
  /// by <see cref="MaxMatchLength"/>) so later lookups can match deeper than any
  /// single previous insertion reached. Every prefix node walked during the match
  /// phase has its "most recent occurrence" position refreshed to <paramref name="i"/>.
  /// </summary>
  private static (int Length, int Position) FindAndInsert(TrieNode root, ReadOnlySpan<byte> data, int i) {
    var node = root;
    var depth = 0;
    var bestLength = 0;
    var bestPosition = -1;
    var maxDepth = Math.Min(MaxMatchLength, data.Length - i);

    // Phase 1: follow existing trie structure, recording the deepest match found.
    while (depth < maxDepth) {
      var b = data[i + depth];
      node.Children ??= [];

      if (!node.Children.TryGetValue(b, out var child))
        break;

      if (child.Position >= 0) {
        bestLength = depth + 1;
        bestPosition = child.Position;
      }
      child.Position = i;
      node = child;
      depth++;
    }

    // Phase 2: extend the trie with brand-new nodes for the rest of this suffix,
    // so a future occurrence of this longer prefix can be matched in one lookup
    // instead of needing to be rebuilt one level at a time.
    while (depth < maxDepth) {
      var newNode = new TrieNode { Position = i };
      node.Children ??= [];
      node.Children[data[i + depth]] = newNode;
      node = newNode;
      depth++;
    }

    return (bestLength, bestPosition);
  }

  /// <summary>A single node of the incremental suffix trie.</summary>
  private sealed class TrieNode {
    /// <summary>Most recent starting position whose suffix passes through this node, or -1.</summary>
    public int Position = -1;

    /// <summary>Child nodes keyed by the next byte, lazily allocated.</summary>
    public Dictionary<byte, TrieNode>? Children;
  }
}
