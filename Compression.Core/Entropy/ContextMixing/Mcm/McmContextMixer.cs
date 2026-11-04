using System.Numerics;

namespace Compression.Core.Entropy.ContextMixing.Mcm;

/// <summary>Which of mcm's model sets drives the coder (fixed per stream in an mcm archive).</summary>
public enum McmModelProfile {
  /// <summary>Text: word, bracket and interval contexts, text byte order.</summary>
  Text,
  /// <summary>Binary: sparse contexts, binary byte order, fast path after long unmatched runs.</summary>
  Binary,
  /// <summary>The undocumented <c>-s</c> level: the text set outside matches, orders 0–9 inside.</summary>
  Simple,
}

/// <summary>
/// mcm 0.84's context-mixing coder (<c>cm::CM</c>), reimplemented from <c>docs/MCM-ON-DISK.md</c>:
/// hashed bit-history states, one adaptive probability map per input, a single mixer layer
/// selected by an interval context, a match model with LZP and two SSE stages, over a 7-Zip
/// range coder. One instance codes one stream.
/// </summary>
public sealed class McmContextMixer {
  /// <summary>Highest memory level the managed tables can hold (level 10 needs a hash table past 2 GiB).</summary>
  public const int MaxMemoryLevel = 9;

  private const int Order1Base = 512;
  private const int Order2Base = 131_584;
  private const int Sparse2Base = 33_686_016;
  private const int Sparse3Base = 33_817_088;
  private const int Sparse4Base = 33_948_160;
  private const int HashBase = 34_079_232;
  private const int MaxMatch = 80;
  private const int MatchModelsPerContext = (MaxMatch + 1) * 2;
  private const int SseCells = 33;
  private const int SseContexts = 257 * 256;
  private const long NeverFastPath = 0xFFFFFFFF;
  private const int LzpNever = 101;

  /// <summary><c>0x4EC457C1 * 19</c> evaluated as a 32-bit signed product, then widened with its sign.</summary>
  private static readonly ulong InitialHashSeed = unchecked((ulong)(long)(int)(0x4EC457C1u * 19u));

  /// <summary><c>7 * 0x97654321</c> modulo 2^32.</summary>
  private const uint IntervalSalt = unchecked(7u * 0x97654321u);

  private const uint Interval2Salt = 2_716_049_183u;

  private enum Kind {
    Order0 = 0, Order1, Order2, Order3, Order4, Order5, Order6, Order7, Order8, Order9, Order10, Order11, Order12,
    Bracket, Sparse2, Sparse3, Sparse4, Sparse23, Sparse34, Word1, Word2, Word12, Interval, Interval2, Interval3, SpecialChar,
  }

  private sealed class ContextSet {
    public uint Enabled;
    public int MatchOrder;
    public int MaxOrder;
    public int MinLzp = int.MaxValue;
    public long FastPathAfter = NeverFastPath;

    public static ContextSet Of(int inputs, int matchOrder, params Kind[] kinds) {
      var set = new ContextSet { MatchOrder = matchOrder == 0 ? 0 : matchOrder - 1 };
      for (var i = 0; i < kinds.Length && i < inputs; ++i)
        set.Enabled |= 1u << (int)kinds[i];
      var maxModelOrder = 0;
      for (var order = 0; order <= 12; ++order)
        if ((set.Enabled & (1u << order)) != 0)
          maxModelOrder = order;
      set.MaxOrder = Math.Max(maxModelOrder, set.MatchOrder);
      return set;
    }

    public bool Has(Kind kind) => (this.Enabled & (1u << (int)kind)) != 0;
  }

  private readonly int _inputs;
  private readonly McmModelProfile _profile;
  private readonly byte[] _forward = new byte[256];
  private readonly byte[] _backward = new byte[256];

  private readonly byte[] _table;
  private readonly uint _hashMask;

  private readonly uint[] _mapProbability;
  private readonly short[] _mapStretch;
  private readonly byte[] _mapLearn;

  private readonly int[] _mixers;
  private readonly int _mixerStride;
  private readonly int _intervalMixerMask;
  private readonly int[] _mixerRate = new int[256];

  private readonly uint[] _sse1 = new uint[SseContexts * SseCells];
  private readonly uint[] _sse2 = new uint[SseContexts * SseCells];
  private int _ssePick;

  private readonly byte[] _history;
  private readonly ulong _historyMask;
  private ulong _historyPosition;

  private readonly uint[] _matchTable;
  private readonly uint _matchTableMask;
  private readonly int[] _matchModels = new int[256 * MatchModelsPerContext];
  private uint _matchHash;
  private ulong _matchPosition;
  private int _matchLength;
  private uint _expectedCode;
  private int _matchModel;
  private long _missLength;

