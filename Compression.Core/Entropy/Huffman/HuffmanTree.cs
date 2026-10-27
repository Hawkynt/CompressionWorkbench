namespace Compression.Core.Entropy.Huffman;

/// <summary>
/// Provides static methods for building Huffman trees and extracting code lengths.
/// </summary>
public static class HuffmanTree {
  /// <summary>
  /// Builds a Huffman tree from symbol frequencies.
  /// </summary>
  /// <remarks>
  /// The tree is the one <see cref="DeterministicHuffman"/> describes, node for node: merge the
  /// two lightest nodes, and where several are equally light take them in rank order - a leaf
  /// for symbol <c>s</c> ranks <c>s</c>, the <c>k</c>-th internal node created ranks
  /// <c>frequencies.Length + k</c>, so leaves come before internal nodes, leaves by ascending
  /// symbol and internal nodes oldest first. Ranks cannot collide, so the rule is a total order
  /// and the tree is a function of the frequencies alone. The construction is not repeated
  /// here: this method asks <see cref="DeterministicHuffman.BuildForest"/> for the tree and only
  /// wraps the result in <see cref="HuffmanNode"/> objects, so the tree a caller walks and the
  /// code lengths <see cref="DeterministicHuffman.BuildCodeLengths(ReadOnlySpan{long})"/> hands
  /// out can never drift apart.
  /// </remarks>
  /// <param name="frequencies">An array where index = symbol and value = frequency.
  /// Symbols with zero frequency are excluded.</param>
  /// <returns>The root of the Huffman tree.</returns>
  /// <exception cref="ArgumentException">No symbols with non-zero frequency.</exception>
  public static HuffmanNode BuildFromFrequencies(long[] frequencies) {
    var leafCount = DeterministicHuffman.CountLeaves(frequencies);
    if (leafCount == 0)
      throw new ArgumentException("At least one symbol must have a non-zero frequency.", nameof(frequencies));

    // Single-symbol tree: pair the only leaf with a dummy sibling so the symbol still gets a
    // one-bit code instead of a zero-length one.
    if (leafCount == 1) {
      var single = 0;
      while (frequencies[single] <= 0)
        ++single;

      return new(new HuffmanNode(single, frequencies[single]), new(-2, 0));
    }

    var (weight, symbol, left, right) = DeterministicHuffman.BuildForest(frequencies, leafCount);

    // Children always sit at a lower index than their parent, so one forward pass materialises
    // every node after both of its children exist. The last slot is the root.
    var nodes = new HuffmanNode[weight.Length];
    for (var i = 0; i < leafCount; ++i)
      nodes[i] = new(symbol[i], weight[i]);

    for (var i = leafCount; i < nodes.Length; ++i)
      nodes[i] = new(nodes[left[i]], nodes[right[i]]);

    return nodes[^1];
  }

  /// <summary>
  /// Extracts code lengths from a Huffman tree for each symbol.
  /// </summary>
  /// <param name="root">The root of the Huffman tree.</param>
  /// <param name="maxSymbol">The maximum symbol value (exclusive). Determines the length of the output array.</param>
  /// <returns>An array where index = symbol, value = code length in bits.</returns>
  public static int[] GetCodeLengths(HuffmanNode root, int maxSymbol) {
    var lengths = new int[maxSymbol];
    AssignLengths(root, 0, lengths);
    return lengths;
  }

  /// <summary>
  /// Limits code lengths to a maximum value using the package-merge algorithm variant.
  /// Redistributes code lengths to satisfy the Kraft inequality while staying within the limit.
  /// </summary>
  /// <param name="codeLengths">The code lengths to limit (modified in place).</param>
  /// <param name="maxLength">The maximum allowed code length.</param>
  public static void LimitCodeLengths(int[] codeLengths, int maxLength) {
    var needsAdjustment = codeLengths.Any(t => t > maxLength);
    if (!needsAdjustment)
      return;

    // Collect non-zero lengths
    var symbols = new List<(int Symbol, int Length)>();
    for (var i = 0; i < codeLengths.Length; ++i)
      if (codeLengths[i] > 0)
        symbols.Add((i, codeLengths[i]));

    // Clamp lengths to maxLength — this shortens over-long codes, increasing
    // their Kraft contribution and potentially pushing the sum over budget.
    for (var i = 0; i < symbols.Count; ++i)
      if (symbols[i].Length > maxLength)
        symbols[i] = (symbols[i].Symbol, maxLength);

    // Fix Kraft overflow: lengthening a short code (L → L+1) halves its Kraft
    // contribution, reducing the sum. Pick the shortest code each iteration
    // for maximum reduction.
    var kraftMax = 1L << maxLength;
    for (;;) {
      var kraftSum = 0L;
      for (var i = 0; i < symbols.Count; ++i)
        kraftSum += 1L << (maxLength - symbols[i].Length);

      if (kraftSum <= kraftMax)
        break;

      // Find the shortest code that can be lengthened
      var shortestIdx = -1;
      var shortestLen = int.MaxValue;
      for (var i = 0; i < symbols.Count; ++i)
        if (symbols[i].Length < maxLength && symbols[i].Length < shortestLen) {
          shortestLen = symbols[i].Length;
          shortestIdx = i;
        }

      if (shortestIdx < 0)
        break; // all at maxLength — can't improve

      symbols[shortestIdx] = (symbols[shortestIdx].Symbol, symbols[shortestIdx].Length + 1);
    }

    // Redistribute excess Kraft space: shorten the longest codes to use up
    // remaining budget and keep the tree balanced. Shortening from L to L-1
    // doubles the symbol's Kraft contribution (adds 2^(maxLength-L) to sum).
    for (;;) {
      var kraftSum = 0L;
      for (var i = 0; i < symbols.Count; ++i)
        kraftSum += 1L << (maxLength - symbols[i].Length);

      var excess = kraftMax - kraftSum;
      if (excess <= 0)
        break;

      // Find the longest code that can be shortened
      var longestIdx = -1;
      var longestLen = 0;
      for (var i = 0; i < symbols.Count; ++i)
        if (symbols[i].Length > longestLen) {
          longestLen = symbols[i].Length;
          longestIdx = i;
        }

      if (longestIdx < 0 || longestLen <= 1)
        break;

      // Shortening adds 2^(maxLength - longestLen) to the sum
      var added = 1L << (maxLength - longestLen);
      if (added <= excess)
        symbols[longestIdx] = (symbols[longestIdx].Symbol, longestLen - 1);
      else
        break;
    }

    // Write back
    codeLengths.AsSpan().Clear();
    for (var i = 0; i < symbols.Count; ++i)
      codeLengths[symbols[i].Symbol] = symbols[i].Length;
  }

  private static void AssignLengths(HuffmanNode node, int depth, int[] lengths) {
    if (node.IsLeaf) {
      if (node.Symbol >= 0 && node.Symbol < lengths.Length)
        lengths[node.Symbol] = depth;

      return;
    }

    if (node.Left is not null)
      AssignLengths(node.Left, depth + 1, lengths);
    if (node.Right is not null)
      AssignLengths(node.Right, depth + 1, lengths);
  }
}
