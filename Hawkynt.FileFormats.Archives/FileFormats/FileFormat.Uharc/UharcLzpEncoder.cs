namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of <see cref="UharcLzpDecoder"/> (docs/UHARC-ON-DISK.md, "LZP"): it keeps
/// the decoder's model step for step and, wherever the decoder will ask, answers with what the
/// input holds — a hit with the longest copy the prediction allows, or a miss and a literal.
/// </summary>
internal sealed class UharcLzpEncoder {

  private const uint NoPosition = uint.MaxValue;
  private const int HashBits = 18;
  private const int RescaleLimit = 16384;
  private const int OrderZeroContext = 318;
  private const int ContextCount = 319;
  private const int RunLength = 255;

  private readonly byte[] _window;
  private readonly uint _mask;
  private readonly UharcRangeEncoder _coder = new();

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

  private UharcLzpEncoder(int method) {
    var size = 1u << (method - UharcMethods.LzpFirst + 10);
    this._window = new byte[size];
    this._mask = size - 1;

    Array.Fill(this._hashPosition, NoPosition);
    Array.Fill(this._hashContext, NoPosition);
    Array.Fill(this._pairPosition, NoPosition);

    for (var i = 0; i < 50; ++i) {
      this._decisionSplit[i] = UharcLzpDecoder.DecisionSeed[i];
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

  /// <summary>Packs <paramref name="data"/> as an LZP stream of <paramref name="method"/> (8..23).</summary>
  /// <param name="data">The bytes to pack.</param>
  /// <param name="method">Stream method byte; the window is <c>2^(method+2)</c>.</param>
  /// <param name="fileEnds">Cumulative end offsets of the archived files (empty for a table).</param>
  public static byte[] Encode(ReadOnlySpan<byte> data, int method, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.LzpFirst or > UharcMethods.LzpLast)
      throw new ArgumentOutOfRangeException(nameof(method));

    var encoder = new UharcLzpEncoder(method);
    if (data.IsEmpty)
      return encoder._coder.Finish();

    // The model works on complemented bytes.
    var symbols = new byte[data.Length];
    for (var i = 0; i < symbols.Length; ++i)
      symbols[i] = (byte)~data[i];

    var resets = new UharcModelResets(fileEnds, symbols.Length, UharcModelResets.LzpSpan, mergeSmallFiles: true);
    var position = 0;
    while (position < symbols.Length) {
      if (resets.IsResetPoint(position))
        encoder.ResetAtFileBoundary();

      var copied = encoder.TryPrediction(symbols, position);
      if (copied > 0)
        position += copied;
      else {
        encoder.EncodeLiteral(symbols[position]);
        encoder.WriteByte(symbols[position]);
        ++position;
      }
    }

    return encoder._coder.Finish();
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

  private void WriteByte(byte value) {
    this._window[this._writePosition] = value;
    this._history = (this._history << 8) | value;
    this._writePosition = (this._writePosition + 1) & this._mask;
  }

  // ── Prediction ─────────────────────────────────────────────────────────

  private int TryPrediction(byte[] symbols, int position) {
    var hash = (int)((this._history ^ (this._history >> 13)) & ((1u << HashBits) - 1));
    var pair = (int)(this._history & 0xFFFF);
    var hashedPosition = this._hashPosition[hash];
    var hashedContext = this._hashContext[hash];
    var hashLength = this._hashLength[hash];
    var pairPosition = this._pairPosition[pair];
    this._predictedPosition = NoPosition;

    uint candidate;
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

    if (decision == 0 || ((candidate - this._writePosition) & this._mask) < 256) {
      this._runCount = 0;
      return 0;
    }

    if (this._runCount == 0)
      decision += (hashLength == 0 ? 23 : -1) + this._decisionHistory;
    decision = Math.Clamp(decision, 0, 49);

    if (this._excludedPosition != NoPosition && this._window[this._excludedPosition] == this._window[candidate]) {
      this.StoreDecision(decision, this._decisionSplit[decision], this._decisionTotal[decision] + 64u);
      this._decisionHistory = UharcLzpDecoder.DecisionHistoryNext[(this._decisionHistory * 2 + 1) & 15];
      this._runCount = 0;
      return 0;
    }

    this._predictedPosition = candidate;
    var matched = this.MatchLength(symbols, position, candidate);
    // Length 1 has no code: a single matching byte is a miss followed by that byte as a literal.
    var miss = matched < 2 ? 1 : 0;
    this.EncodeDecision(decision, miss);
    this._decisionHistory = UharcLzpDecoder.DecisionHistoryNext[(this._decisionHistory * 2 + miss) & 15];
    var candidatePair = this.PairAt(candidate);
    if (miss != 0) {
      this._hashLength[hash] = 0;
      this._pairLength[candidatePair] = 0;
      this._runCount = 0;
      return 0;
    }

    // A copy stops right before the first byte that differs (the decoder then excludes it),
    // runs on as 255, or ends with the input.
    var length = matched;
    var pairLength = this._pairLength[candidatePair];
    var group = this._runCount != 0 ? (this._runCount > 2 ? 9 : 8) : UharcLzpDecoder.LengthGroup(fromHash ? hashLength : pairLength);
    this.EncodeLength(group, pairLength, length);
    this._hashLength[hash] = (byte)length;
    this._pairLength[candidatePair] = (byte)length;
    this._lastDistance = (this._writePosition - candidate) & this._mask;

    var count = Math.Min(symbols.Length - position, length);
    if (length == RunLength) {
      this._excludedPosition = NoPosition;
      ++this._runCount;
    } else {
      this._excludedPosition = (candidate + (uint)count) & this._mask;
      this._runCount = 0;
    }

    for (var i = 0; i < count; ++i)
      this.WriteByte(symbols[position + i]);
    return count;
  }

  /// <summary>How many of the next bytes the copy from <paramref name="candidate"/> reproduces, at most 255.</summary>
  private int MatchLength(byte[] symbols, int position, uint candidate) {
    var distance = (int)((this._writePosition - candidate) & this._mask);
    var limit = Math.Min(RunLength, symbols.Length - position);
    var length = 0;
    while (length < limit) {
      var expected = length < distance ? this._window[(candidate + (uint)length) & this._mask] : symbols[position + length - distance];
      if (expected != symbols[position + length])
        break;
      ++length;
    }

    return length;
  }

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

  private void EncodeDecision(int index, int miss) {
    var split = (uint)this._decisionSplit[index];
    var total = (uint)this._decisionTotal[index];
    if (miss == 0) {
      this._coder.Encode(0, split, total);
      split += 64;
    } else
      this._coder.Encode(split, total - split, total);

    this.StoreDecision(index, split, total + 64);
  }

  private void StoreDecision(int index, uint split, uint total) {
    if (total >= RescaleLimit) {
      split = Math.Max(1, (split + 1) >> 1);
      total = Math.Max(split + 1, (total + 2) >> 1);
    }

    this._decisionSplit[index] = (ushort)split;
    this._decisionTotal[index] = (ushort)total;
  }

  private void EncodeLength(int group, int previous, int length) {
    var node = 6;
    var result = RunLength;
    while (node != 0) {
      var branch = previous <= node ? 256 : 0;
      var bit = length <= node ? 0 : 1;
      this._coder.EncodeBit(ref this._lengthProbability[group, node + branch], 6, bit);
      if (bit == 0)
        result = node;
      node = UharcLzpDecoder.LengthLadder[node * 2 + (result > node ? 1 : 0)];
    }

    if (result != length)
      throw new InvalidOperationException($"UHARC: copy length {length} has no code.");
  }

  // ── Literals ───────────────────────────────────────────────────────────

  private void EncodeLiteral(byte symbol) {
    var context = this.LiteralContext();
    var excluded = this._excludedPosition == NoPosition ? -1 : this._window[this._excludedPosition];
    if (symbol == excluded)
      throw new InvalidOperationException("UHARC: literal equals the excluded byte.");

    this.EncodeSymbol(context, excluded, symbol);
    this.UpdateLiteral(context, symbol);
    this._excludedPosition = this._predictedPosition != NoPosition && symbol == this._window[this._predictedPosition]
      ? (this._predictedPosition + 1) & this._mask
      : NoPosition;
  }

  private int LiteralContext() {
    var last = (int)(this._history & 0xFF);
    var before = (int)((this._history >> 8) & 0xFF);
    switch (this._history & 0xFFFF) {
      case 0xFFFF: return 314;
      case 0xD1DF: return 315;
      case 0xD3DF: return 316;
    }

    if (last == 0xDF && UharcLzpDecoder.IsNonLetter[before])
      return 317;

    return !UharcLzpDecoder.IsNonLetter[last] && UharcLzpDecoder.IsNonLetter[before] ? last + 123 : last;
  }

  private void EncodeSymbol(int context, int excluded, int symbol) {
    var seen = this._contextTotal[context] - (excluded >= 0 ? this._frequency[context, excluded] : 0);
    if (seen > 0) {
      var withEscape = (uint)(seen + this._escapeWeight[context]);
      if (this._frequency[context, symbol] != 0) {
        var low = 0u;
        for (var s = 255; s > symbol; --s)
          if (s != excluded)
            low += this._frequency[context, s];
        this._coder.Encode(low, this._frequency[context, symbol], withEscape);
        return;
      }

      this._coder.Encode((uint)seen, (uint)this._escapeWeight[context], withEscape);
    }

    uint total = 0, below = 0;
    for (var s = 0; s < 256; ++s) {
      if (s == excluded || this._frequency[context, s] != 0)
        continue;
      total += this._frequency[OrderZeroContext, s];
      if (s > symbol)
        below += this._frequency[OrderZeroContext, s];
    }

    this._coder.Encode(below, this._frequency[OrderZeroContext, symbol], total);
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
}