  private readonly int[] _fastMix;

  private readonly ContextSet _set;
  private readonly ContextSet _matchSet;
  private readonly bool _textIntervals;
  private readonly byte[] _intervalMap = new byte[256];
  private readonly byte[] _intervalMap2 = new byte[256];
  private readonly byte[] _smallIntervalMap = new byte[256];
  private readonly ulong _intervalMask;
  private ulong _interval;
  private ulong _interval2;
  private ulong _smallInterval;
  private ulong _lastBytes;

  private readonly int[] _wordCode = new int[256];
  private readonly int _wordEscape;
  private ulong _wordHash1;
  private ulong _wordHash2;
  private uint _wordPrevious;
  private int _wordLength;
  private int _wordLastByte;

  private readonly bool[] _bracketSpecial = new bool[256];
  private readonly byte[] _bracketStack = new byte[256];
  private readonly int[] _bracketSymbol = new int[8];
  private int _bracketDepth;
  private uint _bracketRun;
  private uint _bracketLast;

  private readonly int[] _contexts;
  private readonly int[] _slots;
  private readonly int[] _states;
  private readonly int[] _inputsStretch;

  private McmRangeEncoder? _encoder;
  private McmRangeDecoder? _decoder;

  /// <summary>Creates a coder.</summary>
  /// <param name="inputs">Model inputs: 3 turbo, 4 fast, 6 mid and simple, 10 high.</param>
  /// <param name="profile">The model set.</param>
  /// <param name="memoryLevel">mcm's memory level, 0..<see cref="MaxMemoryLevel"/>.</param>
  /// <param name="lzp">Whether LZP bits are coded for long matches.</param>
  public McmContextMixer(int inputs, McmModelProfile profile, int memoryLevel, bool lzp) {
    if (inputs is < 1 or > 12)
      throw new ArgumentOutOfRangeException(nameof(inputs));
    if (memoryLevel is < 0 or > MaxMemoryLevel)
      throw new NotSupportedException($"mcm memory level {memoryLevel} needs more than the 2 GiB a managed table can hold.");
    this._inputs = inputs;
    this._profile = profile;

    var text = BuildSet(inputs, 7, Kind.Order4, Kind.Order2, Kind.Bracket, Kind.Interval, Kind.Order3, Kind.Order5, Kind.Word1,
      Kind.Interval2, Kind.Order0, Kind.Order1, Kind.Sparse2, Kind.Sparse3, Kind.Sparse4, Kind.Sparse34);
    text.MinLzp = lzp ? 12 : LzpNever;
    var textMatch = BuildSet(inputs, 7, Kind.Order4, Kind.Order2, Kind.Bracket, Kind.Order7, Kind.Interval, Kind.SpecialChar,
      Kind.Word1, Kind.Order5, Kind.Order2, Kind.Order1, Kind.Sparse2, Kind.Sparse3, Kind.Sparse4, Kind.Sparse34);
    var binary = BuildSet(inputs, 6, Kind.Order1, Kind.Order3, Kind.Sparse34, Kind.Sparse2, Kind.Interval2, Kind.Order2, Kind.Order4,
      Kind.Sparse3, Kind.Sparse4, Kind.Order0, Kind.Order0);
    binary.MinLzp = lzp ? 0 : LzpNever;
    binary.FastPathAfter = 25_000;
    var binaryMatch = BuildSet(inputs, 6, Kind.Order1, Kind.Order2, Kind.Sparse34, Kind.Order4, Kind.Interval2, Kind.Sparse2,
      Kind.Sparse3, Kind.Sparse4, Kind.Order0, Kind.Order9);
    var simple = BuildSet(inputs, 8, Kind.Order0, Kind.Order1, Kind.Order2, Kind.Order3, Kind.Order4, Kind.Order6,
      Kind.Order7, Kind.Order8, Kind.Order9);

    (this._set, this._matchSet) = profile switch {
      McmModelProfile.Text => (text, textMatch),
      McmModelProfile.Binary => (binary, binaryMatch),
      _ => (text, simple),
    };
    this._textIntervals = profile == McmModelProfile.Text;

    // Byte orders: everything below works on reordered symbols.
    var textOrder = McmTables.TextOrder;
    var order = profile == McmModelProfile.Binary ? McmTables.BinaryOrder : textOrder;
    for (var i = 0; i < 256; ++i) {
      this._backward[i] = order[i];
      this._forward[order[i]] = (byte)i;
    }
    Span<byte> textForward = stackalloc byte[256];
    for (var i = 0; i < 256; ++i)
      textForward[textOrder[i]] = (byte)i;

    // Interval maps.
    if (this._textIntervals)
      for (var i = 0; i < 256; ++i) {
        this._smallIntervalMap[textForward[i]] = McmTables.TextSmallIntervalMap[i];
        this._intervalMap[textForward[i]] = McmTables.TextIntervalMap[i];
        this._intervalMap2[textForward[i]] = McmTables.TextIntervalMap2[i];
      }
    else {
      var binaryOrder = McmTables.BinaryOrder;
      for (var i = 0; i < 256; ++i) {
        var symbol = 0;
        for (var j = 0; j < 256; ++j)
          if (binaryOrder[j] == i) {
            symbol = j;
            break;
          }
        var small = (i < 1 ? 1 : 0) + (i < 32 ? 1 : 0) + (i < 64 ? 1 : 0) + (i < 128 ? 1 : 0) + (i < 255 ? 1 : 0) + (i < 142 ? 1 : 0) + (i < 138 ? 1 : 0);
        var wide = small + (i < 140 ? 1 : 0) + (i < 137 ? 1 : 0) + (i < 97 ? 1 : 0) + (i < 100 ? 1 : 0) + (i < 128 ? 1 : 0) + (i < 48 ? 1 : 0) + (i < 252 ? 1 : 0) + (i < 99 ? 1 : 0);
        this._smallIntervalMap[symbol] = (byte)small;
        this._intervalMap[symbol] = (byte)wide;
        this._intervalMap2[symbol] = (byte)wide;
      }
    }
    this._intervalMask = this._textIntervals ? (1UL << 49) - 1 : 0xFFFFFFFFUL;

    // Word model on text-ordered symbols.
    Array.Fill(this._wordCode, -1);
    var code = 0;
    for (var ch = 'a'; ch <= 'z'; ++ch)
      this._wordCode[textForward[ch]] = code++;
    for (var ch = 'A'; ch <= 'Z'; ++ch)
      this._wordCode[textForward[ch]] = this._wordCode[textForward[ch + 32]];
    foreach (var b in (ReadOnlySpan<byte>)[6, 38, 92, 3])
      this._wordCode[textForward[b]] = code++;
    for (var i = 128; i < 256; ++i)
      if (this._wordCode[textForward[i]] < 0)
        this._wordCode[textForward[i]] = code++;
    this._wordEscape = textForward[3];
    this.ResetWord();

    // Bracket model.
    foreach (var b in (ReadOnlySpan<byte>)[42, 62, 36, 92, 34, 72, 10])
      this._bracketSpecial[textForward[b]] = true;
    var brackets = "[({<])}>";
    for (var i = 0; i < 8; ++i)
      this._bracketSymbol[i] = textForward[brackets[i]];

    // Hash table and direct tables.
    this._hashMask = (2u << 20 << memoryLevel) - 1;
    this._table = new byte[(int)this._hashMask + 1 + HashBase + 65536];

    // Probability maps.
    var maps = 2 * (inputs + 1);
    this._mapProbability = new uint[maps * 256];
    this._mapStretch = new short[maps * 256];
    this._mapLearn = new byte[maps * 256];
    var initial = McmTables.InitialProbabilities;
    for (var j = 0; j < maps; ++j)
      for (var k = 0; k < 256; ++k) {
        int p = initial[Math.Min(j, 9) * 256 + k];
        this._mapProbability[j * 256 + k] = (uint)p << 19;
        this._mapStretch[j * 256 + k] = McmTables.Stretch[p];
        this._mapLearn[j * 256 + k] = 9;
      }
    this.AssignLearnRates();

    // Mixers.
    this._mixerStride = inputs + 2;
    var mixerCount = 256 << (4 + memoryLevel);
    this._mixers = new int[mixerCount * this._mixerStride];
    var initialWeight = (41 << 17) / inputs / 16;
    for (var m = 0; m < mixerCount; ++m)
      this._mixers.AsSpan(m * this._mixerStride, inputs).Fill(initialWeight);
    var contextsPerMixer = mixerCount / 256;
    this._intervalMixerMask = this._textIntervals ? contextsPerMixer / 2 - 1 : contextsPerMixer / 4 - 1;
    for (var i = 0; i < 256; ++i)
      this._mixerRate[i] = this._textIntervals ? 31 + 2500 / (3 + i) : 60 + 3100 / (3 + i);

    // SSE.
    for (var i = 0; i < SseCells; ++i) {
      var p = Math.Min(i << 7, 4096);
      var squashed = p - 2048 <= -2048 ? 1 : p - 2048 >= 2048 ? 4095 : McmTables.SquashAny[(p - 2048) & 0x7FFF];
      var cell = ((uint)squashed << 19) | 64u;
      for (var ctx = 0; ctx < SseContexts; ++ctx) {
        this._sse1[ctx * SseCells + i] = cell;
        this._sse2[ctx * SseCells + i] = cell;
      }
    }

    // Match model.
    var historySize = (1 << 18) << memoryLevel;
    this._history = new byte[historySize + 4];
    this._historyMask = (ulong)historySize - 1;
    this._matchTable = new uint[historySize >> 1];
    this._matchTableMask = (uint)(historySize >> 1) - 1;
    for (var c = 0; c < 256; ++c)
      for (var i = 0; i < MatchModelsPerContext; ++i) {
        var length = 4 + 4 + i / 2;
        var p = (i & 1) != 0 ? 4095 - 2048 / length : 2048 / length;
        this._matchModels[c * MatchModelsPerContext + i] = p << 18;
      }

    this._fastMix = new int[4096];
    Array.Fill(this._fastMix, 2048 << 18);

    this._contexts = new int[inputs];
    this._slots = new int[inputs];
    this._states = new int[inputs];
    this._inputsStretch = new int[inputs];
  }

