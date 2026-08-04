using Compression.Core.BitIO;

namespace Compression.Core.Entropy.AdaptiveHuffman;

/// <summary>
/// FGK (Faller-Gallager-Knuth) adaptive Huffman coding tree.
/// Maintains a binary code tree that is incrementally rebalanced after every
/// symbol, so no code table is ever transmitted — the encoder and decoder derive
/// the same codes purely by replaying the identical update procedure over the
/// symbols seen so far.
/// </summary>
/// <remarks>
/// This is the classic Faller/Gallager/Knuth scheme (Vitter's improvement, which
/// tightens the node-numbering invariant for a provably better worst case, is not
/// implemented here):
/// <list type="bullet">
///   <item>N. Faller, "An Adaptive System for Data Compression", Record of the 7th
///   Asilomar Conference on Circuits, Systems and Computers, 1973.</item>
///   <item>R. G. Gallager, "Variations on a theme by Huffman", IEEE Transactions on
///   Information Theory 24(6), 1978.</item>
///   <item>D. E. Knuth, "Dynamic Huffman Coding", Journal of Algorithms 6(2), 1985.</item>
/// </list>
/// The tree starts as a single NYT ("Not Yet Transmitted") node. Every node is kept
/// numbered so that, read in increasing number order, weights are non-decreasing and
/// every parent's number exceeds both of its children's numbers (the "sibling
/// property"). To encode an already-seen symbol, the path from the root to its leaf
/// is emitted. To encode a symbol seen for the first time, the path to the current
/// NYT node is emitted, followed by the raw 8-bit symbol value; the NYT node is then
/// split into a new internal node with a fresh NYT child and a new leaf for the
/// symbol. After every symbol, the weight of its leaf (new or existing) is
/// incremented by one, then propagated up to the root: at each node on the path, if
/// the node is not already the highest-numbered node among its weight peers, it is
/// swapped with that peer (skipping the node's own ancestors) before the weight is
/// incremented, which is what keeps the sibling property intact incrementally
/// instead of rebuilding the tree from scratch.
/// </remarks>
internal sealed class AdaptiveHuffmanTree {
  private const int SymbolCount = 256;

  private readonly Node _nyt;
  private readonly Node?[] _symbolNode = new Node?[AdaptiveHuffmanTree.SymbolCount];
  private readonly List<Node> _order = [];
  private Node _root;

  /// <summary>
  /// Initializes a new tree containing only the NYT node (the state before any
  /// symbol has been processed).
  /// </summary>
  public AdaptiveHuffmanTree() {
    this._nyt = new() { Weight = 0, Symbol = -1, IsNyt = true, Number = 1 };
    this._root = this._nyt;
    this._order.Add(this._nyt);
  }

  /// <summary>
  /// Encodes one symbol: emits its current code (or the NYT escape plus raw byte for
  /// a symbol seen for the first time), then updates the tree.
  /// </summary>
  /// <typeparam name="TOrder">The bit order used by <paramref name="writer"/>.</typeparam>
  /// <param name="writer">The bit writer to append the code to.</param>
  /// <param name="symbol">The symbol to encode.</param>
  public void EncodeSymbol<TOrder>(BitWriter<TOrder> writer, byte symbol) where TOrder : struct, IBitOrder {
    var leaf = this._symbolNode[symbol];

    if (leaf != null) {
      foreach (var bit in AdaptiveHuffmanTree.GetPathBits(leaf))
        writer.WriteBit(bit);

      this.UpdateTree(leaf);
      return;
    }

    foreach (var bit in AdaptiveHuffmanTree.GetPathBits(this._nyt))
      writer.WriteBit(bit);

    for (var i = 7; i >= 0; --i)
      writer.WriteBit((symbol >> i) & 1);

    var newLeaf = this.SplitNyt(symbol);
    this.UpdateTree(newLeaf);
  }

  /// <summary>
  /// Decodes one symbol by descending the tree bit-by-bit (reading a raw byte on a
  /// NYT escape), then applies the same tree update as the encoder.
  /// </summary>
  /// <typeparam name="TOrder">The bit order used by <paramref name="reader"/>.</typeparam>
  /// <param name="reader">The bit buffer to read the code from.</param>
  /// <returns>The decoded symbol.</returns>
  public byte DecodeSymbol<TOrder>(BitBuffer<TOrder> reader) where TOrder : struct, IBitOrder {
    var node = this._root;
    while (!node.IsLeaf)
      node = reader.ReadBits(1) == 0 ? node.Left! : node.Right!;

    if (!node.IsNyt) {
      var symbol = (byte)node.Symbol;
      this.UpdateTree(node);
      return symbol;
    }

    var raw = 0;
    for (var i = 0; i < 8; ++i)
      raw = (raw << 1) | (int)reader.ReadBits(1);

    var newLeaf = this.SplitNyt((byte)raw);
    this.UpdateTree(newLeaf);
    return (byte)raw;
  }

