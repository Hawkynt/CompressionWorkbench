namespace FileFormat.Kgb;

/// <summary>
/// The bit predictor of KGB Archiver (PAQ6 by Matt Mahoney, as KGB 1.0 runs it), written from
/// docs/KGB-ON-DISK.md: context-mixing over order-0..9, match, record, sparse, analog, word and
/// x86-call models, one or two adaptive mixers and an SSE stage, sized by the memory level 0..9.
/// </summary>
/// <remarks>
/// Encoder and decoder must make exactly the same decisions, including every pseudo-random draw,
/// so the order of operations follows the document step for step. One instance codes one archive.
/// </remarks>
internal sealed class KgbModel {
  private const int ProbabilityScale = 4096;
  private const int SseContexts = 2048, SseBins = 32, SseStep = 1024 / SseBins;

  private readonly int _level;
  private readonly KgbRandom _random = new();
  private readonly uint[] _hashTable = new uint[8 * 256];

  // history
  private readonly byte[] _history;
  private readonly uint _historyMask;
  private uint _pos;
  private int _bit;
  private uint _lowNibble = 1, _highNibble;
  private readonly uint[] _lastSeen = new uint[256 * 4];

  // mixers
  private readonly Mixer _mixer1, _mixer2;

  // models
  private readonly CharModel _char;
  private readonly MatchModel? _match;
  private readonly RecordModel? _record;
  private readonly SparseModel? _sparse;
  private readonly AnalogModel? _analog;
  private readonly WordModel? _word;
  private readonly ExeModel? _exe;

  // SSE
  private readonly ushort[] _stretch = new ushort[ProbabilityScale];
  private readonly byte[] _sseOnes = [], _sseCount = [];
  private int _sseContext;
  private int _sseInput = 512;

  /// <summary>The probability that the next bit is 1, scaled to 0..4095.</summary>
  public int Probability { get; private set; } = ProbabilityScale / 2;

  public KgbModel(int level) {
    if (level is < 0 or > 9)
      throw new ArgumentOutOfRangeException(nameof(level), "KGB memory levels are 0 to 9.");
    this._level = level;
    for (var table = 7; table >= 0; --table)
      for (var v = 0; v < 256; ++v)
        this._hashTable[table * 256 + v] = this._random.Next();

    this._history = new byte[1u << (19 + level - (level >= 6 ? 1 : 0))];
    this._historyMask = (uint)this._history.Length - 1;
    this._history[0] = 1;

    this._mixer1 = new Mixer(this);
    this._mixer2 = new Mixer(this);
    this._char = new CharModel(this);
    if (level >= 2)
      this._match = new MatchModel(this);
    if (level >= 4) {
      this._record = new RecordModel(this);
      this._sparse = new SparseModel(this);
      this._analog = new AnalogModel(this);
      this._word = new WordModel(this);
    }
    if (level >= 3)
      this._exe = new ExeModel(this);

    for (var i = 0; i < ProbabilityScale; ++i) {
      var p = (int)(64 * Math.Log((i + 0.5) / (ProbabilityScale - 0.5 - i)) + 512);
      this._stretch[i] = (ushort)Math.Clamp(p, 0, 1023);
    }
    if (level >= 1) {
      this._sseOnes = new byte[SseContexts * (SseBins + 1)];
      this._sseCount = new byte[SseContexts * (SseBins + 1)];
      var upper = SseBins + 1;
      for (var i = ProbabilityScale - 1; i >= 0; --i) {
        var p = (this._stretch[i] + SseStep / 2) / SseStep;
        var n = Math.Min(254, 1 + ProbabilityScale * ProbabilityScale / ((i + 1) * (ProbabilityScale - i)));
        var ones = (i * n + ProbabilityScale / 2) / ProbabilityScale;
        for (var bin = upper - 1; bin >= p; --bin)
          for (var context = 0; context < SseContexts; ++context) {
            this._sseCount[context * (SseBins + 1) + bin] = (byte)n;
            this._sseOnes[context * (SseBins + 1) + bin] = (byte)ones;
          }
        upper = p;
      }
    }
  }

  #region Shared services

  private uint Draw() => this._random.Next();

  private uint H(uint a) => this._hashTable[a & 255];
  private uint H(uint a, uint b) => this._hashTable[a & 255] + this._hashTable[256 + (b & 255)];
  private uint H(uint a, uint b, uint c) => this.H(a, b) + this._hashTable[512 + (c & 255)];
  private uint H(uint a, uint b, uint c, uint d) => this.H(a, b, c) + this._hashTable[768 + (d & 255)];

  /// <summary>The byte <paramref name="back"/> positions before the partial byte (0: the partial byte with its leading 1).</summary>
  private uint C(int back) => this._history[(this._pos - (uint)back) & this._historyMask];

  /// <summary>The byte at absolute position <paramref name="at"/>.</summary>
  private uint At(uint at) => this._history[at & this._historyMask];

  private uint LastSeen(uint value, int which) => this._lastSeen[value * 4 + which];

  private bool IsText => this.LastSeen(0, 3) < this.LastSeen(32, 3);

  private bool AtByteStart => this._bit == 0;

  private void MixWrite(int n0, int n1) {
    this._mixer1.Write(n0, n1);
    if (this._level >= 5)
      this._mixer2.Write(n0, n1);
  }

  private void MixAdd(int n0, int n1) {
    this._mixer1.Add(n0, n1);
    if (this._level >= 5)
      this._mixer2.Add(n0, n1);
  }

  /// <summary>A counter learns bit <paramref name="y"/>; above state 207 the increment is probabilistic.</summary>
  private void Learn(byte[] table, int index, int y) {
    var s = table[index];
    if (y != 0)
      table[index] = s < 208 || this.Draw() < Promote1[s] ? Next11[s] : Next10[s];
    else
      table[index] = s < 208 || this.Draw() < Promote0[s] ? Next01[s] : Next00[s];
  }