  private static ContextSet BuildSet(int inputs, int matchOrder, params Kind[] kinds) => ContextSet.Of(inputs, matchOrder, kinds);

  /// <summary>Compresses <paramref name="data"/> into a range-coded stream.</summary>
  public byte[] Encode(ReadOnlySpan<byte> data) {
    this._encoder = new McmRangeEncoder(Math.Max(64, data.Length / 2));
    foreach (var raw in data) {
      var symbol = this._forward[raw];
      this.CodeByte(symbol);
      this.Update(symbol);
    }
    var result = this._encoder.Finish();
    this._encoder = null;
    return result;
  }

  /// <summary>Decodes <paramref name="count"/> bytes from the stream starting at <paramref name="offset"/>.</summary>
  /// <param name="source">Buffer holding the coded stream.</param>
  /// <param name="offset">Where the stream starts.</param>
  /// <param name="end">End of readable input; reads past it see 0xFF.</param>
  /// <param name="count">Bytes to decode.</param>
  /// <param name="consumed">Bytes of <paramref name="source"/> the stream occupies.</param>
  public byte[] Decode(byte[] source, int offset, int end, int count, out int consumed) {
    this._decoder = new McmRangeDecoder(source, offset, end);
    var output = new byte[count];
    for (var i = 0; i < count; ++i) {
      var symbol = this.CodeByte(0);
      this.Update(symbol);
      output[i] = this._backward[symbol];
    }
    consumed = this._decoder.Position - offset;
    if (this._decoder.Overran)
      throw new InvalidDataException("mcm CM stream is truncated.");
    this._decoder = null;
    return output;
  }

