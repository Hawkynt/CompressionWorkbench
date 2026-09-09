using System.Numerics;

namespace Compression.Core.Dictionary.Aplib;

/// <summary>
/// Compresses standard bare aPLib streams, either with the fast greedy encoder or
/// with a bounded cost-based parser that searches the complete aPLib token grammar.
/// </summary>
/// <remarks>
/// <para>
/// The optimizer is an independent implementation derived from the documented
/// aPLib token grammar and its bit costs. It keeps several parser states because
/// aPLib's normal-match cost depends on whether the previous token was a match and
/// because a literal can enable reuse of the previous match offset on the next token.
/// </para>
/// <para>
/// Candidate discovery is deliberately bounded so <see cref="CompressOptimal"/>
/// remains usable on large inputs. The optimized stream is compared with the fast
/// greedy stream and the smaller one is returned, so the optimal path can never
/// regress compressed size relative to <see cref="Compress"/>.
/// </para>
/// </remarks>
public static class AplibCompressor {
  private const int BeamWidth = 8;
  private const int MaxBucketStates = BeamWidth * 12;
  private const int PrunedBucketStates = BeamWidth * 6;
  private const int MaxChain = 256;
  private const int CandidateLimit = 12;
  private const int ShortCandidateLimit = 12;
  private const int MaxMatchLength = ushort.MaxValue;

  /// <summary>Compresses a bare standard aPLib stream using the fast greedy encoder.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> data) => AplibBuildingBlock.CompressBare(data);

  /// <summary>
  /// Compresses a bare standard aPLib stream using a bounded cost-based parser and
  /// returns whichever of the optimized and greedy streams is smaller.
  /// </summary>
  public static byte[] CompressOptimal(ReadOnlySpan<byte> data) {
    var baseline = AplibBuildingBlock.CompressBare(data);
    if (data.Length < 2)
      return baseline;

    var optimized = new Parser(data).Compress();
    return optimized.Length < baseline.Length ? optimized : baseline;
  }

  private enum TokenKind : byte {
    Literal,
    Single,
    Short,
    Reuse,
    Normal,
  }

  private readonly record struct Token(TokenKind Kind, int Length, int Offset = 0, byte Literal = 0);
  private readonly record struct StateKey(bool LastWasMatch, int RepeatOffset, byte TagModulo);
  private readonly record struct MatchCandidate(int Offset, int Length);

  private readonly record struct Node(
    int Position,
    long CostBits,
    byte TagModulo,
    int RepeatOffset,
    bool LastWasMatch,
    int Parent,
    Token Token
  );

  private sealed class Parser {
    private readonly byte[] _input;
    private readonly int[] _previous;
    private readonly Dictionary<StateKey, int>?[] _states;
    private readonly List<Node> _nodes = [];

    public Parser(ReadOnlySpan<byte> input) {
      this._input = input.ToArray();
      this._previous = BuildPrevious(this._input);
      this._states = new Dictionary<StateKey, int>?[this._input.Length + 1];

      var initial = new Node(1, 0, 0, 0, false, -1, default);
      this._nodes.Add(initial);
      this._states[1] = new() { [new(false, 0, 0)] = 0 };
    }