  #endregion

  /// <summary>Learns <paramref name="y"/>, the bit just coded, and sets <see cref="Probability"/> for the next one.</summary>
  public void Update(int y) {
    if (this._level >= 1) {
      var bin = this._sseContext * (SseBins + 1) + this._sseInput / SseStep;
      this.LearnSse(bin, y);
      this.LearnSse(bin + 1, y);
    }

    this._mixer1.Learn(y);
    if (this._level >= 5)
      this._mixer2.Learn(y);

    // history
    ref var partial = ref this._history[this._pos & this._historyMask];
    partial = (byte)(partial + partial + y);
    if (++this._bit == 8) {
      var v = partial;
      this._lastSeen[v * 4 + 3] = this._lastSeen[v * 4 + 2];
      this._lastSeen[v * 4 + 2] = this._lastSeen[v * 4 + 1];
      this._lastSeen[v * 4 + 1] = this._lastSeen[v * 4];
      this._lastSeen[v * 4] = this._pos;
      this._bit = 0;
      ++this._pos;
      this._history[this._pos & this._historyMask] = 1;
    }
    this._lowNibble += this._lowNibble + (uint)y;
    if (this._lowNibble >= 16) {
      this._highNibble = this._lowNibble - 16;
      this._lowNibble = 1;
    }

    // predictions, in input order
    this.MixWrite(1, 1);
    this._char.Predict();
    this._match?.Predict();
    this._record?.Predict();
    this._sparse?.Predict();
    this._analog?.Predict();
    this._word?.Predict();
    this._exe?.Predict();

    var p1 = this._mixer1.Predict((int)(this.C(1) >> 5) + (this.IsText ? 8 : 0));
    var p = p1;
    if (this._level >= 5)
      p = (int)(((uint)p1 + (uint)this._mixer2.Predict((int)((this.C(1) >> 6) + 4 * (this.C(2) >> 6)))) / 2);

    if (this._level >= 1) {
      this._sseContext = (int)((this.C(0) * 4 + this.C(1) / 64) * 2 + (this.IsText ? 1u : 0u));
      this._sseInput = this._stretch[p];
      var weight = (uint)(this._sseInput % SseStep);
      var bin = this._sseContext * (SseBins + 1) + this._sseInput / SseStep;
      var refined = ((uint)this.SseProbability(bin) * (SseStep - weight) + (uint)this.SseProbability(bin + 1) * weight) / SseStep;
      p = (int)((refined * 3 + (uint)p) / 4);
    }
    this.Probability = p;
  }

  private int SseProbability(int bin) => ProbabilityScale * (this._sseOnes[bin] * 64 + 1) / (this._sseCount[bin] * 64 + 2);

  private void LearnSse(int bin, int y) {
    if (y != 0)
      ++this._sseOnes[bin];
    if (++this._sseCount[bin] > 254) {
      this._sseOnes[bin] /= 2;
      this._sseCount[bin] /= 2;
    }
  }

  #region Mixer

  private sealed class Mixer(KgbModel model) {
    private const int Inputs = 64, Sets = 16;
    private readonly uint[] _n0 = new uint[Inputs], _n1 = new uint[Inputs];
    private readonly uint[] _weights = Enumerable.Repeat(1u, Sets * Inputs).ToArray();
    private int _count, _set;

    public void Write(int n0, int n1) {
      this._n0[this._count] = (uint)n0;
      this._n1[this._count] = (uint)n1;
      ++this._count;
    }

    public void Add(int n0, int n1) {
      this._n0[this._count - 1] += (uint)n0;
      this._n1[this._count - 1] += (uint)n1;
    }

    public int Predict(int set) {
      this._set = set;
      uint n0 = 1, n1 = 1;
      for (var j = 0; j < this._count; ++j) {
        var w = this._weights[set * Inputs + j];
        n0 += this._n0[j] * w;
        n1 += this._n1[j] * w;
      }
      int sum = (int)(n0 + n1), ones = (int)n1;
      while (sum > 2_000_000_000 / ProbabilityScale) {
        sum /= 4;
        ones /= 4;
      }
      return (ProbabilityScale - 1) * ones / sum;
    }

    public void Learn(int y) {
      uint s0 = 0, s1 = 0;
      var w = this._set * Inputs;
      for (var i = 0; i < this._count; ++i) {
        s0 += (this._weights[w + i] + 48) * this._n0[i];
        s1 += (this._weights[w + i] + 48) * this._n1[i];
      }
      if (s0 > 0 && s1 > 0) {
        var s = s0 + s1;
        var sy = y != 0 ? s1 : s0;
        var a = (0xFFFFFFFFu / sy + (model.Draw() & 1023)) >> 10;
        var b = (0xFFFFFFFFu / s + (model.Draw() & 1023)) >> 10;
        for (var i = 0; i < this._count; ++i) {
          var ny = y != 0 ? this._n1[i] : this._n0[i];
          var dw = (int)(ny * a - (this._n0[i] + this._n1[i]) * b + (model.Draw() & 255)) >> 8;
          this._weights[w + i] = (uint)Math.Min(65535, Math.Max(1, (int)(this._weights[w + i] + (uint)dw)));
        }
      }
      this._count = 0;
    }
  }

  #endregion

  #region Context map