  private int CodeBit(int bit, int probability) {
    if (this._decoder != null)
      return this._decoder.Decode(probability);
    this._encoder!.Encode(bit, probability);
    return bit;
  }

  private void AssignLearnRates() {
    Span<byte> textLearn = stackalloc byte[26];
    Span<byte> binaryLearn = stackalloc byte[26];
    textLearn.Fill(9);
    binaryLearn.Fill(7);
    textLearn[(int)Kind.Order0] = 24;
    textLearn[(int)Kind.Order1] = 24;
    textLearn[(int)Kind.Order2] = 24;
    textLearn[(int)Kind.Order3] = 11;
    textLearn[(int)Kind.Order4] = 12;
    textLearn[(int)Kind.Order5] = 9;
    textLearn[(int)Kind.Order7] = 9;
    textLearn[(int)Kind.Bracket] = 15;
    textLearn[(int)Kind.Interval] = 9;
    textLearn[(int)Kind.Interval2] = 24;
    textLearn[(int)Kind.Word1] = 20;
    textLearn[(int)Kind.SpecialChar] = 13;
    binaryLearn[(int)Kind.Order0] = 10;
    binaryLearn[(int)Kind.Order1] = 8;
    binaryLearn[(int)Kind.Order2] = 8;
    binaryLearn[(int)Kind.Order3] = 7;
    binaryLearn[(int)Kind.Order4] = 6;
    binaryLearn[(int)Kind.Sparse34] = 8;
    binaryLearn[(int)Kind.Sparse2] = 8;
    binaryLearn[(int)Kind.Sparse3] = 9;
    binaryLearn[(int)Kind.Sparse4] = 6;
    binaryLearn[(int)Kind.Interval2] = 7;
    var learn = this._textIntervals ? textLearn : binaryLearn;

    var kinds = ListKinds(this._set, this._inputs);
    var matchKinds = ListKinds(this._matchSet, this._inputs);
    for (var i = 0; i < this._inputs; ++i) {
      this._mapLearn.AsSpan(i * 256, 256).Fill(learn[kinds[i]]);
      this._mapLearn.AsSpan((i + this._inputs + 1) * 256, 256).Fill(learn[matchKinds[i]]);
    }
  }

