using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// The LZ77 stage of the libcsc encoder: copies input into the circular window and parses it with
/// either a lazy greedy parser (levels 1-2) or a price-driven optimal parser over up to 2 KiB at a
/// time (levels 3-5).
/// </summary>
internal sealed class CscLzEncoder {
  private const int OptimalLimit = 2048;

  private readonly CscModel _model;
  private readonly CscMatchFinder _matchFinder;
  private readonly byte[] _window;
  private readonly uint _windowSize;
  private readonly uint _goodLength;
  private readonly uint _treeCycles;
  private readonly uint _hashCycles;
  private readonly uint[] _repeatDistances = new uint[4];
  private readonly CscMatch[] _prices;
  private readonly OptimalNode[] _nodes = new OptimalNode[OptimalLimit + 1];
  private uint _windowPos;

  public CscLzEncoder(in CscEncoderSettings settings, CscModel model) {
    this._model = model;
    this._windowSize = Math.Clamp(settings.DictionarySize, CscConstants.MinDictionarySize, CscConstants.MaxDictionarySize);
    // Eight bytes of zero slack: the 6-byte hashes read past the last window position.
    this._window = new byte[this._windowSize + 8];
    this._matchFinder = new(this._window, this._windowSize, settings.BinaryTreeSize, settings.BinaryTreeHashBits, settings.HashWidth, settings.HashBits);
    this._goodLength = settings.GoodLength;
    this._treeCycles = settings.BinaryTreeCycles;
    this._hashCycles = (uint)settings.HashWidth;
    this._matchFinder.SetArguments(this._treeCycles, this._hashCycles, true, this._goodLength);
    this._prices = new CscMatch[this._goodLength + 1];
    Array.Fill(this._repeatDistances, this._windowSize);
  }

  /// <summary>
  /// Codes <paramref name="source"/> as one LZ block (terminated by the end-of-block match), or with
  /// <paramref name="lzMode"/> 5 only enters it into the window and match finder.
  /// </summary>
  public void EncodeNormal(ReadOnlySpan<byte> source, int lzMode) {
    for (var i = 0; i < source.Length;) {
      var blockSize = (int)Math.Min(Math.Min(this._windowSize - this._windowPos, (uint)(source.Length - i)), CscConstants.MinBlockSize);
      source.Slice(i, blockSize).CopyTo(this._window.AsSpan((int)this._windowPos));
      switch (lzMode) {
        case 1:
          this.CompressNormal((uint)blockSize, lazy: false);
          break;
        case 2:
          this.CompressNormal((uint)blockSize, lazy: true);
          break;
        case 3:
          this.CompressOptimal((uint)blockSize);
          break;
        case 5:
          this._matchFinder.SetArguments(1, 1, false, this._goodLength);
          this._matchFinder.SlidePositionFast(this._windowPos, (uint)blockSize);
          this._windowPos += (uint)blockSize;
          this._matchFinder.SetArguments(this._treeCycles, this._hashCycles, true, this._goodLength);
          break;
        default:
          throw new ArgumentOutOfRangeException(nameof(lzMode), lzMode, "Unknown CSC LZ mode.");
      }

      if (this._windowPos >= this._windowSize)
        this._windowPos = 0;
      i += blockSize;
    }

    if (lzMode != 5)
      this._model.EncodeEndOfBlock();
  }

  /// <summary>Whether a sampled position of the block already has a long match in the window.</summary>
  public bool IsDuplicateBlock(ReadOnlySpan<byte> buffer, int offset, int size) {
    for (var i = 0; i < size; ++i)
      if (this._matchFinder.TestFind(this._windowPos, buffer, offset + i, (uint)(size - i)))
        return true;
    return false;
  }