  /// <summary>
  /// A nibble hash table of counters plus (level 5 and above) a run table, for one context hash
  /// set at byte boundaries; see "Context tables".
  /// </summary>
  private sealed class ContextMap {
    private readonly KgbModel _model;
    private readonly byte[] _table; // 16-byte elements: checksum, 15 counters
    private readonly int _elementBits;
    private readonly byte[]? _runByte, _runCount;
    private readonly int _runBits;
    private readonly int[] _remembered = [-1, -1, -1, -1, -1, -1, -1, -1];
    private uint _hash;
    private int _slot;
    private uint _run;

    public ContextMap(KgbModel model, int sizeBits) {
      this._model = model;
      this._elementBits = sizeBits > 4 ? sizeBits - 4 : 1;
      this._table = new byte[16 << this._elementBits];
      if (model._level >= 5) {
        var runSize = sizeBits - 2;
        this._runBits = runSize > 1 ? runSize - 1 : 1;
        this._runByte = new byte[1 << this._runBits];
        this._runCount = new byte[1 << this._runBits];
      }
    }

    private int Priority(int element) {
      var s = this._table[element * 16 + 1];
      return StateN0[s] + StateN1[s];
    }

    private void Find(uint h) {
      var checksum = (byte)((h >> 24) ^ h);
      var low = (int)((h >> (32 - this._elementBits)) & ~3u);
      var found = -1;
      for (var i = low; i < low + 4; ++i) {
        if (this._table[i * 16] == checksum) {
          found = i;
          break;
        }
        if (this.Priority(i) == 0) {
          this._table[i * 16] = checksum;
          found = i;
          break;
        }
      }
      if (found < 0) {
        var keep = this.Priority(low + 2) < this.Priority(low + 3) ? 2 : 3;
        Array.Copy(this._table, low * 16, this._table, (low + 1) * 16, keep * 16);
        Array.Clear(this._table, low * 16, 16);
        this._table[low * 16] = checksum;
      } else if (found != low) {
        Span<byte> saved = stackalloc byte[16];
        this._table.AsSpan(found * 16, 16).CopyTo(saved);
        Array.Copy(this._table, low * 16, this._table, (low + 1) * 16, (found - low) * 16);
        saved.CopyTo(this._table.AsSpan(low * 16, 16));
      }
      this._slot = low;
    }

    private void Counters() {
      var m = this._model;
      if (m._bit == 4) {
        this._hash ^= m.H(m._highNibble, this._hash);
        this.Find(this._hash);
      }
      var index = this._slot * 16 + (int)m._lowNibble;
      this._remembered[m._bit] = index;
      var s = this._table[index];
      m.MixAdd(StateN0[s], StateN1[s]);
    }

    private void Run() {
      var m = this._model;
      if (this._runByte is null)
        return;
      uint c = this._runByte[this._run];
      if (((c + 256) >> (8 - m._bit)) != m.C(0))
        return;
      if (((c >> (7 - m._bit)) & 1) != 0)
        m.MixAdd(0, this._runCount![this._run]);
      else
        m.MixAdd(this._runCount![this._run], 0);
    }

    public void Write() {
      this._model.MixWrite(0, 0);
      this.Counters();
      this.Run();
    }

    public void Add() {
      this.Counters();
      this.Run();
    }

    public void Update(uint h) {
      var m = this._model;
      if (this._runByte is not null) {
        var c = (byte)m.C(1);
        if (this._runCount![this._run] == 0) {
          this._runCount[this._run] = 1;
          this._runByte[this._run] = c;
        } else if (this._runByte[this._run] == c) {
          if (this._runCount[this._run] < 255)
            ++this._runCount[this._run];
        } else {
          this._runByte[this._run] = c;
          this._runCount[this._run] = 1;
        }
        this._run = h >> (32 - this._runBits);
      }
      var last = m.C(1);
      for (var i = 0; i < 8; ++i)
        if (this._remembered[i] >= 0) {
          m.Learn(this._table, this._remembered[i], (int)((last >> (7 - i)) & 1));
          this._remembered[i] = -1;
        }
      this._hash = h;
      this.Find(h);
    }
  }

  #endregion

  #region Models

  private sealed class CharModel {
    private readonly KgbModel _m;
    private readonly byte[] _order0 = new byte[256], _order1 = new byte[65536];
    private readonly ContextMap[] _maps;
    private readonly uint[] _hashes = new uint[10];
    private int _at0, _at1;

    public CharModel(KgbModel m) {
      this._m = m;
      var l = m._level;
      var big = l >= 6 ? 1 : 0;
      int[] sizes = [l + 15, l + 17, l + 18, l >= 1 ? l + 18 : 0, l >= 3 ? l + 18 : 0, l >= 3 ? l + 18 : 0,
        l >= 5 ? l + 18 - big : 0, l >= 5 ? l + 18 - big : 0];
      this._maps = [.. sizes.Select(s => new ContextMap(m, s))];
    }

    public void Predict() {
      var m = this._m;
      var y = (int)(m.C(m.AtByteStart ? 1 : 0) & 1);
      m.Learn(this._order0, this._at0, y);
      m.Learn(this._order1, this._at1, y);
      var l = m._level;
      if (m.AtByteStart) {
        for (var i = 9; i > 0; --i)
          this._hashes[i] = this._hashes[i - 1] ^ m.H(m.C(1), (uint)i);
        var used = l >= 5 ? 8 : l >= 3 ? 6 : l >= 1 ? 4 : 3;
        for (var k = 0; k < used; ++k)
          this._maps[k].Update(this._hashes[k + 2]);
      }
      this._at0 = (int)m.C(0);
      this._at1 = (int)(m.C(0) + 256 * m.C(1));
      var s0 = this._order0[this._at0];
      m.MixWrite(StateN0[s0], StateN1[s0]);
      var s1 = this._order1[this._at1];
      m.MixWrite(StateN0[s1], StateN1[s1]);
      this._maps[0].Write();
      this._maps[1].Write();
      this._maps[2].Write();
      if (l >= 1)
        this._maps[3].Add();
      if (l >= 3) {
        this._maps[4].Write();
        this._maps[5].Add();
      }
      if (l >= 5) {
        this._maps[6].Write();
        this._maps[7].Add();
      }
    }
  }