  /// <summary>The kinds of a set's contexts in hash order; unused slots count as order 0.</summary>
  private static int[] ListKinds(ContextSet set, int inputs) {
    List<Kind> order = [Kind.Order0, Kind.SpecialChar, Kind.Order1, Kind.Sparse2, Kind.Sparse3, Kind.Sparse4, Kind.Sparse23, Kind.Sparse34, Kind.Order2];
    for (var o = 3; o <= set.MaxOrder && o <= 12; ++o)
      order.Add((Kind)o);
    order.AddRange([Kind.Word1, Kind.Word2, Kind.Word12, Kind.Interval, Kind.Interval2, Kind.Bracket]);
    var kinds = new int[inputs];
    var n = 0;
    foreach (var kind in order)
      if (set.Has(kind) && n < inputs)
        kinds[n++] = (int)kind;
    return kinds;
  }

  private static uint HashFunc(ulong a, ulong b) {
    b += a;
    b += BitOperations.RotateLeft(unchecked((uint)(b * 7)), 11);
    return unchecked((uint)(b ^ (b >> 13)));
  }

  private static uint Hashify(ulong h) {
    h ^= h >> 9;
    h ^= unchecked(h * 6937);
    h += h >> 13;
    return unchecked((uint)h);
  }

  private int HashSlot(uint hash) => (int)(hash & this._hashMask) + HashBase;

  private byte History(ulong position) => this._history[(int)(position & this._historyMask)];

  /// <summary>Fills the context bases for <paramref name="set"/> in hash order; returns the running order hash.</summary>
  private uint Contexts(uint h, ContextSet set) {
    var contexts = this._contexts;
    Array.Clear(contexts);
    var n = 0;
    var last = this._lastBytes;
    int p0 = (byte)last, p1 = (byte)(last >> 8), p2 = (byte)(last >> 16), p3 = (byte)(last >> 24);
    var enabled = set.Enabled;
    if ((enabled & (1u << (int)Kind.Order0)) != 0)
      contexts[n++] = 0;
    if ((enabled & (1u << (int)Kind.SpecialChar)) != 0)
      contexts[n++] = this.HashSlot(123_119_912u);
    if ((enabled & (1u << (int)Kind.Order1)) != 0)
      contexts[n++] = Order1Base + p0 * 512;
    if ((enabled & (1u << (int)Kind.Sparse2)) != 0)
      contexts[n++] = Sparse2Base + p1 * 512;
    if ((enabled & (1u << (int)Kind.Sparse3)) != 0)
      contexts[n++] = Sparse3Base + p2 * 512;
    if ((enabled & (1u << (int)Kind.Sparse4)) != 0)
      contexts[n++] = Sparse4Base + p3 * 512;
    if ((enabled & (1u << (int)Kind.Sparse23)) != 0)
      contexts[n++] = this.HashSlot(HashFunc((ulong)p2, HashFunc((ulong)p1, 0x37220B98)));
    if ((enabled & (1u << (int)Kind.Sparse34)) != 0)
      contexts[n++] = this.HashSlot(HashFunc((ulong)p3, HashFunc((ulong)p2, 0x651A833E)));
    if ((enabled & (1u << (int)Kind.Order2)) != 0)
      contexts[n++] = Order2Base + (int)(last & 0xFFFF) * 512;
    for (var order = 3; order <= set.MaxOrder; ++order) {
      h = HashFunc(this.History(this._historyPosition - (ulong)order), h);
      if (order <= 12 && (enabled & (1u << order)) != 0)
        contexts[n++] = this.HashSlot(h);
    }
    if ((enabled & (1u << (int)Kind.Word1)) != 0) {
      var hash = this.WordHash();
      if (this._wordLength < 2)
        hash ^= this._wordPrevious;
      contexts[n++] = this.HashSlot(hash + 99_912_312u);
    }
    if ((enabled & (1u << (int)Kind.Word2)) != 0)
      contexts[n++] = this.HashSlot(this._wordPrevious + 111_992u);
    if ((enabled & (1u << (int)Kind.Word12)) != 0)
      contexts[n++] = this.HashSlot((this.WordHash() ^ this._wordPrevious) + 5_111_321u);
    if ((enabled & (1u << (int)Kind.Interval)) != 0)
      contexts[n++] = this.HashSlot(Hashify(this._interval & this._intervalMask) + IntervalSalt);
    if ((enabled & (1u << (int)Kind.Interval2)) != 0)
      contexts[n++] = this.HashSlot(Hashify(this._interval2 & 0xFFFFFFFFUL) + Interval2Salt);
    if ((enabled & (1u << (int)Kind.Bracket)) != 0)
      contexts[n++] = this.HashSlot(Hashify(this.BracketHash() + 739_108_107u) + 0x20019412u);
    return h;
  }

