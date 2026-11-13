namespace FileFormat.Uharc;

/// <summary>
/// The binary-stream predictor (docs/UHARC-ON-DISK.md, "PPM", Literals step 2): eight sparse
/// contexts, each remembering one predicted byte with hit and miss counts; the most
/// trustworthy one proposes a byte that a single binary decision accepts or rejects.
/// </summary>
internal sealed partial class UharcPpmDecoder {

  private static readonly int[] LevelOffset = [0x000000, 0x040000, 0x0C0000, 0x140000, 0x180000, 0x1C0000, 0x200000, 0x3C0000];

  private readonly byte[] _levels = new byte[0x400000];
  private readonly byte[] _ringCounts = new byte[0x80];
  private readonly byte[] _ringConfidence = new byte[256];
  private readonly byte[] _ringPredicted = new byte[256];
  private readonly uint[] _ringContexts = new uint[8];

  private struct RingChoice {
    public int Level;
    public byte Predicted;
    public byte Flags;
    public byte Misses;
    public byte Hits;
  }

  private RingChoice _ringBest;
  private int _ringPointer = -1;
  private bool _ringSkip;
  private int _ringLastLevel;
  private bool _ringStrideLevel;
  private bool _ringReady;
  private bool _ringRan;
  private bool _ringArmed;

  private void ResetRing(bool full) {
    Array.Clear(this._levels);
    Array.Fill(this._ringCounts, (byte)1);
    Array.Clear(this._ringConfidence);
    Array.Fill(this._ringPredicted, (byte)0xFF);
    if (!full)
      return;

    this._ringPointer = -1;
    this._ringBest = new() { Level = 8 };
    this._ringLastLevel = 0;
  }

  private int LevelBase(int level) => LevelOffset[level] + (int)this._ringContexts[level] * 4;

  private void HalveRankOnMiss() {
    if (this._stride > 1)
      this._rankSmall[this._rankSmallPointer] >>= 1;
  }

  /// <summary>Picks the level to trust for the next byte; false when none qualifies.</summary>
  private bool PrepareRing() {
    var r0 = this._roll0;
    var r1 = this._roll1;
    uint b0 = r0 & 0xFF, b1 = (r0 >> 8) & 0xFF, b2 = (r0 >> 16) & 0xFF, b3 = r0 >> 24;
    var c3 = r1 >> 24;
    uint depth = (r0 & 0xFFFF0000) == 0xFFFF0000 ? 0x10000u
      : (r0 & 0xFFFF) == 0xFFFF ? 0x20000u
      : (r0 & 0xFF000000) == 0xFF000000 ? 0x30000u
      : (r0 & 0xFF0000) == 0xFF0000 ? 0x40000u
      : (r0 & 0xFF00) == 0xFF00 ? 0x50000u
      : (r0 & 0xFF) == 0xFF ? 0x60000u
      : 0u;

    this._ringContexts[0] = (b3 << 8) + c3;
    this._ringContexts[1] = (r0 >> 8) & 0xFFFF;
    this._ringContexts[2] = (r0 >> 16) & 0xFFFF;
    this._ringContexts[3] = (b2 << 8) + b0;
    this._ringContexts[4] = (b3 << 8) + b1;
    this._ringContexts[5] = (b3 << 8) + b0;
    this._ringContexts[6] = (r1 & 0xFFFF) + depth;

    // Level 7: the difference of the bytes one and two strides back, which predicts their extrapolation.
    uint phase = 0, near = 0, far = 0;
    var levelsOff = false;
    if (this._strideLast != 0) {
      near = this._window[(this._position - this._strideLast) & this._mask];
      far = this._window[(this._position - 2 * this._strideLast) & this._mask];
      if (this._strideLast == this._recordStride) {
        // Record file: the phase is whether the bytes two and three back are 09 00 (complemented
        // F6 FF), and no level is trusted right after a 01.
        phase = (r0 >> 16) == 0xF6FF ? 0x100u : 0u;
        levelsOff = (r0 & 0xFF) == 1;
      } else
        phase = ((this._symbols % this._strideLast) << 8) & 0xFFFF;
    }

    this._ringContexts[7] = (phase + ((near - far) & 0xFF)) & 0xFFFF;
    this._levels[this.LevelBase(7) + 2] = (byte)(2 * near - far);
    if (b0 == 0xFF) {
      this._ringContexts[1] += 0x10000;
      this._ringContexts[2] += 0x10000;
    }

    this._ringPointer = -1;
    this._ringReady = false;
    this._ringStrideLevel = false;
    uint bestScore = 0;
    var best = 8;
    for (var level = levelsOff ? 8 : 0; level < 8; ++level) {
      var b = this.LevelBase(level);
      uint misses = this._levels[b], hits = this._levels[b + 1], flags = this._levels[b + 3];
      if (misses == 0 || flags >= 0x80)
        continue;
      var score = (hits << 8) * flags / misses;
      if (score > bestScore) {
        bestScore = score;
        best = level;
      }
    }

    this._ringBest.Level = best;
    if (best >= 8) {
      this.HalveRankOnMiss();
      this._ringSkip = true;
      return false;
    }

    var bb = this.LevelBase(best);
    this._ringBest.Predicted = this._levels[bb + 2];
    this._ringBest.Flags = this._levels[bb + 3];
    var m = this._levels[bb];
    var h = this._levels[bb + 1];
    this._ringBest.Misses = m;
    this._ringBest.Hits = h;
    if (h <= m && (this._ringBest.Flags & 0x7F) <= 1) {
      this.HalveRankOnMiss();
      this._ringSkip = true;
      return false;
    }

    this._ringStrideLevel = this._strideLast > 1 && best == 7;
    this._ringLastLevel = best;
    this._ringReady = true;
    this._ringSkip = !(m * 5 <= h * 3 || (this._ringBest.Flags & 0x7F) > 2);
    if (m == 2 && h is >= 3 and <= 6)
      this._ringPointer = ((h << 3) - 0x18 + best) * 2;
    else if (m == 3 && h is >= 4 and <= 7)
      this._ringPointer = ((h << 3) - 0x20 + best) * 2 + 0x40;
    return true;
  }