  private sealed class MatchModel {
    private const int Slots = 4;
    private readonly KgbModel _m;
    private readonly int _bits;
    private readonly uint[] _index;
    private uint _hashShort, _hashLong;
    private readonly uint[] _begin = new uint[Slots], _end = new uint[Slots];

    public MatchModel(KgbModel m) {
      this._m = m;
      this._bits = 17 + m._level - (m._level >= 6 ? 1 : 0);
      this._index = new uint[1 << this._bits];
    }

    public void Predict() {
      var m = this._m;
      if (m.AtByteStart) {
        var c1 = m.C(1);
        this._hashShort = this._hashShort * 908754512u + c1 + 1;
        this._hashLong = this._hashLong * 91368434u + c1 + 1;
        var h = (this._hashShort >> 28) == 0 ? this._hashLong >> (32 - this._bits) : this._hashShort >> (32 - this._bits);
        for (var i = 0; i < Slots; ++i)
          if (this._end[i] != 0 && c1 == m.At(this._end[i]))
            ++this._end[i];
        for (var i = 0; i < Slots; ++i) {
          if (this._end[i] != 0)
            continue;
          var candidate = this._index[h];
          if (this._end.Contains(candidate))
            break;
          this._end[i] = candidate;
          if (candidate > 0) {
            this._begin[i] = candidate;
            var q = m._pos;
            while (this._begin[i] > 0 && q > 0 && this._begin[i] != q + 1 && m.At(this._begin[i] - 1) == m.At(q - 1)) {
              --this._begin[i];
              --q;
            }
          }
          if (this._end[i] == this._begin[i])
            this._begin[i] = this._end[i] = 0;
          break;
        }
        this._index[h] = m._pos;
      }

      for (var i = 0; i < Slots; ++i)
        if (this._end[i] != 0 && ((m.At(this._end[i]) + 256) >> (8 - m._bit)) != m.C(0))
          this._begin[i] = this._end[i] = 0;

      int n0 = 0, n1 = 0;
      for (var i = 0; i < Slots; ++i) {
        if (this._end[i] == 0)
          continue;
        var length = this._end[i] - this._begin[i];
        var weight = (int)Math.Min(511u, length * length / 4);
        if (((m.At(this._end[i]) >> (7 - m._bit)) & 1) != 0)
          n1 += weight;
        else
          n0 += weight;
      }
      m.MixWrite(n0, n1);
    }
  }

  private sealed class RecordModel {
    private readonly KgbModel _m;
    private readonly ContextMap[] _maps;
    private int _cycleA = 2, _cycleB = 3;

    public RecordModel(KgbModel m) {
      this._m = m;
      var size = 16 + m._level - (m._level >= 6 ? 1 : 0);
      this._maps = [.. Enumerable.Range(0, 5).Select(_ => new ContextMap(m, size))];
    }

    public void Predict() {
      var m = this._m;
      if (m.AtByteStart) {
        var v = m.C(1);
        var d1 = (int)(m.LastSeen(v, 0) - m.LastSeen(v, 1));
        var d2 = (int)(m.LastSeen(v, 1) - m.LastSeen(v, 2));
        var d3 = (int)(m.LastSeen(v, 2) - m.LastSeen(v, 3));
        if (d1 > 1 && d1 == d2 && d2 == d3) {
          if (d1 == this._cycleA)
            (this._cycleA, this._cycleB) = (this._cycleB, this._cycleA);
          else if (d1 != this._cycleB) {
            this._cycleA = this._cycleB;
            this._cycleB = d1;
          }
        }
        var a = Math.Min(this._cycleA, this._cycleB);
        var b = Math.Max(this._cycleA, this._cycleB);
        this._maps[0].Update(m.H(m.C(a), m.C(a * 2), (uint)a));
        this._maps[1].Update(m.H(m.C(1), m.C(a), (uint)a));
        this._maps[2].Update(m.H(m.C(a), m._pos % (uint)a));
        this._maps[3].Update(m.H(m.C(b), m.C(b * 2), (uint)b));
        this._maps[4].Update(m.H(m.C(1), m.C(b), (uint)b));
      }
      foreach (var map in this._maps)
        map.Write();
    }
  }

  private sealed class SparseModel {
    private readonly KgbModel _m;
    private readonly ContextMap[] _maps;

    public SparseModel(KgbModel m) {
      this._m = m;
      var size = m._level + 15 - (m._level >= 6 ? 1 : 0);
      this._maps = [.. Enumerable.Range(0, 9).Select(_ => new ContextMap(m, size))];
    }

    public void Predict() {
      var m = this._m;
      if (m.AtByteStart) {
        this._maps[0].Update(m.H(m.C(1), m.C(3)));
        this._maps[1].Update(m.H(m.C(1), m.C(4)));
        this._maps[2].Update(m.H(m.C(1), m.C(5)));
        this._maps[3].Update(m.H(m.C(1), m.C(6)));
        this._maps[4].Update(m.H(m.C(2), m.C(3)));
        this._maps[5].Update(m.H(m.C(2), m.C(4)));
        this._maps[6].Update(m.H(m.C(3), m.C(4)));
        var gap = (uint)Math.Min(255, (int)(m._pos - m.LastSeen(m.C(1), 2)));
        this._maps[7].Update(m.H(m.C(1), gap));
        this._maps[8].Update(m.H(m.C(1), m.C(2), gap));
      }
      foreach (var map in this._maps)
        map.Write();
    }
  }