  private void EncodeNonLiteral(CscMatch match) {
    var reps = this._repeatDistances;
    if (match.Distance <= 4) {
      if (match is { Length: 1, Distance: 1 }) {
        this._model.EncodeRep0Len1();
        return;
      }

      this._model.EncodeRepeatMatch(match.Distance - 1, match.Length - 2);
      var index = (int)match.Distance - 1;
      var distance = reps[index];
      for (var j = index; j > 0; --j)
        reps[j] = reps[j - 1];
      reps[0] = distance;
      return;
    }

    this._model.EncodeMatch(match.Distance - 5, match.Length - 2);
    reps[3] = reps[2];
    reps[2] = reps[1];
    reps[1] = reps[0];
    reps[0] = match.Distance - 4;
  }

  private void CompressNormal(uint size, bool lazy) {
    var mf = this._matchFinder;
    var window = this._window;
    CscMatch current = default;
    var haveCurrent = false;
    for (uint i = 0; i < size;) {
      if (!haveCurrent)
        current = mf.FindMatch(this._repeatDistances, this._windowPos, size - i);

      if (current.Length == 1 || !lazy || current.Length >= this._goodLength) {
        if (current.Distance == 0)
          this._model.EncodeLiteral(window[this._windowPos]);
        else
          this.EncodeNonLiteral(current);
        mf.SlidePosition(this._windowPos, current.Length, size - i);
        i += current.Length;
        this._windowPos += current.Length;
        if (current.Distance != 0)
          this._model.SetLiteralContext(window[this._windowPos - 1]);
        haveCurrent = false;
        continue;
      }

      var next = mf.FindMatch(this._repeatDistances, this._windowPos + 1, size - i - 1);
      if (CscMatchFinder.IsSecondBetter(current, next)) {
        this._model.EncodeLiteral(window[this._windowPos]);
        ++i;
        ++this._windowPos;
        current = next;
        haveCurrent = true;
      } else {
        this.EncodeNonLiteral(current);
        mf.SlidePosition(this._windowPos + 1, current.Length - 1, size - i - 1);
        i += current.Length;
        this._windowPos += current.Length;
        this._model.SetLiteralContext(window[this._windowPos - 1]);
        haveCurrent = false;
      }
    }
  }