  private bool DecodeRing(ReadOnlySpan<uint> weights, ReadOnlySpan<byte> guesses) {
    uint m0, m1;
    var flagWeight = (uint)UharcPpmTables.ConfidenceWeight[(this._ringBest.Flags >> 3) & 0xF];
    if (this._ringPointer < 0) {
      m0 = this._ringBest.Misses;
      m1 = this._ringBest.Hits + flagWeight;
    } else {
      m0 = this._ringCounts[this._ringPointer];
      m1 = this._ringCounts[this._ringPointer + 1] + flagWeight;
    }

    for (var i = 0; i < 3; ++i)
      if (this._ringBest.Predicted == guesses[i])
        m1 += weights[i];
      else
        m0 += weights[i];

    var total = m0 + m1;
    if (total == 0)
      return false;

    var count = this._coder.GetCount(total);
    if (count < m0) {
      this._coder.Consume(0, m0);
      return false;
    }

    this._coder.Consume(m0, m1);
    return true;
  }

  private void CountRingPointer(byte value, byte guess) {
    var p = this._ringPointer;
    ++this._ringCounts[p + (value == guess ? 1 : 0)];
    if (this._ringCounts[p + 1] >= 0xFE || this._ringCounts[p] >= 0xFE) {
      this._ringCounts[p] = (byte)((this._ringCounts[p] + 1) >> 1);
      this._ringCounts[p + 1] = (byte)((this._ringCounts[p + 1] + 1) >> 1);
    }
  }

  private void UpdateRing(byte value) {
    for (var level = 0; level < 8; ++level) {
      var b = this.LevelBase(level);
      if (this._levels[b] == 0) {
        this._levels[b] = 2;
        this._levels[b + 1] = 2;
        this._levels[b + 2] = value;
        this._levels[b + 3] = 0;
        continue;
      }

      if (level == this._ringLastLevel) {
        uint misses = this._levels[b], hits = this._levels[b + 1];
        var guess = this._levels[b + 2];
        if (this._ringRan && misses + hits != 0) {
          uint key;
          if (value == guess) {
            this._wpKernel = false;
            key = (hits << 14) / (misses + hits);
          } else
            key = (misses << 14) / (misses + hits);
          if (key < 0x4000)
            this._wpCost += Cost[key];
        }

        if (this._ringPointer >= 0)
          this.CountRingPointer(value, guess);
      }

      var flags = (byte)(this._levels[b + 3] & 0x7F);
      this._levels[b + 3] = flags;
      var predicted = this._levels[b + 2];
      var m = this._levels[b];
      var h = this._levels[b + 1];
      if (value == predicted) {
        if (h >= 0xFE) {
          m = (byte)((m + 1) >> 1);
          h = (byte)((h + 1) >> 1);
        }

        ++h;
        this._levels[b] = m;
        this._levels[b + 1] = h;
        if (flags < 0x7F)
          this._levels[b + 3] = (byte)(flags + 1);
      } else {
        if (m >= 0xFE) {
          m = (byte)((m + 1) >> 1);
          h = (byte)((h + 1) >> 1);
        }

        ++m;
        this._levels[b] = m;
        this._levels[b + 1] = h;
        if (flags < 2) {
          this._levels[b + 2] = value;
          this._levels[b + 3] = 0x80;
        } else
          this._levels[b + 3] = (byte)((flags >> 1) + 0x80);
      }
    }
  }

  private void CountRingByte(byte context, byte value) {
    if (value == this._ringPredicted[context]) {
      if (this._ringConfidence[context] < 0xF)
        ++this._ringConfidence[context];
    } else {
      this._ringPredicted[context] = value;
      this._ringConfidence[context] = 0;
    }
  }
}