  private sealed class AnalogModel {
    private readonly KgbModel _m;
    private readonly ContextMap[] _maps;
    private uint _phase3;

    public AnalogModel(KgbModel m) {
      this._m = m;
      this._maps = [.. Enumerable.Range(0, 7).Select(_ => new ContextMap(m, m._level + 13))];
    }

    public void Predict() {
      var m = this._m;
      if (m.AtByteStart) {
        if (++this._phase3 == 3)
          this._phase3 = 0;
        this._maps[0].Update(m.H(m.C(2) / 4, m.C(4) / 4, m._pos % 2));
        this._maps[1].Update(m.H(m.C(2) / 16, m.C(4) / 16, m._pos % 2));
        this._maps[2].Update(m.H(m.C(2) / 4, m.C(4) / 4, m.C(8) / 4, m._pos % 4));
        this._maps[3].Update(m.H(m.C(3), m.C(6) / 4, this._phase3));
        this._maps[4].Update(m.H(m.C(1) / 16, m.C(2) / 16, m.C(3) / 4, this._phase3));
        this._maps[5].Update(m.H(m.C(1) / 2, m.C(2) / 8, m.C(3) / 32));
        this._maps[6].Update(m.H(m.C(216), m.C(432)));
      }
      this._maps[0].Write();
      this._maps[1].Add();
      this._maps[2].Add();
      this._maps[3].Write();
      this._maps[4].Add();
      this._maps[5].Write();
      this._maps[6].Write();
    }
  }

  private sealed class WordModel {
    private readonly KgbModel _m;
    private readonly ContextMap[] _maps;
    private readonly uint[] _spaced = new uint[3], _letters = new uint[3];

    public WordModel(KgbModel m) {
      this._m = m;
      var size = m._level + 17 - (m._level >= 6 ? 1 : 0);
      this._maps = [.. Enumerable.Range(0, 6).Select(_ => new ContextMap(m, size))];
    }

    public void Predict() {
      var m = this._m;
      if (m.AtByteStart) {
        var c = m.C(1);
        if (c > 32)
          this._spaced[0] ^= m.H(this._spaced[0], c);
        else if (this._spaced[0] != 0) {
          this._spaced[2] = this._spaced[1];
          this._spaced[1] = this._spaced[0];
          this._spaced[0] = 0;
        }
        if (c is >= 65 and <= 90 or >= 97 and <= 122 or >= 192) {
          var lower = c is >= 65 and <= 90 ? c + 32 : c;
          this._letters[0] ^= m.H(this._letters[0], lower, 1);
        } else {
          this._letters[2] = this._letters[1];
          this._letters[1] = this._letters[0];
          this._letters[0] = 0;
        }
        this._maps[0].Update(this._spaced[0]);
        this._maps[1].Update(this._spaced[1] + this._spaced[0]);
        this._maps[2].Update(this._spaced[2] + this._spaced[0]);
        this._maps[3].Update(this._letters[0]);
        this._maps[4].Update(this._letters[1] + this._letters[0]);
        this._maps[5].Update(this._letters[2] + this._letters[0]);
      }
      foreach (var map in this._maps)
        map.Write();
    }
  }

  private sealed class ExeModel(KgbModel m) {
    private readonly uint[] _address = new uint[256];
    private readonly byte[] _count = new byte[256];

    public void Predict() {
      if (m.AtByteStart && m.C(5) == 0xE8 && (m.C(1) == 0 || m.C(1) == 0xFF)) {
        var a = m.C(4) + (m.C(3) << 8) + (m.C(2) << 16) + (m.C(1) << 24) + m._pos - 5;
        var i = (int)(a & 0xFF);
        if (this._address[i] == a && this._count[i] < 255)
          ++this._count[i];
        else {
          this._address[i] = a;
          this._count[i] = 1;
        }
      }
      int n0 = 0, n1 = 0;
      var bit = m._bit;
      if (m.C(4) == 0xE8) {
        var i = (int)((m.C(3) + m._pos - 4) & 0xFF);
        if (this._count[i] > 0) {
          var r = this._address[i] - m._pos + 4;
          var check = (((r & 0xFF000000) >> 8) + 0x1000000) >> (24 - bit);
          if (m.C(0) == check && m.C(1) == ((r >> 16) & 0xFF))
            Assign(ref n0, ref n1, (r >> (31 - bit)) & 1, this._count[i] * 16);
        }
      }
      if (m.C(3) == 0xE8) {
        var i = (int)((m.C(2) + m._pos - 3) & 0xFF);
        if (this._count[i] > 0) {
          var r = this._address[i] - m._pos + 3;
          var check = ((r & 0xFF0000) + 0x1000000) >> (24 - bit);
          if (m.C(0) == check && m.C(1) == ((r >> 8) & 0xFF))
            Assign(ref n0, ref n1, (r >> (23 - bit)) & 1, this._count[i] * 4);
        }
      } else if (m.C(2) == 0xE8) {
        var i = (int)((m.C(1) + m._pos - 2) & 0xFF);
        if (this._count[i] > 0) {
          var r = this._address[i] - m._pos + 2;
          var check = ((r & 0xFF00) + 0x10000) >> (16 - bit);
          if (m.C(0) == check)
            Assign(ref n0, ref n1, (r >> (15 - bit)) & 1, this._count[i]);
        }
      }
      m.MixWrite(n0, n1);
    }

    private static void Assign(ref int n0, ref int n1, uint bit, int weight) {
      if (bit != 0)
        n1 = weight;
      else
        n0 = weight;
    }
  }

  #endregion

  #region Counter states (copied from the format's state table)