  private void CompressOptimal(uint size) {
    var mf = this._matchFinder;
    var model = this._model;
    var window = this._window;
    var prices = this._prices;
    var nodes = this._nodes;

    for (uint i = 0; i < size;) {
      mf.FindMatchWithPrice(model, model.State, prices, this._repeatDistances, this._windowPos, size - i);
      if (prices[0].Distance == 0) {
        model.EncodeLiteral(window[this._windowPos]);
        ++i;
        ++this._windowPos;
        continue;
      }

      uint current = 0, end = 1;
      nodes[0].Price = 0;
      nodes[0].BackPos = 0;
      this._repeatDistances.CopyTo(nodes[0].RepeatDistances);
      nodes[0].State = model.State;
      var limit = Math.Min(OptimalLimit, size - i);

      for (;;) {
        ref var node = ref nodes[current];
        node.Literal = window[this._windowPos];
        if (current != 0) {
          this.DeriveNodeState(current);
          if (current < limit)
            mf.FindMatchWithPrice(model, node.State, prices, node.RepeatDistances, this._windowPos, size - i - current);
        }

        if (current == limit) {
          this.Backward(current);
          i += current;
          break;
        }

        if (prices[0].Length == 1 && current + 1 == end) {
          this.Backward(current);
          model.EncodeLiteral(node.Literal);
          i += current + 1;
          ++this._windowPos;
          break;
        }

        if (current + 1 >= end)
          nodes[end++].Price = uint.MaxValue;

        if (prices[0].Length >= this._goodLength || (prices[0].Length > 1 && prices[0].Length + current >= limit)) {
          this.Backward(current);
          i += current;
          this.EncodeNonLiteral(prices[0]);
          mf.SlidePosition(this._windowPos, prices[0].Length, size - i);
          i += prices[0].Length;
          this._windowPos += prices[0].Length;
          model.SetLiteralContext(window[this._windowPos - 1]);
          break;
        }

        var literalContext = this._windowPos != 0 ? window[this._windowPos - 1] : 0u;
        var literalPrice = model.GetLiteralPrice(node.State, literalContext, window[this._windowPos]);
        Relax(nodes, current, current + 1, literalPrice, 0);
        if (prices[1].Distance != 0)
          Relax(nodes, current, current + 1, prices[1].Price, 1);

        var length = prices[0].Length;
        while (current + length >= end)
          nodes[end++].Price = uint.MaxValue;

        for (; length > 1; --length)
          if (prices[length].Distance != 0)
            Relax(nodes, current, current + length, prices[length].Price, prices[length].Distance);

        ++current;
        ++this._windowPos;
      }
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void Relax(OptimalNode[] nodes, uint from, uint to, uint price, uint distance) {
    var total = price + nodes[from].Price;
    ref var target = ref nodes[to];
    if (total >= target.Price)
      return;

    target.Distance = distance;
    target.BackPos = (int)from;
    target.Price = total;
  }

  /// <summary>Replays the packet that reaches <paramref name="index"/> to get its state and repeat distances.</summary>
  private void DeriveNodeState(uint index) {
    var nodes = this._nodes;
    ref var node = ref nodes[index];
    ref var from = ref nodes[node.BackPos];
    node.RepeatDistances = from.RepeatDistances;
    if (node.Distance == 0) {
      node.State = (from.State * 4) & 0x3F;
      return;
    }

    if (node.Distance <= 4) {
      var length = index - (uint)node.BackPos;
      if (length == 1 && node.Distance == 1) {
        node.State = (from.State * 4 + 2) & 0x3F;
        return;
      }

      node.State = (from.State * 4 + 3) & 0x3F;
      var slot = (int)node.Distance - 1;
      var distance = node.RepeatDistances[slot];
      for (var j = slot; j > 0; --j)
        node.RepeatDistances[j] = node.RepeatDistances[j - 1];
      node.RepeatDistances[0] = distance;
      return;
    }

    node.State = (from.State * 4 + 1) & 0x3F;
    node.RepeatDistances[3] = from.RepeatDistances[2];
    node.RepeatDistances[2] = from.RepeatDistances[1];
    node.RepeatDistances[1] = from.RepeatDistances[0];
    node.RepeatDistances[0] = node.Distance - 4;
  }

  /// <summary>Emits the cheapest path from node 0 to <paramref name="end"/>.</summary>
  private void Backward(uint end) {
    var nodes = this._nodes;
    var model = this._model;
    for (var i = (int)end; i != 0;) {
      nodes[nodes[i].BackPos].NextPos = i;
      i = nodes[i].BackPos;
    }

    for (var i = 0; i != end;) {
      var next = nodes[i].NextPos;
      var distance = nodes[next].Distance;
      if (distance == 0)
        model.EncodeLiteral(nodes[i].Literal);
      else {
        if (distance > 4)
          model.EncodeMatch(distance - 5, (uint)(next - i - 2));
        else if (next - i == 1 && distance == 1)
          model.EncodeRep0Len1();
        else
          model.EncodeRepeatMatch(distance - 1, (uint)(next - i - 2));
        model.SetLiteralContext(nodes[next - 1].Literal);
      }

      i = next;
    }

    ((ReadOnlySpan<uint>)nodes[end].RepeatDistances).CopyTo(this._repeatDistances);
  }

  /// <summary>One position of the optimal parser's lattice.</summary>
  private struct OptimalNode {
    public uint Distance;
    public uint State;
    public int BackPos;
    public int NextPos;
    public uint Price;
    public uint Literal;
    public RepeatDistanceSet RepeatDistances;
  }

  /// <summary>The four most recent distances, stored inline.</summary>
  [InlineArray(4)]
  private struct RepeatDistanceSet {
    private uint _element0;
  }
}
