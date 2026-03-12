namespace Compression.Core.Entropy.Huffman;

/// <summary>
/// A node in a Huffman tree. Leaf nodes carry a symbol; internal nodes have left and right children.
/// </summary>
public sealed class HuffmanNode : IComparable<HuffmanNode> {
  /// <summary>
  /// Gets the symbol value for leaf nodes, or -1 for internal nodes.
  /// </summary>
  public int Symbol { get; }

  /// <summary>
  /// Gets the frequency (weight) of this node.
  /// </summary>
  public long Frequency { get; }

  /// <summary>
  /// Gets the left child (represents bit 0), or <c>null</c> for leaf nodes.
  /// </summary>
  public HuffmanNode? Left { get; }

  /// <summary>
  /// Gets the right child (represents bit 1), or <c>null</c> for leaf nodes.
  /// </summary>
  public HuffmanNode? Right { get; }

  /// <summary>
  /// Gets whether this is a leaf node.
  /// </summary>
  public bool IsLeaf => this.Left is null && this.Right is null;

  /// <summary>
  /// Creates a leaf node with the specified symbol and frequency.
  /// </summary>
  /// <param name="symbol">The symbol value.</param>
  /// <param name="frequency">The frequency of this symbol.</param>
  public HuffmanNode(int symbol, long frequency) {
    this.Symbol = symbol;
    this.Frequency = frequency;
  }

  /// <summary>
  /// Creates an internal node with the specified children.
  /// </summary>
  /// <param name="left">The left child (bit 0).</param>
  /// <param name="right">The right child (bit 1).</param>
  public HuffmanNode(HuffmanNode left, HuffmanNode right) {
    this.Symbol = -1;
    this.Left = left;
    this.Right = right;
    this.Frequency = left.Frequency + right.Frequency;
  }

  /// <summary>
  /// Compares by frequency for heap ordering.
  /// </summary>
  /// <param name="other">The other node to compare to.</param>
  /// <returns>A negative, zero, or positive value.</returns>
  public int CompareTo(HuffmanNode? other) {
    if (other is null) return 1;
    var cmp = this.Frequency.CompareTo(other.Frequency);

    // Tie-break: leaves before internal nodes, then by symbol
    return cmp != 0 ? cmp : this.Symbol.CompareTo(other.Symbol);
  }
}