  private static readonly ushort[] StateN0 = [
    0, 0, 4, 0, 1, 8, 0, 1, 2, 12, 0, 1, 1, 3, 16, 0,
    1, 1, 1, 4, 20, 0, 1, 1, 1, 2, 5, 24, 0, 1, 1, 1,
    1, 2, 6, 28, 0, 1, 1, 1, 1, 1, 3, 7, 32, 0, 1, 1,
    1, 1, 1, 2, 3, 8, 36, 0, 1, 1, 1, 2, 4, 9, 40, 0,
    1, 1, 1, 2, 4, 10, 44, 0, 1, 1, 1, 3, 5, 11, 48, 0,
    1, 1, 1, 3, 5, 12, 52, 0, 1, 1, 6, 13, 56, 0, 1, 1,
    6, 14, 60, 0, 1, 1, 7, 15, 64, 0, 1, 1, 7, 16, 68, 0,
    1, 17, 72, 0, 1, 1, 8, 18, 76, 0, 1, 1, 9, 19, 80, 0,
    1, 1, 9, 20, 84, 0, 1, 1, 10, 21, 88, 0, 1, 1, 10, 22,
    92, 0, 1, 23, 96, 0, 1, 24, 100, 0, 1, 25, 104, 0, 1, 26,
    108, 0, 1, 27, 112, 0, 1, 28, 116, 0, 1, 29, 120, 0, 1, 30,
    124, 0, 1, 31, 128, 0, 1, 32, 132, 0, 1, 33, 136, 0, 1, 34,
    140, 0, 1, 35, 144, 0, 1, 36, 148, 0, 1, 37, 152, 0, 1, 38,
    156, 0, 1, 39, 160, 1, 40, 0, 176, 1, 44, 0, 192, 1, 48, 0,
    224, 1, 56, 0, 256, 1, 64, 0, 384, 1, 96, 0, 512, 1, 128, 0,
    640, 1, 160, 0, 768, 1, 192, 0, 896, 1, 224, 0, 1020, 1, 255,
  ];
  private static readonly ushort[] StateN1 = [
    0, 4, 0, 8, 1, 0, 12, 2, 1, 0, 16, 3, 1, 1, 0, 20,
    4, 1, 1, 1, 0, 24, 5, 2, 1, 1, 1, 0, 28, 6, 2, 1,
    1, 1, 1, 0, 32, 7, 3, 1, 1, 1, 1, 1, 0, 36, 8, 3,
    2, 1, 1, 1, 1, 1, 0, 40, 9, 4, 2, 1, 1, 1, 0, 44,
    10, 4, 2, 1, 1, 1, 0, 48, 11, 5, 3, 1, 1, 1, 0, 52,
    12, 5, 3, 1, 1, 1, 0, 56, 13, 6, 1, 1, 0, 60, 14, 6,
    1, 1, 0, 64, 15, 7, 1, 1, 0, 68, 16, 7, 1, 1, 0, 72,
    17, 1, 0, 76, 18, 8, 1, 1, 0, 80, 19, 9, 1, 1, 0, 84,
    20, 9, 1, 1, 0, 88, 21, 10, 1, 1, 0, 92, 22, 10, 1, 1,
    0, 96, 23, 1, 0, 100, 24, 1, 0, 104, 25, 1, 0, 108, 26, 1,
    0, 112, 27, 1, 0, 116, 28, 1, 0, 120, 29, 1, 0, 124, 30, 1,
    0, 128, 31, 1, 0, 132, 32, 1, 0, 136, 33, 1, 0, 140, 34, 1,
    0, 144, 35, 1, 0, 148, 36, 1, 0, 152, 37, 1, 0, 156, 38, 1,
    0, 160, 39, 1, 0, 40, 1, 176, 0, 44, 1, 192, 0, 48, 1, 224,
    0, 56, 1, 256, 0, 64, 1, 384, 0, 96, 1, 512, 0, 128, 1, 640,
    0, 160, 1, 768, 0, 192, 1, 896, 0, 224, 1, 1020, 0, 255, 1,
  ];
  private static readonly byte[] Next00 = [
    0, 1, 2, 1, 4, 5, 1, 4, 8, 9, 3, 4, 8, 13, 14, 3,
    7, 8, 13, 19, 20, 6, 7, 12, 13, 19, 26, 27, 6, 11, 12, 18,
    19, 26, 34, 35, 10, 11, 17, 18, 25, 26, 34, 43, 44, 10, 16, 17,
    24, 25, 33, 34, 43, 53, 54, 15, 16, 23, 24, 43, 53, 61, 62, 15,
    22, 23, 31, 53, 61, 69, 70, 21, 22, 30, 31, 61, 69, 77, 78, 21,
    29, 30, 39, 69, 77, 85, 86, 28, 29, 38, 85, 91, 92, 28, 37, 38,
    91, 97, 98, 36, 37, 47, 97, 103, 104, 36, 46, 47, 103, 109, 110, 45,
    46, 113, 114, 45, 56, 57, 113, 119, 120, 55, 56, 65, 119, 125, 126, 55,
    64, 65, 125, 131, 132, 63, 64, 73, 131, 137, 138, 63, 72, 73, 137, 143,
    144, 71, 72, 147, 148, 63, 80, 151, 152, 63, 72, 155, 156, 63, 72, 159,
    160, 63, 72, 163, 164, 63, 72, 167, 168, 63, 72, 171, 172, 63, 72, 175,
    176, 63, 72, 179, 180, 63, 72, 183, 184, 63, 72, 187, 188, 63, 72, 191,
    192, 71, 72, 195, 196, 71, 80, 199, 200, 71, 80, 203, 204, 71, 80, 207,
    208, 71, 80, 211, 212, 80, 214, 71, 216, 80, 218, 71, 220, 80, 222, 79,
    224, 88, 226, 87, 228, 94, 230, 93, 232, 100, 234, 105, 236, 112, 238, 111,
    240, 116, 242, 115, 244, 122, 246, 121, 248, 128, 250, 127, 252, 134, 254,
  ];
  private static readonly byte[] Next01 = [
    2, 4, 5, 4, 8, 9, 4, 8, 13, 14, 7, 8, 13, 19, 20, 7,
    12, 13, 19, 26, 27, 11, 12, 18, 19, 26, 34, 35, 11, 17, 18, 25,
    26, 34, 43, 44, 16, 17, 24, 25, 33, 34, 43, 53, 54, 16, 23, 24,
    32, 33, 42, 43, 53, 61, 62, 22, 23, 31, 32, 53, 61, 69, 70, 22,
    30, 31, 40, 61, 69, 77, 78, 29, 30, 39, 40, 69, 77, 85, 86, 29,
    38, 39, 49, 77, 85, 91, 92, 37, 38, 48, 91, 97, 98, 37, 47, 48,
    97, 103, 104, 46, 47, 58, 103, 109, 110, 46, 57, 58, 109, 113, 114, 56,
    57, 119, 120, 56, 65, 66, 119, 125, 126, 64, 65, 74, 125, 131, 132, 64,
    73, 74, 131, 137, 138, 72, 73, 82, 137, 143, 144, 72, 81, 82, 143, 147,
    148, 80, 81, 151, 152, 72, 89, 155, 156, 72, 81, 159, 160, 72, 81, 163,
    164, 72, 81, 167, 168, 72, 81, 171, 172, 72, 81, 175, 176, 72, 81, 179,
    180, 72, 81, 183, 184, 72, 81, 187, 188, 72, 81, 191, 192, 72, 81, 195,
    196, 80, 81, 199, 200, 80, 89, 203, 204, 80, 89, 207, 208, 80, 89, 211,
    212, 80, 89, 214, 216, 89, 218, 80, 220, 89, 222, 80, 224, 89, 226, 88,
    228, 95, 230, 94, 232, 101, 234, 100, 236, 107, 238, 112, 240, 117, 242, 116,
    244, 123, 246, 122, 248, 129, 250, 128, 252, 135, 254, 134, 252, 141, 254,
  ];
  private static readonly byte[] Next10 = [
    0, 1, 2, 3, 4, 2, 6, 7, 4, 2, 10, 11, 7, 4, 5, 15,
    16, 11, 7, 8, 5, 21, 22, 16, 11, 12, 8, 9, 28, 29, 22, 16,
    17, 12, 13, 9, 36, 37, 29, 22, 23, 17, 18, 13, 14, 45, 46, 37,
    29, 30, 23, 24, 18, 19, 14, 55, 56, 46, 37, 24, 25, 19, 20, 63,
    64, 56, 46, 32, 25, 26, 20, 71, 72, 64, 56, 32, 33, 26, 27, 79,
    80, 72, 64, 41, 33, 34, 27, 87, 88, 80, 42, 34, 35, 93, 94, 88,
    42, 43, 35, 99, 100, 94, 52, 43, 44, 105, 106, 100, 52, 53, 44, 111,
    112, 53, 54, 115, 116, 112, 60, 61, 54, 121, 122, 116, 68, 61, 62, 127,
    128, 122, 68, 69, 62, 133, 134, 128, 76, 69, 70, 139, 140, 134, 76, 77,
    70, 145, 146, 77, 78, 149, 150, 85, 70, 153, 154, 77, 70, 157, 158, 77,
    70, 161, 162, 77, 70, 165, 166, 77, 70, 169, 170, 77, 70, 173, 174, 77,
    70, 177, 178, 77, 70, 181, 182, 77, 70, 185, 186, 77, 70, 189, 190, 77,
    70, 193, 194, 77, 78, 197, 198, 85, 78, 201, 202, 85, 78, 205, 206, 85,
    78, 209, 210, 85, 78, 213, 85, 215, 78, 217, 85, 219, 78, 221, 85, 223,
    86, 225, 91, 227, 92, 229, 97, 231, 98, 233, 103, 235, 110, 237, 113, 239,
    114, 241, 119, 243, 120, 245, 125, 247, 126, 249, 131, 251, 132, 253, 137,
  ];
  private static readonly byte[] Next11 = [
    1, 3, 4, 6, 7, 4, 10, 11, 7, 4, 15, 16, 11, 7, 8, 21,
    22, 16, 11, 12, 8, 28, 29, 22, 16, 17, 12, 13, 36, 37, 29, 22,
    23, 17, 18, 13, 45, 46, 37, 29, 30, 23, 24, 18, 19, 55, 56, 46,
    37, 38, 30, 31, 24, 25, 19, 63, 64, 56, 46, 31, 32, 25, 26, 71,
    72, 64, 56, 40, 32, 33, 26, 79, 80, 72, 64, 40, 41, 33, 34, 87,
    88, 80, 72, 50, 41, 42, 34, 93, 94, 88, 51, 42, 43, 99, 100, 94,
    51, 52, 43, 105, 106, 100, 59, 52, 53, 111, 112, 106, 59, 60, 53, 115,
    116, 60, 61, 121, 122, 116, 67, 68, 61, 127, 128, 122, 75, 68, 69, 133,
    134, 128, 75, 76, 69, 139, 140, 134, 83, 76, 77, 145, 146, 140, 83, 84,
    77, 149, 150, 84, 85, 153, 154, 90, 77, 157, 158, 84, 77, 161, 162, 84,
    77, 165, 166, 84, 77, 169, 170, 84, 77, 173, 174, 84, 77, 177, 178, 84,
    77, 181, 182, 84, 77, 185, 186, 84, 77, 189, 190, 84, 77, 193, 194, 84,
    77, 197, 198, 84, 85, 201, 202, 90, 85, 205, 206, 90, 85, 209, 210, 90,
    85, 215, 213, 90, 85, 217, 90, 219, 85, 221, 90, 223, 85, 225, 90, 227,
    91, 229, 96, 231, 97, 233, 102, 235, 103, 237, 108, 239, 113, 241, 118, 243,
    119, 245, 124, 247, 125, 249, 130, 251, 131, 253, 136, 251, 137, 253, 142,
  ];
  private static readonly uint[] Promote0 = [
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 1073741823u, 4294967295u, 1073741823u, 4294967295u,
    1073741823u, 4294967295u, 1073741823u, 4294967295u, 536870911u, 4294967295u, 536870911u, 4294967295u,
    536870911u, 4294967295u, 536870911u, 4294967295u, 134217727u, 4294967295u, 134217727u, 4294967295u,
    134217727u, 4294967295u, 134217727u, 4294967295u, 134217727u, 4294967295u, 134217727u, 4294967295u,
    134217727u, 4294967295u, 134217727u, 4294967295u, 134217727u, 4294967295u, 134217727u, 4294967295u,
    138547332u, 4294967295u, 138547332u, 4294967295u, 0u, 4294967295u, 0u,
  ];
  private static readonly uint[] Promote1 = [
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u, 4294967295u,
    4294967295u, 1073741823u, 4294967295u, 4294967295u, 4294967295u, 1073741823u, 4294967295u, 1073741823u,
    4294967295u, 1073741823u, 4294967295u, 536870911u, 4294967295u, 536870911u, 4294967295u, 536870911u,
    4294967295u, 536870911u, 4294967295u, 134217727u, 4294967295u, 134217727u, 4294967295u, 134217727u,
    4294967295u, 134217727u, 4294967295u, 134217727u, 4294967295u, 134217727u, 4294967295u, 134217727u,
    4294967295u, 134217727u, 4294967295u, 134217727u, 4294967295u, 134217727u, 4294967295u, 138547332u,
    4294967295u, 138547332u, 4294967295u, 0u, 4294967295u, 0u, 4294967295u,
  ];
  #endregion
}

