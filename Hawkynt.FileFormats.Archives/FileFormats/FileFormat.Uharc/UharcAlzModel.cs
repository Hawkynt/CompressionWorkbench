namespace FileFormat.Uharc;

/// <summary>What the encoder codes in one ALZ step (ignored when decoding).</summary>
/// <param name="Token">1 new distance, 2 last distance, 3 an older distance, 4 literal.</param>
/// <param name="Distance">Copy distance of token 1.</param>
/// <param name="Slot">Recent-distance slot 1..11 of token 3.</param>
/// <param name="Length">Copy length, 2..255 (255 continues as a run).</param>
/// <param name="Literal">The byte of token 4.</param>
internal readonly record struct UharcAlzChoice(int Token, uint Distance, int Slot, int Length, byte Literal);

/// <summary>
/// The ALZ model (docs/UHARC-ON-DISK.md, "ALZ"), written once for both directions: every step
/// codes a token from an adaptive five-symbol table — 0 switches the literal model's
/// "selector", 1 is a copy with an explicit distance, 2 repeats the last distance, 3 reuses one
/// of the eleven older distances, 4 is a literal. Selectors 1..9 hand the literals to the
/// multimedia predictors of <see cref="UharcAlzSideModel"/>.
/// </summary>
internal sealed class UharcAlzModel<TCoder> where TCoder : struct, IUharcCoder {

  private const int RunLength = 255;
  private const int RecentCount = 12;
  private const ushort Half = 2048;

  private static readonly ushort[] TokenSeed = [0, 1, 34, 45, 57, 101];
  private static readonly ushort[] RunTokenSeed = [0, 1, 28, 78, 89, 100];

  private TCoder _coder;
  private readonly UharcAlzLiteralModel<TCoder> _literals;
  private readonly UharcAlzSideModel _side = new();
  private readonly byte[] _window;
  private readonly uint _mask;
  private readonly uint _dictionarySize;

  private readonly ushort[,] _tokens = new ushort[66, 6];
  private readonly byte[] _tokenHistory = new byte[65536];
  private readonly ushort[] _slotProbability = new ushort[64];
  private readonly ushort[] _recentProbability = new ushort[64];
  private ushort _lowBitProbability = Half;
  private readonly ushort[] _lengthProbability = new ushort[8192];
  private readonly byte[] _lengthHistory = new byte[0x40000];
  private readonly uint[] _recent = new uint[RecentCount];

  private uint _writePosition;
  private uint _history;
  private int _literalMode;
  private uint _runCount;
  private int _selector;
  private bool _alternativeText;

  /// <param name="coder">The coder side.</param>
  /// <param name="method">Stream method 40..87.</param>
  /// <param name="selector">The selector the encoder codes first, 0..10.</param>
  public UharcAlzModel(TCoder coder, int method, int selector) {
    this._dictionarySize = (uint)UharcMethods.DictionarySize(method);
    this._window = new byte[this._dictionarySize];
    this._mask = this._dictionarySize - 1;

    for (var row = 0; row < 66; ++row) {
      var seed = row < 64 ? TokenSeed : RunTokenSeed;
      for (var i = 0; i < 6; ++i)
        this._tokens[row, i] = seed[i];
    }

    Array.Fill(this._slotProbability, Half);
    Array.Fill(this._recentProbability, Half);
    Array.Fill(this._lengthProbability, Half);
    // The recent distances start empty: UHARC's decoders copy zeros through a slot no copy has
    // filled, so no valid stream uses one, and ours rejects it.

    this._coder = coder;
    this._literals = new UharcAlzLiteralModel<TCoder>(coder);
    this.CodeSelector(selector);
  }

  /// <summary>The dictionary (window) size in bytes.</summary>
  public uint DictionarySize => this._dictionarySize;

  /// <summary>Distance <paramref name="slot"/> (1..11) of the recent list; slot 0 is the last distance.</summary>
  public uint Recent(int slot) => this._recent[slot];