    public byte[] Compress() {
      for (var position = 1; position < this._input.Length; ++position) {
        var bucket = this._states[position];
        if (bucket is null || bucket.Count == 0)
          continue;

        var active = BestNodes(bucket, BeamWidth);
        var normalCandidates = this.FindNormalCandidates(position);
        var shortCandidates = this.FindShortCandidates(position);
        var singleOffset = this.FindSingleOffset(position);

        foreach (var nodeIndex in active) {
          var node = this._nodes[nodeIndex];

          this.Add(nodeIndex, new(TokenKind.Literal, 1, Literal: this._input[position]), tagBits: 1, dataBytes: 1);

          if (singleOffset >= 0)
            this.Add(nodeIndex, new(TokenKind.Single, 1, singleOffset), tagBits: 7, dataBytes: 0);

          foreach (var candidate in shortCandidates)
            this.Add(nodeIndex, new(TokenKind.Short, candidate.Length, candidate.Offset), tagBits: 3, dataBytes: 1);

          if (!node.LastWasMatch && node.RepeatOffset > 0 && node.RepeatOffset <= position) {
            var repeatLength = this.MatchLength(position, position - node.RepeatOffset);
            if (repeatLength >= 2) {
              foreach (var length in LengthOptions(repeatLength, minimum: 2, adjustment: 0))
                this.Add(nodeIndex, new(TokenKind.Reuse, length, node.RepeatOffset), tagBits: 4 + GammaBits(length), dataBytes: 0);
            }
          }

          foreach (var candidate in normalCandidates) {
            var adjustment = LengthAdjustment(candidate.Offset);
            var minimum = adjustment + 2;
            foreach (var length in LengthOptions(candidate.Length, minimum, adjustment)) {
              var offsetCode = (candidate.Offset >> 8) + (node.LastWasMatch ? 2 : 3);
              var encodedLength = length - adjustment;
              var tagBits = 2 + GammaBits(offsetCode) + GammaBits(encodedLength);
              this.Add(nodeIndex, new(TokenKind.Normal, length, candidate.Offset), tagBits, dataBytes: 1);
            }
          }
        }
      }

      var finalBucket = this._states[this._input.Length]
        ?? throw new InvalidOperationException("aPLib optimizer failed to reach the end of the input.");
      var best = finalBucket.Values
        .OrderBy(this.FinalSize)
        .ThenBy(index => this._nodes[index].CostBits)
        .First();
      return this.Emit(best);
    }

    private void Add(int parentIndex, Token token, int tagBits, int dataBytes) {
      var parent = this._nodes[parentIndex];
      var position = parent.Position + token.Length;
      if ((uint)position > (uint)this._input.Length)
        return;

      var repeatOffset = parent.RepeatOffset;
      var lastWasMatch = false;
      switch (token.Kind) {
        case TokenKind.Short:
        case TokenKind.Normal:
          repeatOffset = token.Offset;
          lastWasMatch = true;
          break;
        case TokenKind.Reuse:
          lastWasMatch = true;
          break;
      }

      var costBits = parent.CostBits + tagBits + dataBytes * 8L;
      var tagModulo = (byte)((parent.TagModulo + tagBits) & 7);
      var key = new StateKey(lastWasMatch, repeatOffset, tagModulo);
      var bucket = this._states[position] ??= [];

      if (bucket.TryGetValue(key, out var existingIndex) && this._nodes[existingIndex].CostBits <= costBits)
        return;

      var node = new Node(position, costBits, tagModulo, repeatOffset, lastWasMatch, parentIndex, token);
      var nodeIndex = this._nodes.Count;
      this._nodes.Add(node);
      bucket[key] = nodeIndex;

      if (bucket.Count > MaxBucketStates)
        this.Prune(bucket, PrunedBucketStates);
    }

    private MatchCandidate[] FindNormalCandidates(int position) {
      if (position + 1 >= this._input.Length)
        return [];

      var candidates = new List<MatchCandidate>(MaxChain);
      var candidate = this._previous[position];
      var chain = 0;
      var maximum = Math.Min(this._input.Length - position, MaxMatchLength);

      while (candidate >= 0 && chain++ < MaxChain) {
        var offset = position - candidate;
        var length = this.MatchLength(position, candidate, maximum);
        if (length >= MinimumNormalLength(offset))
          candidates.Add(new(offset, length));
        if (length == maximum)
          break;
        candidate = this._previous[candidate];
      }

      if (candidates.Count <= CandidateLimit)
        return [.. candidates];

      var result = new List<MatchCandidate>(CandidateLimit);
      var seenOffsets = new HashSet<int>();

      foreach (var match in candidates.Take(CandidateLimit / 2))
        if (seenOffsets.Add(match.Offset))
          result.Add(match);

      foreach (var match in candidates.OrderByDescending(static c => c.Length).ThenBy(static c => c.Offset)) {
        if (seenOffsets.Add(match.Offset))
          result.Add(match);
        if (result.Count == CandidateLimit)
          break;
      }

      return [.. result];
    }