/// <summary>The lagged-Fibonacci generator of 55 words both sides draw from in lock step.</summary>
internal sealed class KgbRandom {
  private readonly uint[] _t = new uint[55];
  private int _i;

  public KgbRandom() {
    this._t[0] = 123456789;
    this._t[1] = 987654321;
    for (var j = 2; j < 55; ++j)
      this._t[j] = this._t[j - 1] * 11 + this._t[j - 2] * 19 / 16;
  }

  public uint Next() {
    if (++this._i == 55)
      this._i = 0;
    return this._i >= 24 ? this._t[this._i] ^= this._t[this._i - 24] : this._t[this._i] ^= this._t[this._i + 31];
  }
}

/// <summary>The binary arithmetic coder of KGB/PAQ6 over <see cref="KgbModel"/>; see "Arithmetic coder".</summary>
internal sealed class KgbCoder {
  private readonly KgbModel _model;
  private readonly Stream _stream;
  private uint _x1, _x2 = 0xFFFFFFFF, _x;

  private KgbCoder(KgbModel model, Stream stream) {
    this._model = model;
    this._stream = stream;
  }

  public static KgbCoder ForDecoding(int level, Stream stream) {
    var coder = new KgbCoder(new KgbModel(level), stream);
    for (var i = 0; i < 4; ++i)
      coder._x = (coder._x << 8) | coder.NextByte();
    return coder;
  }