  /// <summary>Codes one reordered symbol (or decodes one when decoding) and returns it.</summary>
  private int CodeByte(int symbol) {
    var matchLength = 0;
    var expected = 0;
    if (this._set.MatchOrder != 0) {
      this.UpdateMatch();
      matchLength = this._matchLength;
      if (matchLength != 0) {
        this._missLength = 0;
        this._matchModel = (int)(this._interval & 0xFF) * MatchModelsPerContext + 2 * Math.Min(matchLength - 4, MaxMatch);
        expected = this.History(this._matchPosition + 1);
        this._expectedCode = (uint)expected << 24;
      }
    }

    var h = HashFunc((this._lastBytes & 0xFFFF) * 3, InitialHashSeed);
    if (matchLength == 0 && ++this._missLength >= this._set.FastPathAfter)
      return this.CodeFast(symbol, h);

    var inMatch = matchLength != 0;
    var mapBase = inMatch ? this._inputs + 1 : 0;
    this._matchHash = this.Contexts(h, inMatch ? this._matchSet : this._set);

    int mixerContext;
    if (!this._textIntervals)
      mixerContext = ((int)(this._interval & (ulong)this._intervalMixerMask) << 1) + (matchLength > 0 ? 1 : 0);
    else
      mixerContext = ((int)(this._smallInterval & (ulong)this._intervalMixerMask) << 1) | (matchLength > 0 || this._wordLength > 6 ? 1 : 0);
    var mixerBase = mixerContext << 8;

    var sseContext = 0;
    if (inMatch && matchLength >= this._set.MinLzp) {
      sseContext = 256 * (1 + expected);
      var hit = this.CodeLzpBit(symbol == expected ? 1 : 0, expected, mixerBase, sseContext, mapBase);
      if (hit != 0)
        return expected;
    }
    return this.CodeSymbolBits(symbol, mixerBase, sseContext, mapBase);
  }

  private int CodeLzpBit(int bit, int expected, int mixerBase, int sseContext, int mapBase) {
    var x = 256 + expected;
    var p = this._inputsStretch;
    p[0] = McmTables.Stretch[this._matchModels[this._matchModel + 1] >> 18];
    for (var i = 1; i < this._inputs; ++i) {
      var slot = this._contexts[i] ^ x;
      this._slots[i] = slot;
      int state = this._table[slot];
      this._states[i] = state;
      p[i] = this._mapStretch[(mapBase + i) * 256 + state];
    }
    var mixer = mixerBase * this._mixerStride;
    var stretched = this.Mix(mixer);
    var mixed = (int)McmTables.SquashAny[stretched & 0x7FFF];
    var probability = this.SseProbability(this._sse2, Math.Clamp(stretched, -2048, 2047) + 2048, sseContext + this._matchLength);
    bit = this.CodeBit(bit, probability);
    if (this.TrainMixer(mixer, mixed, bit))
      this.AdvanceStates(1, mapBase, bit);
    this.TrainSse(this._sse2, bit);
    return bit;
  }

  private int CodeSymbolBits(int symbol, int mixerBase, int sseContext, int mapBase) {
    var p = this._inputsStretch;
    var node = 0;
    var code = 0;
    for (var bitIndex = 7; bitIndex >= 0; --bitIndex) {
      var fromMatch = this._matchLength != 0;
      if (fromMatch)
        p[0] = McmTables.Stretch[this._matchModels[this._matchModel + (int)(this._expectedCode >> 31)] >> 18];
      for (var i = fromMatch ? 1 : 0; i < this._inputs; ++i) {
        var slot = this._contexts[i] ^ node;
        this._slots[i] = slot;
        int state = this._table[slot];
        this._states[i] = state;
        p[i] = this._mapStretch[(mapBase + i) * 256 + state];
      }
      var mixer = (mixerBase + node) * this._mixerStride;
      var stretched = this.Mix(mixer);
      var mixed = (int)McmTables.SquashAny[stretched & 0x7FFF];
      int probability;
      if (sseContext != 0)
        probability = this.SseProbability(this._sse1, Math.Clamp(stretched, -2048, 2047) + 2048, sseContext + node);
      else
        probability = mixed;
      var bit = this.CodeBit((symbol >> bitIndex) & 1, probability);
      if (this.TrainMixer(mixer, mixed, bit))
        this.AdvanceStates(fromMatch ? 1 : 0, mapBase, bit);
      if (sseContext != 0)
        this.TrainSse(this._sse1, bit);
      this.MatchBit(bit);
      code = (code << 1) | bit;
      node = bitIndex == 4 ? 15 * ((code & 0xF) + 1) : node < 15 ? 2 * node + 1 + bit : 15 * (node / 15) + 2 * (node % 15) + 1 + bit;
    }
    return code;
  }