    private MatchCandidate[] FindShortCandidates(int position) {
      if (position + 1 >= this._input.Length)
        return [];

      var result = new List<MatchCandidate>();
      var maximumOffset = Math.Min(127, position);
      for (var offset = 1; offset <= maximumOffset; ++offset) {
        if (this._input[position] != this._input[position - offset]
            || this._input[position + 1] != this._input[position + 1 - offset])
          continue;

        var length = position + 2 < this._input.Length
          && this._input[position + 2] == this._input[position + 2 - offset]
          ? 3
          : 2;
        result.Add(new(offset, length));
        if (length == 3)
          result.Add(new(offset, 2));
      }

      return [.. result
        .OrderByDescending(static c => c.Length)
        .ThenBy(static c => c.Offset)
        .Take(ShortCandidateLimit)];
    }

    private int FindSingleOffset(int position) {
      if (this._input[position] == 0)
        return 0;

      var maximumOffset = Math.Min(15, position);
      for (var offset = 1; offset <= maximumOffset; ++offset)
        if (this._input[position] == this._input[position - offset])
          return offset;
      return -1;
    }

    private int MatchLength(int position, int candidate, int maximum = MaxMatchLength) {
      maximum = Math.Min(maximum, this._input.Length - position);
      var length = 0;
      while (length < maximum && this._input[position + length] == this._input[candidate + length])
        ++length;
      return length;
    }

    private byte[] Emit(int nodeIndex) {
      var tokens = new Stack<Token>();
      while (this._nodes[nodeIndex].Parent >= 0) {
        tokens.Push(this._nodes[nodeIndex].Token);
        nodeIndex = this._nodes[nodeIndex].Parent;
      }

      var writer = new Writer();
      writer.PutByte(this._input[0]);
      var lastWasMatch = false;
      var repeatOffset = 0;

      while (tokens.TryPop(out var token)) {
        switch (token.Kind) {
          case TokenKind.Literal:
            writer.PutBit(0);
            writer.PutByte(token.Literal);
            lastWasMatch = false;
            break;

          case TokenKind.Single:
            writer.PutBit(1);
            writer.PutBit(1);
            writer.PutBit(1);
            for (var bit = 3; bit >= 0; --bit)
              writer.PutBit((token.Offset >> bit) & 1);
            lastWasMatch = false;
            break;

          case TokenKind.Short:
            writer.PutBit(1);
            writer.PutBit(1);
            writer.PutBit(0);
            writer.PutByte((byte)((token.Offset << 1) | (token.Length - 2)));
            repeatOffset = token.Offset;
            lastWasMatch = true;
            break;

          case TokenKind.Reuse:
            writer.PutBit(1);
            writer.PutBit(0);
            writer.PutGamma(2);
            writer.PutGamma((uint)token.Length);
            lastWasMatch = true;
            break;

          case TokenKind.Normal:
            writer.PutBit(1);
            writer.PutBit(0);
            writer.PutGamma((uint)((token.Offset >> 8) + (lastWasMatch ? 2 : 3)));
            writer.PutByte((byte)token.Offset);
            writer.PutGamma((uint)(token.Length - LengthAdjustment(token.Offset)));
            repeatOffset = token.Offset;
            lastWasMatch = true;
            break;
        }
      }

      writer.PutBit(1);
      writer.PutBit(1);
      writer.PutBit(0);
      writer.PutByte(0);
      _ = repeatOffset;
      return writer.ToArray();
    }

