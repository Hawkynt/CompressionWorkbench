namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// One of LZMS's adaptive Huffman codes.
/// </summary>
/// <remarks>
/// <para>Every count starts at one, which is why the initial code is flat. On a
/// rebuild the counts become <c>(count &gt;&gt; 1) + coded since the last rebuild + 1</c>.
/// That shape is worth stating because it hides itself: at the first rebuild the
/// old count is one, so the expression collapses to <c>seen + 1</c> and a whole
/// family of wrong rules fits it. Only the second rebuild tells them apart.</para>
///
/// <para>The tree is ordinary Huffman with ties going to the earliest symbol, and
/// codewords are assigned canonically by length and then by symbol index. That
/// reproduces wimlib's initial codes for every alphabet, and assigning codewords
/// in frequency order instead breaks the very first rebuild.</para>
/// </remarks>
internal sealed class LzmsHuffmanCode {
  private readonly int _symbols;
  private readonly int _rebuildInterval;
  private readonly int[] _frequencies;
  private readonly int[] _since;
  private int _coded;

  private int[] _lengths = [];
  private int[] _codes = [];
  private Dictionary<(int Code, int Length), int> _bySymbolCode = [];

  public LzmsHuffmanCode(int symbols, int rebuildInterval) {
    this._symbols = symbols;
    this._rebuildInterval = rebuildInterval;
    this._frequencies = new int[symbols];
    this._since = new int[symbols];
    Array.Fill(this._frequencies, 1);
    this.Rebuild();
  }

  private void Rebuild() {
    this._lengths = BuildLengths(this._frequencies);
    this.AssignCanonical();
  }

  private void Accumulate(int symbol) {
    ++this._since[symbol];
    if (++this._coded < this._rebuildInterval) return;

    this._coded = 0;
    for (var i = 0; i < this._symbols; ++i) {
      this._frequencies[i] = (this._frequencies[i] >> 1) + this._since[i] + 1;
      this._since[i] = 0;
    }

    this.Rebuild();
  }

  /// <summary>Huffman code lengths, with ties broken towards the earliest symbol.</summary>
  internal static int[] BuildLengths(IReadOnlyList<int> frequencies) {
    var n = frequencies.Count;
    var lengths = new int[n];
    var alive = new List<int>();
    for (var i = 0; i < n; ++i)
      if (frequencies[i] > 0) alive.Add(i);

    if (alive.Count <= 1) {
      if (alive.Count == 1) lengths[alive[0]] = 1;
      return lengths;
    }

    // Nodes are (weight, order, left, right, leaf); ties take the lower order,
    // and leaves are inserted before any internal node exists.
    var heap = new PriorityQueue<int[], (long Weight, int Order)>();
    var nodes = new List<int[]>();
    var order = 0;
    foreach (var symbol in alive) {
      nodes.Add([symbol, -1, -1]);
      heap.Enqueue(nodes[^1], (frequencies[symbol], order++));
    }

    var weights = new Dictionary<int[], long>();
    foreach (var (node, index) in nodes.Select((x, i) => (x, i)))
      weights[node] = frequencies[alive[index]];

    while (heap.Count > 1) {
      var a = heap.Dequeue();
      var b = heap.Dequeue();
      var parent = new[] { -1, nodes.IndexOf(a), nodes.IndexOf(b) };
      nodes.Add(parent);
      weights[parent] = weights[a] + weights[b];
      heap.Enqueue(parent, (weights[parent], order++));
    }

    var root = heap.Dequeue();
    var stack = new Stack<(int[] Node, int Depth)>();
    stack.Push((root, 0));
    while (stack.Count > 0) {
      var (node, depth) = stack.Pop();
      if (node[0] >= 0) {
        lengths[node[0]] = depth == 0 ? 1 : depth;
        continue;
      }

      stack.Push((nodes[node[1]], depth + 1));
      stack.Push((nodes[node[2]], depth + 1));
    }

    return lengths;
  }

  private void AssignCanonical() {
    this._codes = new int[this._symbols];
    this._bySymbolCode = [];
    var max = 0;
    foreach (var length in this._lengths)
      if (length > max) max = length;

    var code = 0;
    var previous = 0;
    for (var length = 1; length <= max; ++length) {
      code <<= previous == 0 ? length : length - previous;
      previous = length;
      for (var symbol = 0; symbol < this._symbols; ++symbol) {
        if (this._lengths[symbol] != length) continue;
        this._codes[symbol] = code;
        this._bySymbolCode[(code, length)] = symbol;
        ++code;
      }
    }
  }

  /// <summary>Writes a symbol and folds it into the counts.</summary>
  public void Write(LzmsBackwardBitWriter writer, int symbol) {
    writer.Write(this._codes[symbol], this._lengths[symbol]);
    this.Accumulate(symbol);
  }

  /// <summary>Reads a symbol and folds it into the counts.</summary>
  public int Read(LzmsBackwardBitReader reader) {
    int value = 0, length = 0;
    while (length < 32) {
      value = (value << 1) | reader.ReadOne();
      ++length;
      if (!this._bySymbolCode.TryGetValue((value, length), out var symbol)) continue;

      this.Accumulate(symbol);
      return symbol;
    }

    throw new InvalidDataException("No LZMS codeword matches the bits read.");
  }
}