  private int Mix(int mixer) {
    var weights = this._mixers;
    var p = this._inputsStretch;
    long total = weights[mixer + this._inputs];
    for (var i = 0; i < this._inputs; ++i)
      total += unchecked(p[i] * weights[mixer + i]);
    return (int)(total >> 17);
  }

  private bool TrainMixer(int mixer, int mixed, int bit) {
    var weights = this._mixers;
    var learnIndex = mixer + this._inputs + 1;
    var learn = weights[learnIndex];
    var error = ((long)(bit << 12) - mixed) * this._mixerRate[learn];
    if (error >= -600 && error <= 600)
      return false;
    var p = this._inputsStretch;
    for (var i = 0; i < this._inputs; ++i)
      weights[mixer + i] = unchecked((int)(weights[mixer + i] + ((error * p[i]) >> 16)));
    weights[mixer + this._inputs] = unchecked((int)(weights[mixer + this._inputs] + (error << 1)));
    if (learn < 255)
      weights[learnIndex] = learn + 1;
    return true;
  }

  private void AdvanceStates(int first, int mapBase, int bit) {
    for (var i = first; i < this._inputs; ++i) {
      var state = this._states[i];
      var map = (mapBase + i) * 256 + state;
      var probability = this._mapProbability[map];
      this._mapStretch[map] = McmTables.Stretch[probability >> 19];
      this._mapProbability[map] = (uint)(probability + (((long)bit << 31) - probability >> this._mapLearn[map]));
      this._table[this._slots[i]] = McmTables.StateNext[state * 2 + bit];
    }
  }

  private int SseProbability(uint[] cells, int q, int context) {
    var index = context * SseCells + (q >> 7);
    var fraction = q & 127;
    this._ssePick = index + (fraction >> 6);
    var p = ((int)(cells[index] >> 19) * (128 - fraction) + (int)(cells[index + 1] >> 19) * fraction) >> 7;
    return p == 0 ? 1 : p;
  }

  private void TrainSse(uint[] cells, int bit) {
    var p = cells[this._ssePick];
    var count = p & 0xFF;
    var rate = 2 + (int)(count >> 5);
    var target = bit != 0 ? 0x7FFFFFFF : 0xFF;
    p = unchecked(p + ((uint)((target - (int)p) >> rate) & 0xFFFFFF00u));
    if (count < 255)
      ++p;
    cells[this._ssePick] = p;
  }

  private void MatchBit(int bit) {
    if (this._matchLength == 0)
      return;
    var expectedBit = (int)(this._expectedCode >> 31);
    var model = this._matchModel + expectedBit;
    this._matchModels[model] += ((bit << 30) - this._matchModels[model]) >> 7;
    if (expectedBit != bit)
      this._matchLength = 0;
    this._expectedCode <<= 1;
  }

  private void UpdateMatch() {
    var last = this._historyPosition - 1;
    this._matchHash ^= this.History(last);
    var high = this._matchHash & ~(uint)this._historyMask;
    ref var slot = ref this._matchTable[this._matchHash & this._matchTableMask];
    if (this._matchLength != 0) {
      if (this._matchLength < MaxMatch)
        ++this._matchLength;
      ++this._matchPosition;
    } else if ((slot & ~(uint)this._historyMask) == high) {
      ulong candidate = slot;
      if (this.Read32(candidate - 4) == this.Read32(last - 4)) {
        this._matchPosition = candidate;
        this._matchLength = 4;
      }
    }
    slot = (uint)(last & this._historyMask) | high;
  }

  /// <summary>Four history bytes starting at the masked position, running into the zero padding at the end.</summary>
  private uint Read32(ulong position) {
    var index = (int)(position & this._historyMask);
    var h = this._history;
    return (uint)(h[index] | h[index + 1] << 8 | h[index + 2] << 16 | h[index + 3] << 24);
  }

  /// <summary>
  /// Whether any byte went through the binary fast path. mcm 0.84's own decoder returns the wrong
  /// byte from that path, so mcm cannot extract a block for which this is true.
  /// </summary>
  public bool UsedFastPath { get; private set; }