  /// <summary>The byte the next literal cannot be (the one that would have continued the last copy), or -1.</summary>
  public int Excluded => this._literals.Excluded;

  /// <summary>Runs of 255-byte copies so far (0 after anything else).</summary>
  public uint RunCount => this._runCount;

  public void ResetForNextFile(int selector) {
    this._literals.ResetForNextFile();
    for (var row = 0; row < 66; ++row) {
      var previous = (uint)this._tokens[row, 1];
      var sum = previous;
      for (var i = 2; i < 6; ++i) {
        var current = (uint)this._tokens[row, i];
        sum += (current - previous + 1) >> 1;
        this._tokens[row, i] = (ushort)sum;
        previous = current;
      }
    }

    this.CodeSelector(selector);
  }

  private void CodeSelector(int value) {
    var selector = (int)this._coder.Uniform(11, (uint)value);
    if (selector == this._selector) {
      this._alternativeText = false;
      return;
    }

    this._alternativeText = selector == 10;
    if (selector == 10) {
      this._literals.ForgetAlternativeContexts();
      selector = 0;
    }

    this._selector = selector;
    this._side.Select(selector);
  }

  /// <summary>Codes one token and what it carries; returns the position after the bytes it produced.</summary>
  public int Step(byte[] output, int position, in UharcAlzChoice choice) {
    int token;
    var escapes = 0;
    while ((token = this.CodeToken(choice.Token)) == 0) {
      this.CodeSelector(0);
      if (++escapes > 64)
        throw new InvalidDataException("UHARC: ALZ selector loop.");
    }

    if (token == 4) {
      var context = (int)(this._history & 0xFF);
      var before = (int)((this._history >> 8) & 0xFF);
      int primary, escape;
      if (this._selector != 0) {
        primary = this._side.Context;
        escape = this._side.EscapeContext;
        if (this._literals.Excluded >= 0)
          this._literals.Excluded = this._side.ToDifference(this._literals.Excluded);
      } else if (this._alternativeText) {
        primary = context + 0x200;
        escape = 0x401;
      } else {
        primary = (context & 0xC0) == (before & 0xC0) ? context + 0x100 : context;
        escape = ((context & 0xE0) >> 5) + 0x3F9;
      }

      this._literals.Side = this._selector != 0 ? this._side : null;
      var target = !TCoder.Encoding ? 0 : this._selector != 0 ? this._side.ToDifference(choice.Literal) : choice.Literal;
      var symbol = this._literals.Code(primary, escape, target);
      var value = this._selector != 0 ? this._side.ToByte(symbol) : (byte)symbol;
      this._literals.Excluded = -1;
      this._runCount = 0;
      return this.WriteByte(output, position, value);
    }

    var (distance, count) = this.CodeCopy(token, position, choice);
    if (count > output.Length - position)
      throw new InvalidDataException("UHARC: ALZ copy runs past the end of the stream.");

    var source = (this._writePosition - distance) & this._mask;
    for (var i = 0; i < count; ++i) {
      position = this.WriteByte(output, position, this._window[source]);
      source = (source + 1) & this._mask;
    }

    if (count == RunLength) {
      this._literals.Excluded = -1;
      ++this._runCount;
    } else {
      this._literals.Excluded = this._window[source];
      this._runCount = 0;
    }

    return position;
  }

  private int WriteByte(byte[] output, int position, byte value) {
    output[position] = value;
    this._window[this._writePosition] = value;
    this._writePosition = (this._writePosition + 1) & this._mask;
    this._history = (this._history << 8) | value;
    if (this._selector != 0)
      this._side.Observe(value, this._history, position + 1);
    return position + 1;
  }

  // ── Tokens ─────────────────────────────────────────────────────────────

