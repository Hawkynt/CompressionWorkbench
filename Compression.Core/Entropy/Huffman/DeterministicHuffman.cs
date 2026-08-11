namespace Compression.Core.Entropy.Huffman;

/// <summary>
/// Builds Huffman code lengths from symbol weights under an explicit total order on
/// tree nodes, so that the resulting lengths are a function of the weights alone.
/// </summary>
/// <remarks>
/// <para>
/// Textbook Huffman construction says "repeatedly merge the two lightest nodes" but says
/// nothing about which node to pick when several are equally light. Feeding the nodes to a
/// generic priority queue therefore leaves the tree shape - and with it the code lengths and
/// every compressed byte that depends on them - at the mercy of that queue's internal
/// ordering of equal keys, which no container documents. This builder removes that freedom
/// by making the tie-break part of the algorithm.
/// </para>
/// <para><b>The total order.</b> Every node carries a weight and a rank:</para>
/// <list type="bullet">
///   <item><description>a leaf for symbol <c>s</c> has the weight of <c>s</c> and rank <c>s</c>;</description></item>
///   <item><description>the <c>k</c>-th internal node created (<c>k</c> counting from zero) has the
///     summed weight of its two children and rank <c>symbolCount + k</c>.</description></item>
/// </list>
/// <para>
/// Node <c>a</c> precedes node <c>b</c> exactly when <c>a.Weight &lt; b.Weight</c>, or the weights
/// are equal and <c>a.Rank &lt; b.Rank</c>. Ranks are pairwise distinct - symbols are distinct and
/// all below <c>symbolCount</c>, creation indices are distinct and all at or above it - so no two
/// distinct nodes ever compare equal and the order is total. In plain terms: lighter first;
/// among equal weights, leaves before internal nodes, leaves by ascending symbol value,
/// internal nodes oldest first. Preferring leaves on a tie also keeps the tree shallow, since
/// a leaf can never be deeper than a node that already has children.
/// </para>
/// <para><b>The construction.</b> No heap is involved. Leaves are sorted once into the above
/// order; internal nodes are appended to a second queue as they are created, and that queue is
/// already sorted, because merge weights are non-decreasing and creation indices increase. The
/// globally smallest node is therefore always at the front of one of the two queues, and
/// building the tree is an ordinary two-queue merge.
/// </para>
/// <para>
/// The Cipher project implements the same rule in
/// <c>algorithms/compression/huffman-code-lengths.data.js</c>; the two agree because they follow
/// the same written rule, not because either mimics the other's runtime.
/// </para>
/// </remarks>
public static class DeterministicHuffman {

  /// <summary>
  /// Builds Huffman code lengths for the given symbol weights.
  /// </summary>
  /// <param name="weights">Weight per symbol, indexed by symbol value. Symbols whose weight is
  /// zero or negative are excluded from the tree and receive length zero.</param>
  /// <returns>An array of the same length as <paramref name="weights"/> holding the code length
  /// of each symbol, or zero for excluded symbols. A single participating symbol receives length
  /// one. Lengths are otherwise unbounded; callers that need a depth limit apply their own
  /// clamping and Kraft repair afterwards.</returns>
  public static int[] BuildCodeLengths(ReadOnlySpan<int> weights) {
    var symbolCount = weights.Length;
    var lengths = new int[symbolCount];

    var leafCount = 0;
    for (var i = 0; i < symbolCount; ++i)
      if (weights[i] > 0)
        ++leafCount;

    if (leafCount == 0)
      return lengths;

    if (leafCount == 1) {
      for (var i = 0; i < symbolCount; ++i)
        if (weights[i] > 0) {
          lengths[i] = 1;
          break;
        }

      return lengths;
    }

    // Node storage. Slots [0, leafCount) hold the leaves in ascending order, slots
    // [leafCount, nodeCount) the internal nodes in creation order. Every internal node
    // therefore sits at a higher index than both of its children.
    var nodeCount = 2 * leafCount - 1;
    var nodeWeight = new long[nodeCount];
    var nodeSymbol = new int[nodeCount];
    var nodeLeft = new int[nodeCount];
    var nodeRight = new int[nodeCount];

    var leaves = new (long Weight, int Symbol)[leafCount];
    var filled = 0;
    for (var i = 0; i < symbolCount; ++i)
      if (weights[i] > 0)
        leaves[filled++] = (weights[i], i);

    // Ascending by (weight, symbol), which is exactly the total order restricted to leaves
    // because a leaf's rank is its symbol value. The comparison never returns zero for two
    // different leaves, so how the sort itself treats equal keys cannot matter.
    Array.Sort(leaves, static (a, b) => a.Weight != b.Weight
      ? a.Weight.CompareTo(b.Weight)
      : a.Symbol.CompareTo(b.Symbol));

    for (var i = 0; i < leafCount; ++i) {
      nodeWeight[i] = leaves[i].Weight;
      nodeSymbol[i] = leaves[i].Symbol;
    }

    // Two-queue merge: leafHead walks the sorted leaves, internalHead the internal nodes in
    // creation order. Both queues are in ascending total order, so the smallest node still in
    // play is always one of the two fronts.
    var leafHead = 0;
    var internalHead = leafCount;
    var created = leafCount;

    int TakeSmallest() {
      // Equal weight favours the leaf: a leaf's rank is below symbolCount while an internal
      // node's rank is at or above it.
      var takeLeaf = leafHead < leafCount
                     && (internalHead >= created || nodeWeight[leafHead] <= nodeWeight[internalHead]);

      return takeLeaf ? leafHead++ : internalHead++;
    }

    while (created < nodeCount) {
      var left = TakeSmallest();
      var right = TakeSmallest();
      nodeWeight[created] = nodeWeight[left] + nodeWeight[right];
      nodeSymbol[created] = -1;
      nodeLeft[created] = left;
      nodeRight[created] = right;
      ++created;
    }

    // Depths, walked from the root backwards. Both children of a node always sit at a lower
    // index than the node itself, so one reverse pass suffices and no recursion is needed even
    // for a maximally skewed tree.
    var depth = new int[nodeCount];
    for (var i = nodeCount - 1; i >= leafCount; --i) {
      var childDepth = depth[i] + 1;
      depth[nodeLeft[i]] = childDepth;
      depth[nodeRight[i]] = childDepth;
    }

    for (var i = 0; i < leafCount; ++i)
      lengths[nodeSymbol[i]] = Math.Max(depth[i], 1);

    return lengths;
  }

}