  private int CodeFast(int symbol, uint h) {
    this.UsedFastPath = true;
    var matchHash = h;
    for (var order = 3; order <= this._set.MatchOrder; ++order)
      matchHash = HashFunc(this.History(this._historyPosition - (ulong)order), matchHash);
    this._matchHash = matchHash;

    var s0 = Order2Base + (int)(this._lastBytes & 0xFFFF) * 512;
    var s1 = Order1Base + (int)(this._lastBytes & 0xFF) * 512;
    var s2 = 0;
    var table = this._table;
    var ctx = 1;
    var second = false;
    var result = 0;
    var shift = 7;
    for (;;) {
      int a = table[s0 + ctx], b = table[s1 + ctx], c = table[s2 + ctx];
      var index = (FastIndex(a) << 8) | (FastIndex(b) << 4) | FastIndex(c);
      var p = this._fastMix[index] >> 18;
      if (p == 0)
        p = 1;
      if (p == 4096)
        p = 4095;
      var bit = this.CodeBit((symbol >> shift--) & 1, p);
      this._fastMix[index] += ((bit << 30) - this._fastMix[index]) >> 10;
      table[s0 + ctx] = McmTables.StateNext[a * 2 + bit];
      table[s1 + ctx] = McmTables.StateNext[b * 2 + bit];
      table[s2 + ctx] = McmTables.StateNext[c * 2 + bit];
      result = (result << 1) | bit;
      ctx += ctx + bit;
      if ((ctx & 0x10) == 0)
        continue;
      if (second)
        break;
      var offset = 15 + (ctx ^ 0x10) * 15;
      s0 += offset;
      s1 += offset;
      s2 += offset;
      ctx = 1;
      second = true;
    }
    return result;
  }

  private static int FastIndex(int state) => (McmTables.Stretch[McmTables.InitialProbabilities[state]] + 2048) >> 8;

  private void Update(int symbol) {
    this.UpdateWord(symbol);
    this._history[(int)(this._historyPosition & this._historyMask)] = (byte)symbol;
    ++this._historyPosition;
    this._interval = (this._interval << 4) | this._intervalMap[symbol];
    this._interval2 = (this._interval2 << 4) | this._intervalMap2[symbol];
    this._smallInterval = this._smallInterval * 8 + this._smallIntervalMap[symbol];
    this._lastBytes = (this._lastBytes << 8) | (byte)symbol;
    this.UpdateBracket(symbol);
  }

  private void ResetWord() {
    this._wordHash1 = 0x1F20239A;
    this._wordHash2 = 0xBE5FD47A;
    this._wordLength = 0;
  }

  private uint WordHash() {
    var t = unchecked(this._wordHash1 * 15) ^ unchecked(this._wordHash2 * 41);
    t ^= t >> 4;
    return unchecked((uint)t);
  }

  private void UpdateWord(int symbol) {
    if (symbol >= 128 && this._wordLastByte == this._wordEscape)
      this.StepWord(symbol);
    this._wordLastByte = symbol;
    this.StepWord(symbol);
  }

  private void StepWord(int symbol) {
    var code = this._wordCode[symbol];
    if (code >= 0) {
      this._wordHash1 = unchecked(this._wordHash1 * 43 + (ulong)code);
      this._wordHash2 = unchecked(this._wordHash1 * 24);
      if (this._wordLength < 16)
        ++this._wordLength;
    } else if (this._wordLength != 0) {
      this._wordPrevious = BitOperations.RotateLeft(unchecked(this.WordHash() * 21), 14);
      this.ResetWord();
    }
  }

  private uint BracketHash() {
    uint top = this._bracketDepth == 0 ? 0u : this._bracketStack[this._bracketDepth - 1];
    return (((this._bracketLast << 8) | top) << 1) | (this._bracketRun > 0 ? 1u : 0u);
  }

  private void UpdateBracket(int symbol) {
    var s = this._bracketSymbol;
    if (symbol == s[0] || symbol == s[1] || symbol == s[2] || symbol == s[3] || this._bracketSpecial[symbol]) {
      this._bracketRun = 0;
      if (this._bracketDepth >= 256)
        this._bracketDepth = 0;
      this._bracketStack[this._bracketDepth++] = (byte)symbol;
    } else {
      var closer = symbol == s[4] ? 0 : symbol == s[5] ? 1 : symbol == s[6] ? 2 : symbol == s[7] ? 3 : -1;
      if (closer >= 0) {
        this._bracketRun = 0;
        var popped = this._bracketDepth == 0 ? 0 : this._bracketStack[--this._bracketDepth];
        if (popped != s[closer])
          this._bracketDepth = 0;
      } else
        ++this._bracketRun;
    }
    this._bracketLast = (uint)symbol;
  }
}