  /// <summary>
  /// Replaces the current NYT node with an internal node whose children are a fresh
  /// NYT (weight 0, taking over as the tree's new escape node) and a new leaf for
  /// <paramref name="symbol"/> (weight 0, incremented to 1 by the caller's
  /// subsequent <see cref="UpdateTree"/> call).
  /// </summary>
  private Node SplitNyt(byte symbol) {
    var oldNyt = this._nyt;
    var newLeaf = new Node { Weight = 0, Symbol = symbol };
    var newInternal = new Node { Weight = 0, Symbol = -1 };

    var parent = oldNyt.Parent;
    newInternal.Parent = parent;
    if (parent == null)
      this._root = newInternal;
    else if (parent.Left == oldNyt)
      parent.Left = newInternal;
    else
      parent.Right = newInternal;

    newInternal.Left = oldNyt;
    newInternal.Right = newLeaf;
    oldNyt.Parent = newInternal;
    newLeaf.Parent = newInternal;

    // oldNyt keeps its number (lowest weight-0 slot); newLeaf and newInternal are
    // inserted directly above it and everything above is renumbered.
    var insertAt = oldNyt.Number; // 0-based index right after oldNyt's slot
    this._order.Insert(insertAt, newLeaf);
    this._order.Insert(insertAt + 1, newInternal);
    for (var i = insertAt; i < this._order.Count; ++i)
      this._order[i].Number = i + 1;

    this._symbolNode[symbol] = newLeaf;
    return newLeaf;
  }

  /// <summary>
  /// Increments the weight of <paramref name="start"/> and every ancestor up to the
  /// root, swapping each node with the highest-numbered node sharing its weight
  /// (excluding its own ancestors) beforehand so the sibling property is preserved.
  /// </summary>
  private void UpdateTree(Node start) {
    var node = start;
    while (node != null) {
      var swapWith = this.FindSwapCandidate(node);
      if (swapWith != null)
        this.Swap(node, swapWith);

      ++node.Weight;
      node = node.Parent;
    }
  }

  /// <summary>
  /// Finds the highest-numbered node with the same weight as <paramref name="node"/>,
  /// excluding <paramref name="node"/> itself and any of its ancestors. Same-weight
  /// nodes always occupy a contiguous run in <see cref="_order"/>, so this walks that
  /// run from its top end downward.
  /// </summary>
  private Node? FindSwapCandidate(Node node) {
    var weight = node.Weight;
    var hi = node.Number - 1;
    while (hi + 1 < this._order.Count && this._order[hi + 1].Weight == weight)
      ++hi;

    for (var i = hi; i > node.Number - 1; --i) {
      var candidate = this._order[i];
      if (!AdaptiveHuffmanTree.IsAncestorOf(candidate, node))
        return candidate;
    }

    return null;
  }

  private static bool IsAncestorOf(Node candidate, Node node) {
    for (var n = node.Parent; n != null; n = n.Parent)
      if (n == candidate)
        return true;

    return false;
  }

  /// <summary>
  /// Exchanges the tree positions (parent/child links) and numbers of two nodes,
  /// carrying each node's own subtree along with it.
  /// </summary>
  private void Swap(Node a, Node b) {
    var pa = a.Parent;
    var pb = b.Parent;
    var aWasLeft = pa != null && pa.Left == a;
    var bWasLeft = pb != null && pb.Left == b;

    a.Parent = pb;
    if (pb == null)
      this._root = a;
    else if (bWasLeft)
      pb.Left = a;
    else
      pb.Right = a;

    b.Parent = pa;
    if (pa == null)
      this._root = b;
    else if (aWasLeft)
      pa.Left = b;
    else
      pa.Right = b;

    (a.Number, b.Number) = (b.Number, a.Number);
    this._order[a.Number - 1] = a;
    this._order[b.Number - 1] = b;
  }

  /// <summary>
  /// Returns the root-to-<paramref name="target"/> path as a sequence of bits
  /// (0 = left child, 1 = right child), root first.
  /// </summary>
  private static List<int> GetPathBits(Node target) {
    var bits = new List<int>();
    for (var n = target; n.Parent != null; n = n.Parent)
      bits.Add(n.Parent.Left == n ? 0 : 1);

    bits.Reverse();
    return bits;
  }

  /// <summary>
  /// A node in the adaptive Huffman tree. Internal nodes have <see cref="Symbol"/>
  /// set to -1; leaves carry either a byte value or the NYT marker.
  /// </summary>
  private sealed class Node {
    public int Weight;
    public int Symbol = -1;
    public bool IsNyt;
    public int Number;
    public Node? Parent;
    public Node? Left;
    public Node? Right;

    public bool IsLeaf => this.Left == null && this.Right == null;
  }
}