  private int CodeToken(int target) {
    var key = (int)(this._history & 0xFFFF);
    var fromHistory = false;
    int row;
    if (this._runCount != 0)
      row = this._runCount > 2 ? 65 : 64;
    else {
      row = this._literalMode;
      if (row <= 15) {
        fromHistory = true;
        var remembered = this._tokenHistory[key];
        if ((remembered & 0x38) != 0)
          row = 32 + (remembered >> 3);
      }
    }

    var count = this._coder.Count(this._tokens[row, 5], TCoder.Encoding ? this._tokens[row, target] : 0u);
    var token = 4;
    while (token > 0 && count < this._tokens[row, token])
      --token;

    this._coder.Consume(this._tokens[row, token], (uint)(this._tokens[row, token + 1] - this._tokens[row, token]));
    if (token == 0)
      return 0;

    for (var i = token + 1; i < 6; ++i)
      this._tokens[row, i] += 88;

    // Halve the four token frequencies; the escape slot keeps its width of 1.
    if (this._tokens[row, 5] >= 16384) {
      Span<uint> width = stackalloc uint[5];
      for (var i = 1; i < 5; ++i)
        width[i] = (uint)(this._tokens[row, i + 1] - this._tokens[row, i] + 1) >> 1;
      for (var i = 1; i < 5; ++i)
        this._tokens[row, i + 1] = (ushort)(this._tokens[row, i] + width[i]);
    }

    if (fromHistory) {
      var tag = (token - 1) & 3;
      var remembered = this._tokenHistory[key];
      if (remembered >> 6 == tag) {
        if ((remembered & 63) != 63)
          ++remembered;
      } else
        remembered = (byte)(tag << 6);
      this._tokenHistory[key] = remembered;
    }

    this._literalMode = UharcAlzDecoder.ModeNext[((token - 1) << 5) + (this._literalMode & 31)];
    return token;
  }

  // ── Copies ─────────────────────────────────────────────────────────────

  private (uint Distance, int Count) CodeCopy(int token, int position, in UharcAlzChoice choice) {
    uint distance;
    int slot;
    switch (token) {
      case 1:
        distance = this.CodeDistance(position, choice.Distance);
        slot = RecentCount - 1;
        break;
      case 2:
        distance = this._recent[0];
        slot = 0;
        break;
      default:
        slot = this.SearchTree(this._recentProbability, UharcAlzDecoder.RecentTree, 1, 11, choice.Slot);
        distance = this._recent[slot];
        break;
    }

    var count = this.CodeLength(token, distance, choice.Length);
    if ((distance > 1 || this._runCount > 1) && slot != 0) {
      for (var i = slot; i > 0; --i)
        this._recent[i] = this._recent[i - 1];
      this._recent[0] = distance;
    }

    if (distance == 0 || distance > (uint)position || distance > this._dictionarySize)
      throw new InvalidDataException("UHARC: ALZ copy reaches before the start of the stream.");

    return (distance, count);
  }

  /// <summary>
  /// A binary search over <paramref name="tree"/>: each node's bit says whether the answer is the
  /// node or lies above it. The encoder answers for <paramref name="target"/>.
  /// </summary>
  private int SearchTree(ushort[] probabilities, byte[] tree, int root, int fallback, int target) {
    var node = root;
    var result = fallback;
    while (node != 0) {
      if (this._coder.Bit(ref probabilities[node], 6, TCoder.Encoding && target > node ? 1 : 0) == 0)
        result = node;
      node = tree[node * 2 + (result > node ? 1 : 0)];
    }

    if (TCoder.Encoding && result != target)
      throw new InvalidOperationException("UHARC: ALZ search tree cannot code the value.");
    return result;
  }

  /// <summary>The slot whose range holds <paramref name="distance"/>.</summary>
  internal static int SlotOf(uint distance) {
    var slot = UharcAlzDecoder.SlotBase.Length - 1;
    while (UharcAlzDecoder.SlotBase[slot] > distance)
      --slot;
    return slot;
  }

