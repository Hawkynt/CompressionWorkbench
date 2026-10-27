namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// The adaptive Huffman tree of LHarc <c>-lh1-</c> (LZHUF), shared by <see cref="Lh1Decoder"/> and
/// <see cref="Lh1Encoder"/> so both evolve it identically.
/// </summary>
/// <remarks>
/// <para>314 leaves (256 literals and 58 copy lengths 3-60) in a table kept sorted by decreasing
/// frequency; node 0 is the root and a branch's children are <c>child</c> (bit 0) and <c>child - 1</c>
/// (bit 1). Nodes of equal frequency form a group whose leftmost node is its leader; incrementing a node
/// first swaps it with its leader, which keeps the table sorted. When the root count reaches 32 KiB the
/// leaf counts are halved and the tree rebuilt.</para>
/// <para>The 64 position codes use a fixed table: 1 code of 3 bits, 3 of 4, 8 of 5, 12 of 6, 24 of 7
/// and 16 of 8, followed by 6 raw low bits.</para>
/// <para>Sourcing: converted from lhasa's <c>lh1_decoder.c</c> (ISC, Simon Howard), tables copied
/// exactly; see THIRD-PARTY-NOTICE.lhasa.txt beside this file.</para>
/// </remarks>
internal sealed class Lh1Tree {
  public const int NumCodes = 314;
  public const int NumNodes = NumCodes * 2 - 1;
  public const int NumOffsets = 64;
  public const int CopyThreshold = 3;
  private const int ReorderLimit = 32 * 1024;
  private static readonly int[] OffsetDistribution = [1, 3, 8, 12, 24, 16];

  private readonly bool[] _leaf = new bool[NumNodes];
  private readonly ushort[] _child = new ushort[NumNodes];
  private readonly ushort[] _parent = new ushort[NumNodes];
  private readonly ushort[] _freq = new ushort[NumNodes];
  private readonly ushort[] _group = new ushort[NumNodes];
  private readonly ushort[] _leafNodes = new ushort[NumCodes];
  private readonly ushort[] _groups = new ushort[NumNodes];
  private readonly ushort[] _groupLeader = new ushort[NumNodes];
  private int _numGroups;

  /// <summary>Maps the next 8 input bits to a position code.</summary>
  public readonly byte[] OffsetLookup = new byte[256];

  /// <summary>Bit length of each position code.</summary>
  public readonly byte[] OffsetLengths = new byte[NumOffsets];

  /// <summary>Each position code's bits, left-aligned in a byte.</summary>
  public readonly byte[] OffsetCodes = new byte[NumOffsets];

  public Lh1Tree() {
    this.InitGroups();
    this.InitTree();
    this.InitOffsetTable();
  }

  public bool IsLeaf(int node) => this._leaf[node];

  public int Child(int node) => this._child[node];

  public int Parent(int node) => this._parent[node];

  public int LeafOf(int code) => this._leafNodes[code];

  private ushort AllocGroup() => this._groups[this._numGroups++];

  private void FreeGroup(ushort group) => this._groups[--this._numGroups] = group;

  private void InitGroups() {
    for (var i = 0; i < NumNodes; ++i)
      this._groups[i] = (ushort)i;
    this._numGroups = 0;
  }

  private void InitTree() {
    var nodeIndex = NumNodes - 1;
    var leafGroup = this.AllocGroup();
    for (var i = 0; i < NumCodes; ++i) {
      this._leaf[nodeIndex] = true;
      this._child[nodeIndex] = (ushort)i;
      this._freq[nodeIndex] = 1;
      this._group[nodeIndex] = leafGroup;
      this._groupLeader[leafGroup] = (ushort)nodeIndex;
      this._leafNodes[i] = (ushort)nodeIndex;
      --nodeIndex;
    }

    var child = NumNodes - 1;
    while (nodeIndex >= 0) {
      this._leaf[nodeIndex] = false;
      this._child[nodeIndex] = (ushort)child;
      this._parent[child] = (ushort)nodeIndex;
      this._parent[child - 1] = (ushort)nodeIndex;
      this._freq[nodeIndex] = (ushort)(this._freq[child] + this._freq[child - 1]);
      this._group[nodeIndex] = this._freq[nodeIndex] == this._freq[nodeIndex + 1]
        ? this._group[nodeIndex + 1]
        : this.AllocGroup();
      this._groupLeader[this._group[nodeIndex]] = (ushort)nodeIndex;
      --nodeIndex;
      child -= 2;
    }
  }

  private void InitOffsetTable() {
    var code = 0;
    var offset = 0;
    for (var i = 0; i < OffsetDistribution.Length; ++i) {
      var length = i + 3;
      var iterBit = 1 << (8 - length);
      for (var j = 0; j < OffsetDistribution[i]; ++j) {
        for (var k = 0; k < iterBit; ++k)
          this.OffsetLookup[code | k] = (byte)offset;
        this.OffsetLengths[offset] = (byte)length;
        this.OffsetCodes[offset] = (byte)code;
        code += iterBit;
        ++offset;
      }
    }
  }

