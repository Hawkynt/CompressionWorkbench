namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.6 LZP decoder (stream methods 8..23, the <c>-mz</c> mode and every compressed
/// directory table), implemented from docs/UHARC-ON-DISK.md section "LZP".
/// </summary>
/// <remarks>
/// The coder works on the bitwise complement of the data: history, contexts and match
/// predictions all see <c>~output</c>. A position is either predicted from a hashed order-4
/// context, the last match distance or the last occurrence of the current byte pair (one
/// binary decision, then a ranked length up to 254, or 255 meaning "run continues"), or it is
/// a literal coded by an order-1 frequency model with an order-0 escape.
/// </remarks>
internal sealed class UharcLzpDecoder {

  private const uint NoPosition = uint.MaxValue;
  private const int HashBits = 18;
  private const int RescaleLimit = 16384;
  private const int OrderZeroContext = 318;
  private const int ContextCount = 319;
  private const int RunLength = 255;

  // Initial split of the 50 match/no-match decisions; every total starts at 99.
  private static readonly byte[] DecisionSeed = [
    11, 12, 13, 14, 14, 14, 14, 14, 11, 12, 13, 14, 14, 14, 14, 14,
    11, 12, 13, 14, 14, 14, 14, 14, 6, 7, 8, 9, 9, 9, 9, 9,
    6, 7, 8, 9, 9, 9, 9, 9, 6, 7, 8, 9, 9, 9, 9, 9,
    49, 79,
  ];

  // Two-bit history of recent decisions -> bias added to the decision index.
  private static readonly byte[] DecisionHistoryNext = [6, 0, 6, 0, 6, 1, 6, 2, 6, 3, 6, 3, 7, 4, 7, 5];

  // Search tree over copy lengths: [node * 2 + (answer above node ? 1 : 0)] -> next node, 0 ends.
  private static readonly byte[] LengthLadder = [
    0, 0, 0, 0, 0, 4, 0, 0, 3, 5, 0, 0, 2, 14, 0, 0, 7, 9, 0, 0, 8, 12, 0, 0, 11, 13, 0, 0, 10, 30, 0, 0,
    15, 17, 0, 0, 16, 20, 0, 0, 19, 21, 0, 0, 18, 26, 0, 0, 23, 25, 0, 0, 24, 28, 0, 0, 27, 29, 0, 0, 22, 62, 0, 0,
    31, 33, 0, 0, 32, 36, 0, 0, 35, 37, 0, 0, 34, 42, 0, 0, 39, 41, 0, 0, 40, 44, 0, 0, 43, 45, 0, 0, 38, 54, 0, 0,
    47, 49, 0, 0, 48, 52, 0, 0, 51, 53, 0, 0, 50, 58, 0, 0, 55, 57, 0, 0, 56, 60, 0, 0, 59, 61, 0, 0, 46, 254, 0, 0,
    63, 65, 0, 0, 64, 68, 0, 0, 67, 69, 0, 0, 66, 74, 0, 0, 71, 73, 0, 0, 72, 76, 0, 0, 75, 77, 0, 0, 70, 86, 0, 0,
    79, 81, 0, 0, 80, 84, 0, 0, 83, 85, 0, 0, 82, 90, 0, 0, 87, 89, 0, 0, 88, 92, 0, 0, 91, 93, 0, 0, 78, 110, 0, 0,
    95, 97, 0, 0, 96, 100, 0, 0, 99, 101, 0, 0, 98, 106, 0, 0, 103, 105, 0, 0, 104, 108, 0, 0, 107, 109, 0, 0, 102, 118, 0, 0,
    111, 113, 0, 0, 112, 116, 0, 0, 115, 117, 0, 0, 114, 122, 0, 0, 119, 121, 0, 0, 120, 124, 0, 0, 123, 125, 0, 0, 94, 190, 0, 0,
    127, 129, 0, 0, 128, 132, 0, 0, 131, 133, 0, 0, 130, 138, 0, 0, 135, 137, 0, 0, 136, 140, 0, 0, 139, 141, 0, 0, 134, 150, 0, 0,
    143, 145, 0, 0, 144, 148, 0, 0, 147, 149, 0, 0, 146, 154, 0, 0, 151, 153, 0, 0, 152, 156, 0, 0, 155, 157, 0, 0, 142, 174, 0, 0,
    159, 161, 0, 0, 160, 164, 0, 0, 163, 165, 0, 0, 162, 170, 0, 0, 167, 169, 0, 0, 168, 172, 0, 0, 171, 173, 0, 0, 166, 182, 0, 0,
    175, 177, 0, 0, 176, 180, 0, 0, 179, 181, 0, 0, 178, 186, 0, 0, 183, 185, 0, 0, 184, 188, 0, 0, 187, 189, 0, 0, 158, 222, 0, 0,
    191, 193, 0, 0, 192, 196, 0, 0, 195, 197, 0, 0, 194, 202, 0, 0, 199, 201, 0, 0, 200, 204, 0, 0, 203, 205, 0, 0, 198, 214, 0, 0,
    207, 209, 0, 0, 208, 212, 0, 0, 211, 213, 0, 0, 210, 218, 0, 0, 215, 217, 0, 0, 216, 220, 0, 0, 219, 221, 0, 0, 206, 238, 0, 0,
    223, 225, 0, 0, 224, 228, 0, 0, 227, 229, 0, 0, 226, 234, 0, 0, 231, 233, 0, 0, 232, 236, 0, 0, 235, 237, 0, 0, 230, 246, 0, 0,
    239, 241, 0, 0, 240, 244, 0, 0, 243, 245, 0, 0, 242, 250, 0, 0, 247, 249, 0, 0, 248, 252, 0, 0, 251, 253, 0, 0, 126, 0, 0, 0,
  ];

  // Byte classes for the literal context: 0 marks the complement of an ASCII letter.
  private static readonly bool[] IsNonLetter = BuildLetterClasses();

  private readonly byte[] _window;
  private readonly uint _mask;
  private readonly UharcRangeDecoder _coder;

  private readonly uint[] _hashPosition = new uint[1 << HashBits];
  private readonly uint[] _hashContext = new uint[1 << HashBits];
  private readonly byte[] _hashLength = new byte[1 << HashBits];
  private readonly byte[] _pairLength = new byte[65536];
  private readonly uint[] _pairPosition = new uint[65536];

  private readonly ushort[] _decisionSplit = new ushort[50];
  private readonly ushort[] _decisionTotal = new ushort[50];
  private readonly ushort[,] _lengthProbability = new ushort[10, 512];

  private readonly ushort[,] _frequency = new ushort[ContextCount, 256];
  private readonly int[] _contextTotal = new int[ContextCount];
  private readonly int[] _escapeWeight = new int[ContextCount];

  private uint _writePosition;
  private uint _history;
  private uint _lastDistance = NoPosition;
  private uint _runCount;
  private uint _predictedPosition = NoPosition;
  private uint _excludedPosition = NoPosition;
  private int _decisionHistory;

  private UharcLzpDecoder(byte[] packed, int offset, int length, int method) {
    var size = 1u << (method - UharcMethods.LzpFirst + 10);
    this._window = new byte[size];
    this._mask = size - 1;
    this._coder = new UharcRangeDecoder(packed, offset, length);

    Array.Fill(this._hashPosition, NoPosition);
    Array.Fill(this._hashContext, NoPosition);
    Array.Fill(this._pairPosition, NoPosition);

    for (var i = 0; i < 50; ++i) {
      this._decisionSplit[i] = DecisionSeed[i];
      this._decisionTotal[i] = 99;
    }

    var initial = 2560;
    for (var group = 0; group < 10; ++group) {
      for (var node = 0; node < 512; ++node)
        this._lengthProbability[group, node] = (ushort)initial;
      if (initial > 64)
        initial -= 64;
    }

    this.ResetLiteralModel();
  }

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes of an LZP stream.</summary>
  /// <param name="packed">Buffer holding the packed stream.</param>
  /// <param name="offset">Start of the packed stream.</param>
  /// <param name="length">Length of the packed stream.</param>
  /// <param name="method">Stream method byte, 8..23 (the dictionary is <c>2^(method+2)</c>).</param>
  /// <param name="unpackedSize">Number of bytes to produce.</param>
  /// <param name="fileEnds">Cumulative end offsets of the archived files (empty for a table).</param>
  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.LzpFirst or > UharcMethods.LzpLast)
      throw new ArgumentOutOfRangeException(nameof(method));

    var output = new byte[unpackedSize];
    if (unpackedSize == 0)
      return output;

    var decoder = new UharcLzpDecoder(packed, offset, length, method);
    var resets = new UharcModelResets(fileEnds, unpackedSize, UharcModelResets.LzpSpan, mergeSmallFiles: true);
    var position = 0;
    while (position < unpackedSize) {
      if (resets.IsResetPoint(position))
        decoder.ResetAtFileBoundary();

      var copied = decoder.TryPrediction(output, position);
      position = copied > 0 ? position + copied : decoder.WriteByte(output, position, decoder.DecodeLiteral());
    }

    return output;
  }

  private void ResetAtFileBoundary() {
    this.ResetLiteralModel();
    for (var i = 0; i < 50; ++i) {
      this._decisionSplit[i] = (ushort)((this._decisionSplit[i] + 7) >> 3);
      this._decisionTotal[i] = (ushort)((this._decisionTotal[i] + 15) >> 3);
    }
  }

  private void ResetLiteralModel() {
    Array.Clear(this._frequency);
    for (var symbol = 0; symbol < 256; ++symbol)
      this._frequency[OrderZeroContext, symbol] = 8;
    Array.Clear(this._contextTotal);
    this._contextTotal[OrderZeroContext] = 256 * 8;
    Array.Fill(this._escapeWeight, 1);
    this._escapeWeight[OrderZeroContext] = 0;
  }

  private int WriteByte(byte[] output, int position, byte value) {
    output[position] = (byte)~value;
    this._window[this._writePosition] = value;
    this._history = (this._history << 8) | value;
    this._writePosition = (this._writePosition + 1) & this._mask;
    return position + 1;
  }

  // ── Prediction ─────────────────────────────────────────────────────────

  /// <summary>Attempts a predicted copy at <paramref name="position"/>; returns the bytes copied, 0 for "decode a literal".</summary>
  private int TryPrediction(byte[] output, int position) {
    var hash = (int)((this._history ^ (this._history >> 13)) & ((1u << HashBits) - 1));
    var pair = (int)(this._history & 0xFFFF);
    var hashedPosition = this._hashPosition[hash];
    var hashedContext = this._hashContext[hash];
    var hashLength = this._hashLength[hash];
    var pairPosition = this._pairPosition[pair];
    this._predictedPosition = NoPosition;

    var candidate = NoPosition;
    var decision = 0;
    var fromHash = false;
    if (this._runCount != 0) {
      candidate = (this._writePosition - this._lastDistance) & this._mask;
      decision = this._runCount > 2 ? 49 : 48;
    } else if (hashedPosition != NoPosition && hashedContext == this._history) {
      candidate = hashedPosition & this._mask;
      fromHash = true;
      decision = 1;
    } else {
      candidate = (this._writePosition - this._lastDistance) & this._mask;
      if (((candidate - this._writePosition) & this._mask) >= 0x104 && this.ContextAgrees(candidate))
        decision = 9;

      if (decision == 0 && pairPosition != NoPosition) {
        candidate = pairPosition & this._mask;
        decision = 17;
      }
    }

    this._hashPosition[hash] = this._writePosition;
    this._hashContext[hash] = this._history;
    this._pairPosition[pair] = this._writePosition;

    // No candidate, or one so old that the window is about to overwrite it.
    if (decision == 0 || ((candidate - this._writePosition) & this._mask) < 256) {
      this._runCount = 0;
      return 0;
    }

    if (this._runCount == 0)
      decision += (hashLength == 0 ? 23 : -1) + this._decisionHistory;
    decision = Math.Clamp(decision, 0, 49);

    // The byte the previous copy stopped in front of is known not to follow: skip the decision.
    if (this._excludedPosition != NoPosition && this._window[this._excludedPosition] == this._window[candidate]) {
      this.CountImplicitMiss(decision);
      this._decisionHistory = DecisionHistoryNext[(this._decisionHistory * 2 + 1) & 15];
      this._runCount = 0;
      return 0;
    }

    this._predictedPosition = candidate;
    var miss = this.DecodeDecision(decision);
    this._decisionHistory = DecisionHistoryNext[(this._decisionHistory * 2 + miss) & 15];
    var candidatePair = this.PairAt(candidate);
    if (miss != 0) {
      this._hashLength[hash] = 0;
      this._pairLength[candidatePair] = 0;
      this._runCount = 0;
      return 0;
    }

    var pairLength = this._pairLength[candidatePair];
    var group = this._runCount != 0 ? (this._runCount > 2 ? 9 : 8) : LengthGroup(fromHash ? hashLength : pairLength);
    var length = this.DecodeLength(group, pairLength);
    this._hashLength[hash] = (byte)length;
    this._pairLength[candidatePair] = (byte)length;
    this._lastDistance = (this._writePosition - candidate) & this._mask;

    var remaining = output.Length - position;
    int count;
    if (length == RunLength) {
      count = Math.Min(remaining, RunLength);
      this._excludedPosition = NoPosition;
      ++this._runCount;
    } else {
      count = Math.Min(remaining, length);
      this._excludedPosition = (candidate + (uint)count) & this._mask;
      this._runCount = 0;
    }

    var source = candidate;
    for (var i = 0; i < count; ++i) {
      position = this.WriteByte(output, position, this._window[source]);
      source = (source + 1) & this._mask;
    }

    return count;
  }

  private static int LengthGroup(int seen) => seen switch {
    0 or 1 => 0,
    < 7 => seen - 1,
    < 12 => seen < 8 ? 5 : 6,
    _ => 7,
  };

  private bool ContextAgrees(uint candidate) {
    var before = (uint)this._window[(candidate - 4) & this._mask] << 24
      | (uint)this._window[(candidate - 3) & this._mask] << 16
      | (uint)this._window[(candidate - 2) & this._mask] << 8
      | this._window[(candidate - 1) & this._mask];
    var now = this._history;
    return (before & 0x0000FFFF) == (now & 0x0000FFFF)
      || (before & 0xFFFF00FF) == (now & 0xFFFF00FF)
      || (before & 0xFFFFFF00) == (now & 0xFFFFFF00);
  }

  private int PairAt(uint candidate) {
    var next = (candidate + 1) & this._mask;
    var low = next == this._writePosition ? 0 : this._window[next];
    return this._window[candidate] << 8 | low;
  }

  private int DecodeDecision(int index) {
    var split = (uint)this._decisionSplit[index];
    var total = (uint)this._decisionTotal[index];
    int bit;
    if (this._coder.GetCount(total) < split) {
      this._coder.Consume(0, split);
      split += 64;
      bit = 0;
    } else {
      this._coder.Consume(split, total - split);
      bit = 1;
    }

    this.StoreDecision(index, split, total + 64);
    return bit;
  }

  private void CountImplicitMiss(int index)
    => this.StoreDecision(index, this._decisionSplit[index], this._decisionTotal[index] + 64u);

  private void StoreDecision(int index, uint split, uint total) {
    if (total >= RescaleLimit) {
      split = Math.Max(1, (split + 1) >> 1);
      total = Math.Max(split + 1, (total + 2) >> 1);
    }

    this._decisionSplit[index] = (ushort)split;
    this._decisionTotal[index] = (ushort)total;
  }

  /// <summary>
  /// Decodes a copy length 1..254 or 255 by a binary search over the length ladder:
  /// a zero bit at node <c>n</c> means "the length is at most n".
  /// </summary>
  private int DecodeLength(int group, int previous) {
    var node = 6;
    var result = RunLength;
    while (node != 0) {
      var branch = previous <= node ? 256 : 0;
      if (this._coder.DecodeBit(ref this._lengthProbability[group, node + branch], 6) == 0)
        result = node;
      node = LengthLadder[node * 2 + (result > node ? 1 : 0)];
    }

    return result;
  }

  // ── Literals ───────────────────────────────────────────────────────────

  private byte DecodeLiteral() {
    var context = this.LiteralContext();
    var excluded = this._excludedPosition == NoPosition ? -1 : this._window[this._excludedPosition];
    var symbol = this.DecodeSymbol(context, excluded);
    this.UpdateLiteral(context, symbol);

    this._excludedPosition = this._predictedPosition != NoPosition && symbol == this._window[this._predictedPosition]
      ? (this._predictedPosition + 1) & this._mask
      : NoPosition;
    return (byte)symbol;
  }

  private int LiteralContext() {
    var last = (int)(this._history & 0xFF);
    var before = (int)((this._history >> 8) & 0xFF);
    switch (this._history & 0xFFFF) {
      case 0xFFFF: return 314;
      case 0xD1DF: return 315;
      case 0xD3DF: return 316;
    }

    if (last == 0xDF && IsNonLetter[before])
      return 317;

    return !IsNonLetter[last] && IsNonLetter[before] ? last + 123 : last;
  }

  private int DecodeSymbol(int context, int excluded) {
    var seen = this._contextTotal[context] - (excluded >= 0 ? this._frequency[context, excluded] : 0);
    if (seen > 0) {
      var withEscape = (uint)(seen + this._escapeWeight[context]);
      var count = this._coder.GetCount(withEscape);
      if (count < seen) {
        var low = 0u;
        for (var symbol = 255; symbol >= 0; --symbol) {
          if (symbol == excluded)
            continue;

          var f = (uint)this._frequency[context, symbol];
          if (count < low + f) {
            this._coder.Consume(low, f);
            return symbol;
          }

          low += f;
        }

        throw new InvalidDataException("UHARC: LZP literal outside its context.");
      }

      this._coder.Consume((uint)seen, (uint)this._escapeWeight[context]);
    }

    var total = 0u;
    for (var symbol = 0; symbol < 256; ++symbol)
      if (symbol != excluded && this._frequency[context, symbol] == 0)
        total += this._frequency[OrderZeroContext, symbol];

    var target = this._coder.GetCount(total);
    var cumulative = 0u;
    for (var symbol = 255; symbol >= 0; --symbol) {
      if (symbol == excluded || this._frequency[context, symbol] != 0)
        continue;

      var f = (uint)this._frequency[OrderZeroContext, symbol];
      if (target < cumulative + f) {
        this._coder.Consume(cumulative, f);
        return symbol;
      }

      cumulative += f;
    }

    throw new InvalidDataException("UHARC: LZP escape symbol not found.");
  }

  private void UpdateLiteral(int context, int symbol) {
    if (this._frequency[context, symbol] == 0) {
      this._escapeWeight[context] += 32;
      this.AddFrequency(OrderZeroContext, symbol, 16);
      if (this._contextTotal[OrderZeroContext] >= RescaleLimit)
        this.Halve(OrderZeroContext);
    }

    this.AddFrequency(context, symbol, 32);
    if (this._contextTotal[context] + this._escapeWeight[context] >= RescaleLimit)
      this.Halve(context);
  }

  private void AddFrequency(int context, int symbol, int delta) {
    this._frequency[context, symbol] = (ushort)Math.Min(ushort.MaxValue, this._frequency[context, symbol] + delta);
    this._contextTotal[context] += delta;
  }

  private void Halve(int context) {
    var total = 0;
    for (var symbol = 0; symbol < 256; ++symbol) {
      var f = (this._frequency[context, symbol] + 1) >> 1;
      this._frequency[context, symbol] = (ushort)f;
      total += f;
    }

    this._contextTotal[context] = total;
    if (context != OrderZeroContext)
      this._escapeWeight[context] = (this._escapeWeight[context] >> 2) + 1;
  }

  private static bool[] BuildLetterClasses() {
    var classes = new bool[256];
    for (var i = 0; i < 256; ++i)
      classes[i] = i is not ((>= 0x85 and <= 0x9E) or (>= 0xA5 and <= 0xBE));
    return classes;
  }
}
