namespace FileFormat.StuffIt;

/// <summary>
/// StuffIt method 5 (LZAH): LZSS over a 4 KiB window with an adaptive Huffman code for the 314
/// literal/length symbols (the LZHUF scheme of LHarc's -lh1-) and a fixed 64-entry code for the
/// top six distance bits, all read most-significant bit first.
/// </summary>
/// <remarks>Converted from XADMaster's XADLZHDynamicHandle (LGPL-2.1-or-later), including its window pre-fill.</remarks>
internal static class StuffItLzahDecoder {
  private const int NumLeaves = 314;
  private const int NumNodes = NumLeaves * 2 - 1;
  private const int WindowSize = 4096;

  private static readonly int[] DistanceLengths = [
    3, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6,
    6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7,
    7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
  ];

  private sealed class Node {
    public Node? Parent, Left, Right;
    public int Index, Freq, Value;
    public bool IsLeaf => this.Left is null && this.Right is null;
  }

  public static byte[] Decode(byte[] input, int outputLength) {
    var output = new byte[outputLength];
    if (outputLength == 0) return output;
    var reader = new StuffItBitReader(input);
    var distanceCode = StuffItPrefixCode.FromLengths(DistanceLengths, 8);

    // Node storage: storage[0] is always the root; nodes[] is the frequency-ordered list.
    var storage = new Node[NumNodes];
    for (var i = 0; i < NumNodes; ++i) storage[i] = new Node();
    var nodes = (Node[])storage.Clone();
    for (var i = 0; i < NumLeaves; ++i) {
      var index = NumNodes - 1 - i;
      nodes[index].Index = index;
      nodes[index].Freq = 1;
      nodes[index].Value = i;
    }
    for (var i = NumLeaves - 2; i >= 0; --i) {
      var n = nodes[i];
      n.Index = i;
      n.Left = nodes[2 * i + 1];
      n.Right = nodes[2 * i + 2];
      n.Left.Parent = n;
      n.Right.Parent = n;
      n.Freq = n.Left.Freq + n.Right.Freq;
    }

    var window = new byte[WindowSize];
    for (var i = 0; i < 256; ++i) window.AsSpan(i * 13 + 18, 13).Fill((byte)i);
    for (var i = 0; i < 256; ++i) window[256 * 13 + 18 + i] = (byte)i;
    for (var i = 0; i < 256; ++i) window[256 * 13 + 256 + 18 + i] = (byte)(255 - i);
    window.AsSpan(256 * 13 + 512 + 18, 128).Clear();
    window.AsSpan(256 * 13 + 512 + 128 + 18, 128 - 18).Fill((byte)' ');

    var o = 0;
    while (o < outputLength) {
      var node = storage[0];
      while (!node.IsLeaf) {
        node = (reader.ReadBit() != 0 ? node.Left : node.Right)
          ?? throw new InvalidDataException("StuffIt LZAH tree is damaged.");
      }
      Update(storage, nodes, node);

      var symbol = node.Value;
      if (symbol < 0x100) {
        window[o & (WindowSize - 1)] = (byte)symbol;
        output[o++] = (byte)symbol;
        continue;
      }
      var length = symbol - 0x100 + 3;
      var high = distanceCode.Decode(reader);
      var distance = (high << 6) + reader.ReadBits(6) + 1;
      var src = o - distance;
      for (var k = 0; k < length && o < outputLength; ++k, ++src) {
        var b = window[src & (WindowSize - 1)];
        window[o & (WindowSize - 1)] = b;
        output[o++] = b;
      }
    }
    return output;
  }

  private static void Update(Node[] storage, Node[] nodes, Node node) {
    if (storage[0].Freq == 0x8000) Reconstruct(storage, nodes);
    for (Node? n = node; n is not null; n = n.Parent) {
      ++n.Freq;
      if (n.Parent is null) break;
      Rearrange(nodes, n);
    }
  }

  private static void Rearrange(Node[] nodes, Node p) {
    var pIndex = p.Index;
    var qIndex = pIndex;
    while (qIndex > 0 && nodes[qIndex - 1].Freq < p.Freq) --qIndex;
    if (qIndex >= pIndex) return;

    var q = nodes[qIndex];
    var newQParent = p.Parent!;
    var newPParent = q.Parent!;
    var pIsRight = p.Parent!.Right == p;
    var qIsRight = q.Parent!.Right == q;

    if (pIsRight) p.Parent.Right = q; else p.Parent.Left = q;
    if (qIsRight) q.Parent.Right = p; else q.Parent.Left = p;

    p.Parent = newPParent;
    q.Parent = newQParent;

    nodes[pIndex] = q;
    q.Index = pIndex;
    nodes[qIndex] = p;
    p.Index = qIndex;
  }

  private static void Reconstruct(Node[] storage, Node[] nodes) {
    var leaves = new Node[NumLeaves];
    var n = 0;
    for (var i = 0; i < NumNodes; ++i) {
      if (!nodes[i].IsLeaf) continue;
      var leaf = nodes[i];
      leaf.Freq = (leaf.Freq + 1) / 2;
      leaves[n++] = leaf;
    }

    var leafIndex = NumLeaves - 1;
    var branchIndex = NumLeaves - 2;
    var nodeIndex = NumNodes - 1;
    var pairIndex = NumNodes - 2;

    while (nodeIndex >= 0) {
      while (nodeIndex >= pairIndex) {
        nodes[nodeIndex] = leaves[leafIndex];
        nodes[nodeIndex].Index = nodeIndex;
        --nodeIndex;
        --leafIndex;
      }

      var branch = storage[branchIndex--];
      branch.Left = nodes[pairIndex];
      branch.Right = nodes[pairIndex + 1];
      branch.Left.Parent = branch;
      branch.Right.Parent = branch;
      branch.Freq = branch.Left.Freq + branch.Right.Freq;

      while (leafIndex >= 0 && leaves[leafIndex].Freq <= branch.Freq) {
        nodes[nodeIndex] = leaves[leafIndex];
        nodes[nodeIndex].Index = nodeIndex;
        --nodeIndex;
        --leafIndex;
      }

      nodes[nodeIndex] = branch;
      nodes[nodeIndex].Index = nodeIndex;
      --nodeIndex;
      pairIndex -= 2;
    }
    nodes[0].Parent = null;
  }
}