  /// <summary>
  /// Whether a new distance can be coded at <paramref name="position"/>: the decoder narrows a
  /// slot at the start of the stream and halves it around a low bit, which leaves some odd
  /// offsets out.
  /// </summary>
  internal static bool CanCodeDistance(uint distance, int position) {
    if (distance == 0 || distance > (uint)position)
      return false;
    var k = SlotOf(distance);
    if (k == 0)
      return true;

    var low = UharcAlzDecoder.SlotBase[k];
    var span = UharcAlzDecoder.SlotSpan[k];
    if (low + span - 1 > (uint)position)
      span = (uint)position - low + 1;
    var offset = distance - low;
    return span >= 4 ? offset >> 1 < span >> 1 : offset < span;
  }

  private uint CodeDistance(int position, uint target) {
    var wanted = TCoder.Encoding ? SlotOf(target) + 1 : 0;
    var k = this.SearchTree(this._slotProbability, UharcAlzDecoder.SlotTree, 16, 44, wanted) - 1;
    var low = UharcAlzDecoder.SlotBase[k];
    if (k == 0)
      return low;

    var span = UharcAlzDecoder.SlotSpan[k];
    if (low > (uint)position)
      throw new InvalidDataException("UHARC: ALZ distance slot beyond the decoded data.");
    if (low + span - 1 > (uint)position)
      span = (uint)position - low + 1;

    var offset = TCoder.Encoding ? target - low : 0;
    var lowBit = -1;
    if (span >= 4) {
      lowBit = this._coder.Bit(ref this._lowBitProbability, 6, (int)(offset & 1));
      span >>= 1;
      offset >>= 1;
    }

    var extra = 0u;
    if (span > 0x800) {
      var blocks = ((span - 1) >> 11) + 1;
      var block = this._coder.Uniform(blocks, offset >> 11);
      extra = block << 11;
      span = block + 1 != blocks ? 0x800 : span - extra;
    }

    if (span >= 2)
      extra += this._coder.Uniform(span, offset - extra);
    if (lowBit >= 0)
      extra = extra * 2 + (uint)lowBit;

    var distance = low + extra;
    if (TCoder.Encoding && distance != target)
      throw new InvalidOperationException("UHARC: ALZ distance cannot be coded here.");
    return distance;
  }

  /// <summary>The shortest copy token 1 can code at <paramref name="distance"/> (2 for the other tokens).</summary>
  internal static int MinimumLength(uint distance) => distance switch {
    < 0x100 => 3,
    < 0x1000 => 4,
    < 0x10000 => 5,
    < 0x100000 => 6,
    _ => 7,
  };

  private int CodeLength(int token, uint distance, int target) {
    int group, forced = 2;
    if (token == 1) {
      group = distance switch {
        < 0x100 => 0,
        < 0x1000 => 1,
        < 0x10000 => 2,
        < 0x100000 => 3,
        _ => 4,
      };
      forced = group + 3;
    } else
      group = this._runCount == 0 ? 5 : this._runCount > 2 ? 7 : 6;

    var key = -1;
    var previous = 0;
    var older = 0;
    if (group <= 5 && distance > 1) {
      key = this._window[(this._writePosition - distance) & this._mask] << 8
        | this._window[(this._writePosition - distance + 1) & this._mask];
      if (group == 5)
        key += 0x10000;
      previous = this._lengthHistory[key];
      older = this._lengthHistory[key + 0x20000];
      this._lengthHistory[key + 0x20000] = (byte)previous;
    }

    var node = 10;
    int result;
    while (true) {
      result = node + 1;
      if (node >= forced) {
        var index = group * 1024 + (node >= previous ? 256 : 0) + (older <= node ? 512 : 0) + node;
        if (this._coder.Bit(ref this._lengthProbability[index], 5, TCoder.Encoding && target > node ? 1 : 0) == 0)
          result = node;
      }

      node = UharcAlzDecoder.LengthTree[node * 2 + (node < result ? 1 : 0)];
      if (node == 0)
        break;
    }

    result &= 0xFF;
    if (TCoder.Encoding && result != target)
      throw new InvalidOperationException("UHARC: ALZ copy length cannot be coded.");
    if (key >= 0)
      this._lengthHistory[key] = (byte)result;
    return result;
  }
}
