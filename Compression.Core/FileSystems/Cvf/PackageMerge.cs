namespace Compression.Core.FileSystems.Cvf;

/// <summary>
/// Optimal length-limited prefix code lengths by the package-merge algorithm (Larmore and Hirschberg,
/// "A fast algorithm for optimal length-limited Huffman codes", JACM 37(3), 1990). The result is
/// always a complete code — Kraft sum exactly one — which SD-4 requires of both its codes.
/// </summary>
internal static class PackageMerge {
  /// <summary>
  /// Code lengths for <paramref name="frequencies"/> (zero frequency = unused, length 0), none longer
  /// than <paramref name="maxLength"/>.
  /// </summary>
  /// <exception cref="ArgumentException">Fewer than two used symbols, or more than <c>2^maxLength</c>.</exception>
  public static int[] Lengths(ReadOnlySpan<long> frequencies, int maxLength) {
    var lengths = new int[frequencies.Length];
    var leaves = new List<int>();
    for (var s = 0; s < frequencies.Length; ++s)
      if (frequencies[s] > 0) leaves.Add(s);
    if (leaves.Count < 2 || leaves.Count > 1L << maxLength)
      throw new ArgumentException($"package-merge needs 2..2^{maxLength} used symbols, got {leaves.Count}.", nameof(frequencies));

    var freq = frequencies.ToArray();
    leaves.Sort((a, b) => freq[a] != freq[b] ? freq[a].CompareTo(freq[b]) : a.CompareTo(b));

    // Nodes: a leaf (Symbol >= 0) or a package of two nodes from the previous level.
    var nodes = new List<(long Weight, int Symbol, int Left, int Right)>();
    var leafNodes = new List<int>(leaves.Count);
    foreach (var s in leaves) {
      leafNodes.Add(nodes.Count);
      nodes.Add((freq[s], s, -1, -1));
    }

    var level = leafNodes;
    for (var depth = 1; depth < maxLength; ++depth) {
      var packages = new List<int>(level.Count / 2);
      for (var i = 0; i + 1 < level.Count; i += 2) {
        packages.Add(nodes.Count);
        nodes.Add((nodes[level[i]].Weight + nodes[level[i + 1]].Weight, -1, level[i], level[i + 1]));
      }

      // Merge the packages into the leaves by weight; leaves first on ties.
      var merged = new List<int>(leafNodes.Count + packages.Count);
      int a = 0, b = 0;
      while (a < leafNodes.Count || b < packages.Count)
        merged.Add(b >= packages.Count || (a < leafNodes.Count && nodes[leafNodes[a]].Weight <= nodes[packages[b]].Weight)
          ? leafNodes[a++]
          : packages[b++]);
      level = merged;
    }

    // Each appearance of a leaf among the first 2n - 2 items adds one bit to its code.
    var stack = new Stack<int>();
    for (var i = 0; i < 2 * leaves.Count - 2; ++i) stack.Push(level[i]);
    while (stack.Count > 0) {
      var node = nodes[stack.Pop()];
      if (node.Symbol >= 0) {
        ++lengths[node.Symbol];
        continue;
      }
      stack.Push(node.Left);
      stack.Push(node.Right);
    }
    return lengths;
  }
}
