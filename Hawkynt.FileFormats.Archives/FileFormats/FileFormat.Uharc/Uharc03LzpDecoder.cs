namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.3 LZP decoder (stream methods 8..21, the <c>-mz</c> mode, and the name table of every
/// 0.3 archive), implemented from docs/UHARC-ON-DISK.md section "UHARC 0.3 LZP".
/// </summary>
/// <remarks>
/// The ancestor of 0.4's LZP: the same predictor, literal contexts and length symbols, but
/// without runs, with 48 decisions picked by a three-bit outcome history, and with its own range
/// coder (<see cref="Uharc03RangeDecoder"/>).
/// </remarks>
internal sealed class Uharc03LzpDecoder {

  private const uint NoPosition = uint.MaxValue;
  private const int HashBits = 18;
  private const int RescaleLimit = 0x4000;
  private const int LengthContextFirst = 318;
  private const int ModelContexts = 326;
  private const int TextOrderZero = 326;
  private const int LengthOrderZero = 327;
  private const int ContextCount = 328;
  private const int ResetSpan = 0x10000;
  private const int Decisions = 48;

  private static readonly ushort[] DecisionSeed = [0x800, 0x400, 0x500, 0x400, 0x600, 0x400, 0x500, 0x400];
  private static readonly byte[] LengthContextStep = [0, 0, 1, 2, 3, 4, 5, 5, 6, 6, 6, 6];

  private readonly byte[] _window;
  private readonly uint _mask;
  private readonly Uharc03RangeDecoder _coder;

  private readonly uint[] _hashPosition = new uint[1 << HashBits];
  private readonly uint[] _hashContext = new uint[1 << HashBits];
  private readonly byte[] _hashLength = new byte[1 << HashBits];
  private readonly uint[] _pairPosition = new uint[65536];

  private readonly ushort[] _decisionSplit = new ushort[Decisions];
  private readonly ushort[] _decisionTotal = new ushort[Decisions];
  private int _outcomes = 7;

  private readonly ushort[,] _frequency = new ushort[ContextCount, 256];
  private readonly int[] _contextTotal = new int[ContextCount];
  private readonly int[] _escapeWeight = new int[ModelContexts];

  private uint _writePosition;
  private uint _history;
  private uint _lastDistance = NoPosition;
  private uint _predictedPosition = NoPosition;
  private uint _excludedPosition = NoPosition;

  private Uharc03LzpDecoder(byte[] packed, int offset, int length, int method) {
    var size = 1u << ((method - UharcMethods.LzpFirst) % 14 + 10);
    this._window = new byte[size];
    this._mask = size - 1;
    this._coder = new Uharc03RangeDecoder(packed, offset, length);

    Array.Fill(this._hashPosition, NoPosition);
    Array.Fill(this._hashContext, NoPosition);
    Array.Fill(this._pairPosition, NoPosition);
    this.ResetModels();
  }

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes of a 0.3 LZP stream.</summary>
  /// <param name="packed">Buffer holding the packed stream.</param>
  /// <param name="offset">Start of the packed stream.</param>
  /// <param name="length">Length of the packed stream.</param>
  /// <param name="method">Stream method byte, 8..21 (the dictionary is <c>2^(method+2)</c>).</param>
  /// <param name="unpackedSize">Number of bytes to produce.</param>
  /// <param name="fileEnds">Cumulative end offsets of the archived files (empty for a table).</param>
  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.LzpFirst or > 21)
      throw new ArgumentOutOfRangeException(nameof(method));

    var output = new byte[unpackedSize];
    if (unpackedSize == 0)
      return output;

    var decoder = new Uharc03LzpDecoder(packed, offset, length, method);
    var ends = fileEnds.Count > 0 && fileEnds[^1] == unpackedSize ? fileEnds : [];
    var nextEnd = 0;
    long resetAfter = ResetSpan;
    long position = 0;
    while (position < unpackedSize) {
      var copied = decoder.TryPrediction(output, position);
      position += copied > 0 ? copied : decoder.DecodeLiteral(output, position);

      // As 0.4: a file end passed beyond the threshold resets the models after this step.
      while (nextEnd < ends.Count - 1 && position > ends[nextEnd]) {
        if (ends[nextEnd++] > resetAfter) {
          decoder.ResetModels();
          resetAfter = position + ResetSpan;
        }
      }
    }

    return output;
  }

  private void ResetModels() {
    Array.Clear(this._frequency);
    Array.Clear(this._contextTotal);
    for (var symbol = 0; symbol < 256; ++symbol) {
      this._frequency[TextOrderZero, symbol] = 8;
      this._frequency[LengthOrderZero, symbol] = 8;
    }

    this._contextTotal[TextOrderZero] = 256 * 8;
    this._contextTotal[LengthOrderZero] = 256 * 8;
    Array.Fill(this._escapeWeight, 1);
    for (var i = 0; i < Decisions; ++i) {
      this._decisionSplit[i] = DecisionSeed[i & 7];
      this._decisionTotal[i] = 0x2000;
    }
  }

  private void Put(byte[] output, long position, byte value) {
    if (position < output.Length)
      output[position] = (byte)~value;
    this._window[this._writePosition] = value;
    this._history = (this._history << 8) | value;
    this._writePosition = (this._writePosition + 1) & this._mask;
  }

  // ── Prediction ─────────────────────────────────────────────────────────

  private int TryPrediction(byte[] output, long position) {
    var hash = (int)((this._history ^ (this._history >> 13)) & ((1u << HashBits) - 1));
    var pair = (int)(this._history & 0xFFFF);
    var hashedPosition = this._hashPosition[hash];
    this._predictedPosition = NoPosition;

    uint candidate;
    var decision = 0;
    if (hashedPosition != NoPosition && this._hashContext[hash] == this._history) {
      candidate = hashedPosition;
      decision = 1;
    } else {
      candidate = (this._writePosition - this._lastDistance) & this._mask;
      if (((candidate - this._writePosition) & this._mask) >= 0x104 && this.ContextAgrees(candidate))
        decision = 9;
      else if (this._pairPosition[pair] != NoPosition) {
        candidate = this._pairPosition[pair];
        decision = 17;
      }
    }

    this._hashPosition[hash] = this._writePosition;
    this._hashContext[hash] = this._history;
    this._pairPosition[pair] = this._writePosition;

    if (decision == 0 || ((candidate - this._writePosition) & this._mask) < 256)
      return 0;

    candidate &= this._mask;
    var index = decision - 1 + (this._hashLength[hash] == 0 ? 0x18 : 0);

    // The byte the previous copy stopped in front of is known not to follow: skip the decision.
    if (this._excludedPosition != NoPosition && this._window[this._excludedPosition] == this._window[candidate]) {
      this.CountDecision(index, miss: 1);
      return 0;
    }

    this._predictedPosition = candidate;
    if (this.DecodeDecision(index) != 0) {
      this._hashLength[hash] = 0;
      return 0;
    }

    var remembered = this._hashLength[hash];
    var context = LengthContextFirst + (remembered < LengthContextStep.Length ? LengthContextStep[remembered] : 7);
    this._excludedPosition = NoPosition;
    var length = this.DecodeSymbol(context);
    this.Update(context, length);
    this._hashLength[hash] = (byte)length;
    this._lastDistance = (this._writePosition - candidate) & this._mask;
    this._excludedPosition = length < 255 ? (candidate + (uint)length) & this._mask : NoPosition;

    var count = length == 0 ? 256 : length;
    var source = candidate;
    for (var i = 0; i < count; ++i) {
      this.Put(output, position + i, this._window[source]);
      source = (source + 1) & this._mask;
    }

    return count;
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

  private int DecisionIndex(int index) {
    var slot = (this._outcomes + index) & 0xFF;
    return slot >= Decisions ? slot & (Decisions - 1) : slot;
  }

  private int DecodeDecision(int index) {
    var slot = this.DecisionIndex(index);
    var split = (uint)this._decisionSplit[slot];
    var total = (uint)this._decisionTotal[slot];
    int miss;
    if (this._coder.GetCount(total) < split) {
      this._coder.Consume(0, split);
      miss = 0;
    } else {
      this._coder.Consume(split, total - split);
      miss = 1;
    }

    this.CountDecision(index, miss);
    return miss;
  }

  private void CountDecision(int index, int miss) {
    var slot = this.DecisionIndex(index);
    var split = (uint)this._decisionSplit[slot] + (miss == 0 ? 64u : 0u);
    var total = (uint)this._decisionTotal[slot] + 64;
    if (total >= RescaleLimit) {
      var halfSplit = (split + 1) >> 1;
      total = halfSplit + ((total - split + 1) >> 1);
      split = halfSplit;
    }

    this._decisionSplit[slot] = (ushort)split;
    this._decisionTotal[slot] = (ushort)total;
    this._outcomes = (this._outcomes * 2 + miss) & 7;
  }

  // ── Frequency model (literals and lengths) ─────────────────────────────

  private int DecodeLiteral(byte[] output, long position) {
    var context = UharcLzpDecoder.LiteralContext(this._history);
    var symbol = this.DecodeSymbol(context);
    this.Update(context, symbol);
    this._excludedPosition = this._predictedPosition != NoPosition && symbol == this._window[this._predictedPosition]
      ? (this._predictedPosition + 1) & this._mask
      : NoPosition;
    this.Put(output, position, (byte)symbol);
    return 1;
  }

  private static int OrderZeroOf(int context) => context >= LengthContextFirst ? LengthOrderZero : TextOrderZero;

  private int DecodeSymbol(int context) {
    var excluded = this._excludedPosition == NoPosition ? -1 : this._window[this._excludedPosition];
    var seen = this._contextTotal[context] - (excluded >= 0 ? this._frequency[context, excluded] : 0);
    if (seen > 0) {
      var count = this._coder.GetCount((uint)(seen + this._escapeWeight[context]));
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

        throw new InvalidDataException("UHARC: LZP symbol outside its context.");
      }

      this._coder.Consume((uint)seen, (uint)this._escapeWeight[context]);
    }

    var fallback = OrderZeroOf(context);
    var total = 0u;
    for (var symbol = 0; symbol < 256; ++symbol)
      if (symbol != excluded && this._frequency[context, symbol] == 0)
        total += this._frequency[fallback, symbol];

    var target = this._coder.GetCount(total);
    var cumulative = 0u;
    for (var symbol = 255; symbol >= 0; --symbol) {
      if (symbol == excluded || this._frequency[context, symbol] != 0)
        continue;

      var f = (uint)this._frequency[fallback, symbol];
      if (target < cumulative + f) {
        this._coder.Consume(cumulative, f);
        return symbol;
      }

      cumulative += f;
    }

    throw new InvalidDataException("UHARC: LZP escape symbol not found.");
  }

  private void Update(int context, int symbol) {
    if (this._frequency[context, symbol] == 0) {
      var fallback = OrderZeroOf(context);
      this._escapeWeight[context] += 32;
      this._frequency[fallback, symbol] += 16;
      this._contextTotal[fallback] += 16;
      if (this._contextTotal[fallback] >= RescaleLimit)
        this.Halve(fallback);
    }

    this._frequency[context, symbol] += 32;
    this._contextTotal[context] += 32;
    if (this._contextTotal[context] + this._escapeWeight[context] >= RescaleLimit)
      this.Halve(context);
  }

  private void Halve(int context) {
    var total = 0;
    for (var symbol = 0; symbol < 256; ++symbol) {
      var f = (this._frequency[context, symbol] + 1) >> 1;
      this._frequency[context, symbol] = (ushort)f;
      total += f;
    }

    this._contextTotal[context] = total;
    if (context < ModelContexts)
      this._escapeWeight[context] = (this._escapeWeight[context] >> 2) + 1;
  }
}
