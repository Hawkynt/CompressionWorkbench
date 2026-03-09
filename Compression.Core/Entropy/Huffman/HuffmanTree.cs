using Compression.Core.DataStructures;

namespace Compression.Core.Entropy.Huffman;

/// <summary>
/// Provides static methods for building Huffman trees and extracting code lengths.
/// </summary>
public static class HuffmanTree {
  /// <summary>
  /// Builds a Huffman tree from symbol frequencies.
  /// </summary>
  /// <param name="frequencies">An array where index = symbol and value = frequency.
  /// Symbols with zero frequency are excluded.</param>
  /// <returns>The root of the Huffman tree.</returns>
  /// <exception cref="ArgumentException">No symbols with non-zero frequency.</exception>
  public static HuffmanNode BuildFromFrequencies(long[] frequencies) {
    var heap = new MinHeap<HuffmanNode>();

    for (int i = 0; i < frequencies.Length; ++i)
      if (frequencies[i] > 0)
        heap.Insert(new HuffmanNode(i, frequencies[i]));

    if (heap.Count == 0)
      throw new ArgumentException("At least one symbol must have a non-zero frequency.", nameof(frequencies));

    // Single-symbol tree: create a dummy internal node
    if (heap.Count == 1) {
      var single = heap.ExtractMin();
      return new HuffmanNode(single, new HuffmanNode(-2, 0));
    }

    while (heap.Count > 1) {
      var left = heap.ExtractMin();
      var right = heap.ExtractMin();
      heap.Insert(new HuffmanNode(left, right));
    }

    return heap.ExtractMin();
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
    bool needsAdjustment = false;
    for (int i = 0; i < codeLengths.Length; ++i)
      if (codeLengths[i] > maxLength) {
        needsAdjustment = true;
        break;
      }

    if (!needsAdjustment)
      return;

    // Collect non-zero lengths and sort descending
    var symbols = new List<(int Symbol, int Length)>();
    for (int i = 0; i < codeLengths.Length; ++i)
      if (codeLengths[i] > 0)
        symbols.Add((i, codeLengths[i]));

    // Clamp lengths to maxLength
    for (int i = 0; i < symbols.Count; ++i)
      if (symbols[i].Length > maxLength)
        symbols[i] = (symbols[i].Symbol, maxLength);

    // Adjust to satisfy Kraft inequality: sum of 2^(-length) <= 1
    // We work with integer Kraft values: sum of 2^(maxLength - length) <= 2^maxLength
    for (;;) {
      long kraftSum = 0;
      long kraftMax = 1L << maxLength;
      for (var i = 0; i < symbols.Count; ++i)
        kraftSum += 1L << (maxLength - symbols[i].Length);

      if (kraftSum <= kraftMax)
        break;

      // Find the longest code and shorten it by 1
      int longestIdx = 0;
      for (var i = 1; i < symbols.Count; ++i)
        if (symbols[i].Length > symbols[longestIdx].Length)
          longestIdx = i;

      symbols[longestIdx] = (symbols[longestIdx].Symbol, symbols[longestIdx].Length - 1);
    }

    // Redistribute excess Kraft space to the shortest codes (make them longer)
    // to keep the tree balanced
    for (;;) {
      long kraftSum = 0;
      long kraftMax = 1L << maxLength;
      for (int i = 0; i < symbols.Count; ++i)
        kraftSum += 1L << (maxLength - symbols[i].Length);

      long excess = kraftMax - kraftSum;
      if (excess <= 0)
        break;

      // Find shortest code that can be lengthened
      int shortestIdx = -1;
      int shortestLen = int.MaxValue;
      for (int i = 0; i < symbols.Count; ++i)
        if (symbols[i].Length < maxLength && symbols[i].Length < shortestLen) {
          shortestLen = symbols[i].Length;
          shortestIdx = i;
        }

      if (shortestIdx < 0)
        break;

      long freed = 1L << (maxLength - symbols[shortestIdx].Length);
      long needed = 1L << (maxLength - symbols[shortestIdx].Length - 1);
      if (freed - needed <= excess)
        symbols[shortestIdx] = (symbols[shortestIdx].Symbol, symbols[shortestIdx].Length + 1);
      else
        break;
    }

    // Write back
    Array.Clear(codeLengths);
    for (int i = 0; i < symbols.Count; ++i)
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