    private long FinalSize(int nodeIndex) {
      var node = this._nodes[nodeIndex];
      var costBits = node.CostBits + 3 + 8;
      var tagModulo = (byte)((node.TagModulo + 3) & 7);
      return 1 + (costBits - tagModulo) / 8 + (tagModulo == 0 ? 0 : 1);
    }

    private void Prune(Dictionary<StateKey, int> bucket, int keep) {
      if (bucket.Count <= keep)
        return;

      var best = BestNodes(bucket, keep);
      bucket.Clear();
      foreach (var nodeIndex in best) {
        var node = this._nodes[nodeIndex];
        bucket[new(node.LastWasMatch, node.RepeatOffset, node.TagModulo)] = nodeIndex;
      }
    }

    private int[] BestNodes(Dictionary<StateKey, int> bucket, int keep) =>
      [.. bucket.Values
        .OrderBy(this.CurrentSize)
        .ThenBy(index => this._nodes[index].CostBits)
        .Take(keep)];

    private long CurrentSize(int nodeIndex) {
      var node = this._nodes[nodeIndex];
      return 1 + (node.CostBits - node.TagModulo) / 8 + (node.TagModulo == 0 ? 0 : 1);
    }

    private static int[] BuildPrevious(byte[] input) {
      var previous = new int[input.Length];
      Array.Fill(previous, -1);
      if (input.Length < 2)
        return previous;

      var head = new int[1 << 16];
      Array.Fill(head, -1);
      for (var position = 0; position + 1 < input.Length; ++position) {
        var hash = (input[position] << 8) | input[position + 1];
        previous[position] = head[hash];
        head[hash] = position;
      }
      return previous;
    }
  }

  private static int[] LengthOptions(int maximum, int minimum, int adjustment) {
    if (maximum < minimum)
      return [];
    if (maximum - minimum <= 12)
      return [.. Enumerable.Range(minimum, maximum - minimum + 1)];

    var values = new HashSet<int> { minimum, minimum + 1, minimum + 2, maximum, maximum - 1, maximum - 2 };
    for (var power = 2; power > 0 && power <= MaxMatchLength; power <<= 1) {
      var plateauEnd = ((power << 1) - 1) + adjustment;
      if (plateauEnd >= minimum && plateauEnd <= maximum)
        values.Add(plateauEnd);
      if (plateauEnd + 1 >= minimum && plateauEnd + 1 <= maximum)
        values.Add(plateauEnd + 1);
      if (power > MaxMatchLength / 2)
        break;
    }
    return [.. values.Order()];
  }

  private static int LengthAdjustment(int offset) =>
    (offset < 128 ? 2 : 0) + (offset >= 1280 ? 1 : 0) + (offset >= 32000 ? 1 : 0);

  private static int MinimumNormalLength(int offset) => LengthAdjustment(offset) + 2;

  private static int GammaBits(int value) {
    if (value < 2)
      throw new ArgumentOutOfRangeException(nameof(value));
    return BitOperations.Log2((uint)value) * 2;
  }

  private sealed class Writer {
    private readonly List<byte> _output = [];
    private int _tagPosition = -1;
    private int _bitsInTag;

    public void PutBit(int bit) {
      if (this._bitsInTag == 0) {
        this._tagPosition = this._output.Count;
        this._output.Add(0);
      }
      if (bit != 0)
        this._output[this._tagPosition] |= (byte)(1 << (7 - this._bitsInTag));
      this._bitsInTag = (this._bitsInTag + 1) & 7;
    }

    public void PutByte(byte value) => this._output.Add(value);

    public void PutGamma(uint value) {
      if (value < 2)
        throw new ArgumentOutOfRangeException(nameof(value));
      var mostSignificantBit = 31 - BitOperations.LeadingZeroCount(value);
      for (var bit = mostSignificantBit - 1; bit >= 0; --bit) {
        this.PutBit((int)((value >> bit) & 1));
        this.PutBit(bit > 0 ? 1 : 0);
      }
    }

    public byte[] ToArray() => [.. this._output];
  }
}