  public static KgbCoder ForEncoding(int level, Stream stream) => new(new KgbModel(level), stream);

  private uint NextByte() {
    var b = this._stream.ReadByte();
    return b < 0 ? 0u : (uint)b;
  }

  private uint Middle() {
    var p = (uint)this._model.Probability;
    var d = this._x2 - this._x1;
    return d >= 0x4000000 ? this._x1 + (d >> 12) * p
      : d >= 0x100000 ? this._x1 + (((d >> 6) * p) >> 6)
      : this._x1 + ((d * p) >> 12);
  }

  public int DecodeBit() {
    var mid = this.Middle();
    var y = this._x <= mid ? 1 : 0;
    if (y != 0)
      this._x2 = mid;
    else
      this._x1 = mid + 1;
    this._model.Update(y);
    while (((this._x1 ^ this._x2) & 0xFF000000) == 0) {
      this._x1 <<= 8;
      this._x2 = (this._x2 << 8) | 255;
      this._x = (this._x << 8) | this.NextByte();
    }
    return y;
  }

  public byte DecodeByte() {
    var c = 0;
    for (var i = 0; i < 8; ++i)
      c = c + c + this.DecodeBit();
    return (byte)c;
  }

  public void EncodeBit(int y) {
    var mid = this.Middle();
    if (y != 0)
      this._x2 = mid;
    else
      this._x1 = mid + 1;
    this._model.Update(y);
    while (((this._x1 ^ this._x2) & 0xFF000000) == 0) {
      this._stream.WriteByte((byte)(this._x2 >> 24));
      this._x1 <<= 8;
      this._x2 = (this._x2 << 8) | 255;
    }
  }

  public void EncodeByte(byte c) {
    for (var i = 7; i >= 0; --i)
      this.EncodeBit((c >> i) & 1);
  }

  public void Flush() {
    while (((this._x1 ^ this._x2) & 0xFF000000) == 0) {
      this._stream.WriteByte((byte)(this._x2 >> 24));
      this._x1 <<= 8;
      this._x2 = (this._x2 << 8) | 255;
    }
    this._stream.WriteByte((byte)(this._x2 >> 24));
  }
}