  private int MakeGroupLeader(int nodeIndex) {
    var group = this._group[nodeIndex];
    int leaderIndex = this._groupLeader[group];
    if (leaderIndex == nodeIndex)
      return nodeIndex;

    (this._leaf[leaderIndex], this._leaf[nodeIndex]) = (this._leaf[nodeIndex], this._leaf[leaderIndex]);
    (this._child[leaderIndex], this._child[nodeIndex]) = (this._child[nodeIndex], this._child[leaderIndex]);
    this.Reparent(nodeIndex);
    this.Reparent(leaderIndex);
    return leaderIndex;
  }

  private void Reparent(int index) {
    if (this._leaf[index])
      this._leafNodes[this._child[index]] = (ushort)index;
    else {
      this._parent[this._child[index]] = (ushort)index;
      this._parent[this._child[index] - 1] = (ushort)index;
    }
  }

  private void IncrementNodeFreq(int nodeIndex) {
    var other = nodeIndex - 1;
    ++this._freq[nodeIndex];
    if (nodeIndex < NumNodes - 1 && this._group[nodeIndex] == this._group[nodeIndex + 1]) {
      ++this._groupLeader[this._group[nodeIndex]];
      if (this._freq[nodeIndex] == this._freq[other])
        this._group[nodeIndex] = this._group[other];
      else {
        this._group[nodeIndex] = this.AllocGroup();
        this._groupLeader[this._group[nodeIndex]] = (ushort)nodeIndex;
      }
    } else if (this._freq[nodeIndex] == this._freq[other]) {
      this.FreeGroup(this._group[nodeIndex]);
      this._group[nodeIndex] = this._group[other];
    }
  }

  private void Reconstruct() {
    // Gather the leaves at the start, halving their counts.
    var leafCount = 0;
    var leafLeaf = new bool[NumCodes];
    var leafChild = new ushort[NumCodes];
    var leafFreq = new ushort[NumCodes];
    for (var i = 0; i < NumNodes; ++i) {
      if (!this._leaf[i])
        continue;
      leafChild[leafCount] = this._child[i];
      leafFreq[leafCount] = (ushort)((this._freq[i] + 1) / 2);
      leafLeaf[leafCount] = true;
      ++leafCount;
    }
    for (var i = 0; i < NumCodes; ++i) {
      this._leaf[i] = leafLeaf[i];
      this._child[i] = leafChild[i];
      this._freq[i] = leafFreq[i];
    }

    var leaf = NumCodes - 1;
    var child = NumNodes - 1;
    var index = NumNodes - 1;
    while (index >= 0) {
      while (child - index < 2) {
        this.CopyNode(index, leaf);
        --index;
        --leaf;
      }
      var freq = this._freq[child] + this._freq[child - 1];
      while (leaf >= 0 && freq >= this._freq[leaf]) {
        this.CopyNode(index, leaf);
        --index;
        --leaf;
      }
      this._leaf[index] = false;
      this._freq[index] = (ushort)freq;
      this._child[index] = (ushort)child;
      this._parent[child] = (ushort)index;
      this._parent[child - 1] = (ushort)index;
      --index;
      child -= 2;
    }

    this.InitGroups();
    var group = this.AllocGroup();
    this._group[0] = group;
    this._groupLeader[group] = 0;
    for (var i = 1; i < NumNodes; ++i) {
      if (this._freq[i] == this._freq[i - 1])
        this._group[i] = this._group[i - 1];
      else {
        group = this.AllocGroup();
        this._group[i] = group;
        this._groupLeader[group] = (ushort)i;
      }
    }
  }

  /// <summary>Copies leaf node <paramref name="from"/> to <paramref name="to"/> (from ≤ to), as the reference's struct copy does.</summary>
  private void CopyNode(int to, int from) {
    this._leaf[to] = this._leaf[from];
    this._child[to] = this._child[from];
    this._freq[to] = this._freq[from];
    this._parent[to] = this._parent[from];
    this._group[to] = this._group[from];
    this._leafNodes[this._child[from]] = (ushort)to;
  }

  /// <summary>Counts one occurrence of <paramref name="code"/>, reshaping the tree.</summary>
  public void Increment(int code) {
    if (this._freq[0] >= ReorderLimit)
      this.Reconstruct();
    ++this._freq[0];
    int nodeIndex = this._leafNodes[code];
    while (nodeIndex != 0) {
      nodeIndex = this.MakeGroupLeader(nodeIndex);
      this.IncrementNodeFreq(nodeIndex);
      nodeIndex = this._parent[nodeIndex];
    }
  }
}
