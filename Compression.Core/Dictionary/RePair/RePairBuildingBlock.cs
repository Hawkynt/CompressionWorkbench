using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.RePair;

/// <summary>
/// Exposes Re-Pair (Recursive Pairing) as a benchmarkable building block.
/// An offline grammar-based compression algorithm that repeatedly replaces the most
/// frequent pair of adjacent symbols with a new non-terminal, building a straight-line
/// grammar. The grammar rules and final sequence are then serialized.
/// The sequence lives in a doubly linked list over the original slot numbers, so a
/// slot's number never changes and list order is always slot order. Pair frequencies
/// are counted once and then maintained incrementally: a substitution only disturbs
/// the two neighbouring positions, so a round costs work proportional to the
/// substitutions it makes rather than to the sequence length.
/// Reference: N. J. Larsson and A. Moffat, "Off-Line Dictionary-Based Compression",
/// Proceedings of the IEEE 88(11), 2000, pp. 1722-1732.
/// </summary>
/// <remarks>
/// Selection order is total and explicit, and is mirrored exactly by the JavaScript
/// port; nothing is left to a container's iteration order.
/// <list type="number">
/// <item>Highest occurrence count wins, counting every adjacent position including
/// overlapping ones, so that "aaa" contains the pair (a,a) twice.</item>
/// <item>Ties are broken by the smallest slot number at which the pair occurs, that
/// is, by the pair appearing earliest in the current sequence.</item>
/// <item>A pair must occur at least twice to be eligible at all.</item>
/// </list>
/// </remarks>
public sealed class RePairBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_RePair";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Re-Pair";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Recursive Pairing, offline grammar-based compression";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  // Non-terminals start at 256 (above byte range).
  private const int FirstNonTerminal = 256;

  // Symbols are written to the stream as 16-bit values, and rule r is referred to
  // as FirstNonTerminal + r, so the last rule that can be named is 65535 - 256.
  // The former limit of 65536 let rule numbers run past what the wire format can
  // express: they wrapped on serialisation and the stream decoded to the wrong
  // bytes with nothing raised. Stopping here costs a little ratio on inputs that
  // would exceed it and changes no output that was previously decodable.
  private const int MaxRules = 65536 - FirstNonTerminal;

  /// <inheritdoc/>
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write 4-byte LE uncompressed size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var length = data.Length;

    // The sequence as a doubly linked list over slot numbers.
    var symbol = new int[length];
    var nextSlot = new int[length];
    var previousSlot = new int[length];
    for (var slot = 0; slot < length; slot++) {
      symbol[slot] = data[slot];
      nextSlot[slot] = slot + 1 < length ? slot + 1 : -1;
      previousSlot[slot] = slot - 1;
    }

    var pairs = new PairTable(symbol, nextSlot);
    for (var slot = 0; slot + 1 < length; slot++)
      pairs.Add(symbol[slot], symbol[slot + 1], slot);
    pairs.PublishTouched();

    // Grammar rules: rule[i] = (left, right) for non-terminal (FirstNonTerminal + i).
    var rules = new List<(int Left, int Right)>();
    var remaining = length;

    while (rules.Count < MaxRules) {
      var winner = pairs.SelectPair();
      if (winner < 0)
        break;

      var left = pairs.LeftOf(winner);
      var right = pairs.RightOf(winner);
      var newSymbol = FirstNonTerminal + rules.Count;
      rules.Add((left, right));

      // The surviving occurrences of the winning pair are exactly the ones this
      // loop has not consumed, and they always lie to the right of the slot just
      // fused, so taking them in ascending slot order reproduces the
      // non-overlapping left-to-right scan without walking the sequence.
      for (; ; ) {
        var slot = pairs.EarliestOccurrence(winner);
        if (slot < 0)
          break;

        var partner = nextSlot[slot];
        var before = previousSlot[slot];
        var after = nextSlot[partner];

        if (before >= 0)
          pairs.Remove(symbol[before], symbol[slot]);
        pairs.Remove(left, right);
        if (after >= 0)
          pairs.Remove(symbol[partner], symbol[after]);

        symbol[slot] = newSymbol;
        symbol[partner] = -1;
        nextSlot[slot] = after;
        if (after >= 0)
          previousSlot[after] = slot;
        --remaining;

        if (before >= 0)
          pairs.Add(symbol[before], newSymbol, before);
        if (after >= 0)
          pairs.Add(newSymbol, symbol[after], slot);
      }

      pairs.PublishTouched();
    }

    // Serialize: number of rules, then each rule (left, right as uint16),
    // then final sequence length, then each symbol as uint16.
    Span<byte> buf = stackalloc byte[4];

    BinaryPrimitives.WriteInt32LittleEndian(buf, rules.Count);
    ms.Write(buf);

    Span<byte> pairBuf = stackalloc byte[4];
    foreach (var (left, right) in rules) {
      BinaryPrimitives.WriteUInt16LittleEndian(pairBuf, (ushort)left);
      BinaryPrimitives.WriteUInt16LittleEndian(pairBuf[2..], (ushort)right);
      ms.Write(pairBuf);
    }

    BinaryPrimitives.WriteInt32LittleEndian(buf, remaining);
    ms.Write(buf);

    Span<byte> symBuf = stackalloc byte[2];
    for (var slot = 0; slot >= 0; slot = nextSlot[slot]) {
      BinaryPrimitives.WriteUInt16LittleEndian(symBuf, (ushort)symbol[slot]);
      ms.Write(symBuf);
    }

    return ms.ToArray();
  }

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var offset = 4;

    // Read rules.
    var ruleCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    offset += 4;

    var rules = new (int Left, int Right)[ruleCount];
    for (var i = 0; i < ruleCount; i++) {
      var left = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
      var right = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 2, 2));
      rules[i] = (left, right);
      offset += 4;
    }

    // Read final sequence.
    var seqLen = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    offset += 4;

    var result = new List<byte>(originalSize);
    var expandStack = new Stack<int>();

    for (var i = 0; i < seqLen; i++) {
      var sym = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
      offset += 2;

      // Expand symbol (iterative to avoid stack overflow on deep grammars).
      expandStack.Push(sym);
      while (expandStack.Count > 0) {
        var s = expandStack.Pop();
        if (s < FirstNonTerminal) {
          result.Add((byte)s);
        } else {
          var ruleIdx = s - FirstNonTerminal;
          // Push right first so left is processed first (stack is LIFO).
          expandStack.Push(rules[ruleIdx].Right);
          expandStack.Push(rules[ruleIdx].Left);
        }
      }
    }

    return [.. result];
  }

  /// <summary>
  /// Tracks how often every adjacent symbol pair occurs and where, and answers
  /// which pair the next substitution round must take.
  /// </summary>
  private sealed class PairTable {
    private readonly int[] _symbol;
    private readonly int[] _nextSlot;

    private readonly Dictionary<long, int> _index = [];
    private readonly List<int> _left = [];
    private readonly List<int> _right = [];
    private readonly List<int> _count = [];
    private readonly List<List<int>> _occurrences = [];
    private readonly List<int> _touchedIn = [];

    private readonly List<int> _touched = [];
    private int _round;

    // Candidate queue: one entry per published (count, earliest slot) statement.
    private readonly List<int> _queueCount = [];
    private readonly List<int> _queueSlot = [];
    private readonly List<int> _queuePair = [];

    /// <summary>Binds the table to the sequence it describes.</summary>
    /// <param name="symbol">Symbol per slot; negative for a removed slot.</param>
    /// <param name="nextSlot">Successor per slot, or -1 at the end.</param>
    public PairTable(int[] symbol, int[] nextSlot) {
      this._symbol = symbol;
      this._nextSlot = nextSlot;
    }

    /// <summary>Left symbol of a pair.</summary>
    /// <param name="id">Pair identifier.</param>
    /// <returns>The left symbol.</returns>
    public int LeftOf(int id) => this._left[id];

    /// <summary>Right symbol of a pair.</summary>
    /// <param name="id">Pair identifier.</param>
    /// <returns>The right symbol.</returns>
    public int RightOf(int id) => this._right[id];

    /// <summary>Records one occurrence of a pair at a slot.</summary>
    /// <param name="left">Left symbol.</param>
    /// <param name="right">Right symbol.</param>
    /// <param name="slot">Slot holding the left symbol.</param>
    public void Add(int left, int right, int slot) {
      var id = this.IdOf(left, right);
      ++this._count[id];
      PushOccurrence(this._occurrences[id], slot);
      this.Touch(id);
    }

    /// <summary>Drops one occurrence of a pair.</summary>
    /// <param name="left">Left symbol.</param>
    /// <param name="right">Right symbol.</param>
    public void Remove(int left, int right) {
      var id = this.IdOf(left, right);
      --this._count[id];
      this.Touch(id);
    }

    /// <summary>
    /// Smallest slot still holding this pair. Occurrences are only ever added,
    /// never deleted: once a slot stops holding a given pair it can never hold
    /// that pair again, because a slot's symbol only ever grows and its successor
    /// only changes when that symbol is replaced. Stale entries are therefore
    /// discarded on sight.
    /// </summary>
    /// <param name="id">Pair identifier.</param>
    /// <returns>The slot, or -1 when the pair no longer occurs.</returns>
    public int EarliestOccurrence(int id) {
      var heap = this._occurrences[id];
      var left = this._left[id];
      var right = this._right[id];
      while (heap.Count > 0) {
        var slot = heap[0];
        var after = this._nextSlot[slot];
        if (this._symbol[slot] == left && after >= 0 && this._symbol[after] == right)
          return slot;
        DropOccurrenceTop(heap);
      }

      return -1;
    }

    /// <summary>
    /// Publishes the current state of every pair disturbed since the last call,
    /// so each eligible pair always has one queue entry stating the truth.
    /// </summary>
    public void PublishTouched() {
      foreach (var id in this._touched) {
        if (this._count[id] < 2)
          continue;
        this.QueuePush(this._count[id], this.EarliestOccurrence(id), id);
      }

      this._touched.Clear();
      ++this._round;
    }

    /// <summary>
    /// The pair the next round must replace: highest count, ties going to the
    /// smallest occupied slot. Queue entries that no longer state the truth are
    /// stale and are discarded as they surface.
    /// </summary>
    /// <returns>The pair identifier, or -1 when no pair occurs twice.</returns>
    public int SelectPair() {
      while (this._queueCount.Count > 0) {
        var id = this._queuePair[0];
        if (this._queueCount[0] != this._count[id]) {
          this.QueuePop();
          continue;
        }

        if (this._queueSlot[0] != this.EarliestOccurrence(id)) {
          this.QueuePop();
          continue;
        }

        return id;
      }

      return -1;
    }

    private int IdOf(int left, int right) {
      var key = ((long)left << 32) | (uint)right;
      if (this._index.TryGetValue(key, out var id))
        return id;

      id = this._left.Count;
      this._index[key] = id;
      this._left.Add(left);
      this._right.Add(right);
      this._count.Add(0);
      this._occurrences.Add([]);
      this._touchedIn.Add(-1);
      return id;
    }

    private void Touch(int id) {
      if (this._touchedIn[id] == this._round)
        return;
      this._touchedIn[id] = this._round;
      this._touched.Add(id);
    }

    private static void PushOccurrence(List<int> heap, int slot) {
      heap.Add(slot);
      var child = heap.Count - 1;
      while (child > 0) {
        var parent = (child - 1) / 2;
        if (heap[parent] <= heap[child])
          break;
        (heap[parent], heap[child]) = (heap[child], heap[parent]);
        child = parent;
      }
    }

    private static void DropOccurrenceTop(List<int> heap) {
      var last = heap.Count - 1;
      heap[0] = heap[last];
      heap.RemoveAt(last);
      var size = heap.Count;
      var parent = 0;
      for (; ; ) {
        var leftChild = parent * 2 + 1;
        var rightChild = leftChild + 1;
        var best = parent;
        if (leftChild < size && heap[leftChild] < heap[best])
          best = leftChild;
        if (rightChild < size && heap[rightChild] < heap[best])
          best = rightChild;
        if (best == parent)
          break;
        (heap[best], heap[parent]) = (heap[parent], heap[best]);
        parent = best;
      }
    }

    private bool QueueBefore(int a, int b) => this._queueCount[a] != this._queueCount[b]
      ? this._queueCount[a] > this._queueCount[b]
      : this._queueSlot[a] < this._queueSlot[b];

    private void QueueSwap(int a, int b) {
      (this._queueCount[a], this._queueCount[b]) = (this._queueCount[b], this._queueCount[a]);
      (this._queueSlot[a], this._queueSlot[b]) = (this._queueSlot[b], this._queueSlot[a]);
      (this._queuePair[a], this._queuePair[b]) = (this._queuePair[b], this._queuePair[a]);
    }

    private void QueuePush(int count, int slot, int id) {
      this._queueCount.Add(count);
      this._queueSlot.Add(slot);
      this._queuePair.Add(id);
      var child = this._queueCount.Count - 1;
      while (child > 0) {
        var parent = (child - 1) / 2;
        if (!this.QueueBefore(child, parent))
          break;
        this.QueueSwap(child, parent);
        child = parent;
      }
    }

    private void QueuePop() {
      var last = this._queueCount.Count - 1;
      this.QueueSwap(0, last);
      this._queueCount.RemoveAt(last);
      this._queueSlot.RemoveAt(last);
      this._queuePair.RemoveAt(last);

      var size = this._queueCount.Count;
      var parent = 0;
      for (; ; ) {
        var leftChild = parent * 2 + 1;
        var rightChild = leftChild + 1;
        var best = parent;
        if (leftChild < size && this.QueueBefore(leftChild, best))
          best = leftChild;
        if (rightChild < size && this.QueueBefore(rightChild, best))
          best = rightChild;
        if (best == parent)
          break;
        this.QueueSwap(best, parent);
        parent = best;
      }
    }
  }
}
