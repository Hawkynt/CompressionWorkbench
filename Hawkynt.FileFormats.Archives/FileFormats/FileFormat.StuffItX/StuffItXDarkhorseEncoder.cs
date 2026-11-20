namespace FileFormat.StuffItX;

/// <summary>
/// Encodes Darkhorse (method 2) streams that <see cref="StuffItXDarkhorse"/> decodes: the same
/// adaptive 12-bit bit models and decisions, driven by a greedy hash-chain match finder that also
/// tries the four remembered distances. The range coder is the decoder's carry-free reading of a
/// coder that propagates carries, so it writes the usual leading zero byte.
/// </summary>
internal sealed class StuffItXDarkhorseEncoder {
  private const int EndLength = 0x111;
  private const int MinMatch = 3;
  private const int MaxMatch = EndLength - 1;
  private const uint Half = 0x800;
  /// <summary>The window this encoder declares (2^20 bytes, the smallest Darkhorse allows) and stays within.</summary>
  public const int WindowBits = 20;
  private const int Window = 1 << WindowBits;
  private const int HashBits = 16;
  private const int ChainLimit = 64;

  private readonly BitEncoder _coder = new();
  private readonly uint[] _flag = Filled(4);
  private uint _flag2 = Half;
  private readonly uint[] _literal = Filled(16 * 256);
  private readonly uint[] _literalGuided = Filled(16 * 256 * 2);
  private uint _recency1 = Half, _recency2 = Half, _recency3 = Half;
  private readonly uint[] _recency = Filled(4);
  private uint _lengthKind = Half;
  private readonly uint[] _shortLength = Filled(4 * 16);
  private readonly uint[] _longLength = Filled(256);
  private readonly uint[] _distanceSlot = Filled(4 * 64);
  private readonly uint[] _distance = Filled(10 * 32);
  private readonly uint[] _distanceLow = Filled(16);
  private readonly int[] _distances = new int[4];

  private static uint[] Filled(int count) {
    var a = new uint[count];
    Array.Fill(a, Half);
    return a;
  }

  /// <summary>The Darkhorse payload of <paramref name="data"/>: window-size byte, coded data and the end code.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    var encoder = new StuffItXDarkhorseEncoder();
    encoder.Run(data);
    var body = encoder._coder.Finish();
    var result = new byte[body.Length + 1];
    result[0] = WindowBits;
    body.CopyTo(result, 1);
    return result;
  }

  private void Run(ReadOnlySpan<byte> data) {
    var head = new int[1 << HashBits];
    Array.Fill(head, -1);
    var previous = new int[data.Length];
    var next = -1; // the decoder's guide byte for the literal right after a match
    var pos = 0;

    void Insert(ReadOnlySpan<byte> d, int at, int[] h, int[] prev) {
      if (at + MinMatch > d.Length) return;
      var key = Hash(d, at);
      prev[at] = h[key];
      h[key] = at;
    }

    while (pos < data.Length) {
      // longest repeat of a remembered distance
      int repIndex = -1, repLength = 0;
      for (var k = 0; k < 4; ++k) {
        if (this._distances[k] >= Window) continue; // stay inside the declared window
        var length = MatchLength(data, pos, pos - this._distances[k] - 1);
        if (length > repLength) { repLength = length; repIndex = k; }
      }

      // longest new match
      int newLength = 0, newDistance = 0;
      if (pos + MinMatch <= data.Length) {
        var chain = ChainLimit;
        for (var candidate = head[Hash(data, pos)]; candidate >= 0 && pos - candidate <= Window && chain-- > 0; candidate = previous[candidate]) {
          var length = MatchLength(data, pos, candidate);
          if (length > newLength) {
            newLength = length;
            newDistance = pos - candidate - 1;
            if (length == MaxMatch) break;
          }
        }
      }

      int step;
      if (repLength >= 2 && repLength + 1 >= newLength) {
        this.Bit(ref this._flag[pos & 3], 1);
        this.Bit(ref this._flag2, 1);
        var distance = this._distances[repIndex];
        this.Recency(pos & 3, repIndex);
        this.Remember(repIndex, distance);
        this.Length(pos & 3, repLength - 2);
        next = Guide(data, pos, distance, repLength);
        step = repLength;
      } else if (newLength >= MinMatch) {
        this.Bit(ref this._flag[pos & 3], 1);
        this.Bit(ref this._flag2, 0);
        this.Length(pos & 3, newLength - 2);
        this.Distance(newLength, newDistance);
        this.Remember(3, newDistance);
        next = Guide(data, pos, newDistance, newLength);
        step = newLength;
      } else {
        this.Bit(ref this._flag[pos & 3], 0);
        this.Literal(pos > 0 ? data[pos - 1] : 0, next, data[pos]);
        next = -1;
        step = 1;
      }

      for (var i = 0; i < step; ++i)
        Insert(data, pos + i, head, previous);
      pos += step;
    }

    // the end code: a new match of length 0x111
    this.Bit(ref this._flag[pos & 3], 1);
    this.Bit(ref this._flag2, 0);
    this.Length(pos & 3, EndLength - 2);
  }

  private static int Hash(ReadOnlySpan<byte> d, int at) => ((d[at] << 8) ^ (d[at + 1] << 4) ^ d[at + 2]) & ((1 << HashBits) - 1);

  private static int MatchLength(ReadOnlySpan<byte> d, int pos, int from) {
    if (from < 0) return 0;
    var limit = Math.Min(MaxMatch, d.Length - pos);
    var length = 0;
    while (length < limit && d[from + length] == d[pos + length]) ++length;
    return length;
  }

  /// <summary>The decoder's guide after a copy: the byte following the copied run in the window (0 before the start).</summary>
  private static int Guide(ReadOnlySpan<byte> d, int pos, int distance, int length) {
    var index = pos - distance - 1 + length % (distance + 1);
    return index >= 0 ? d[index] : 0;
  }

  private void Bit(ref uint weight, int bit) {
    this._coder.Encode(weight, bit);
    if (bit == 0) weight += (0x1000 - weight) >> 5;
    else weight -= weight >> 5;
  }

  private void Literal(int previous, int guess, int value) {
    var context = (previous >> 4) * 256;
    var node = 1;
    var bitIndex = 7;
    if (guess >= 0) {
      while (bitIndex >= 0) {
        var guessBit = (guess >> 7) & 1;
        var bit = (value >> bitIndex--) & 1;
        this.Bit(ref this._literalGuided[(context + node) * 2 + guessBit], bit);
        node = (node << 1) | bit;
        if (bit != guessBit) break;
        guess <<= 1;
      }
    }
    while (bitIndex >= 0) {
      var bit = (value >> bitIndex--) & 1;
      this.Bit(ref this._literal[context + node], bit);
      node = (node << 1) | bit;
    }
  }

  private void Length(int index, int value) {
    if (value < 16) {
      this.Bit(ref this._lengthKind, 0);
      this.Symbol(this._shortLength, index * 16, 4, value);
    } else {
      this.Bit(ref this._lengthKind, 1);
      this.Symbol(this._longLength, 0, 8, value - 16);
    }
  }

  private void Distance(int length, int distance) {
    var slot = Slot(distance);
    this.Symbol(this._distanceSlot, Math.Min(length - 2, 3) * 64, 6, slot);
    if (slot < 4) return;
    var extra = distance - DistanceBase(slot);
    var bits = DistanceBits(slot);
    if (slot < 14) {
      this.Symbol(this._distance, (slot - 4) * 32, bits, extra);
      return;
    }
    for (var i = bits - 1; i >= 4; --i)
      this._coder.EncodeDirect((extra >> i) & 1);
    this.Symbol(this._distanceLow, 0, 4, extra & 15);
  }

  // slot s ≥ 4 covers [base, base + 2^bits) with bits = s/2 − 1 and base = (2 | s&1) << bits
  private static int DistanceBits(int slot) => slot < 4 ? 0 : slot / 2 - 1;
  private static int DistanceBase(int slot) => slot < 4 ? slot : (2 | (slot & 1)) << DistanceBits(slot);

  private static int Slot(int distance) {
    if (distance < 4) return distance;
    var top = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)distance);
    return top * 2 + ((distance >> (top - 1)) & 1);
  }

  private void Recency(int index, int which) {
    if (which == 0) {
      this.Bit(ref this._recency1, 0);
      this.Bit(ref this._recency[index], 1); // 0 here would be the one-byte repeat
      return;
    }
    this.Bit(ref this._recency1, 1);
    if (which == 1) {
      this.Bit(ref this._recency2, 0);
      return;
    }
    this.Bit(ref this._recency2, 1);
    this.Bit(ref this._recency3, which == 2 ? 0 : 1);
  }

  private void Symbol(uint[] weights, int baseIndex, int bits, int value) {
    var node = 1;
    for (var i = bits - 1; i >= 0; --i) {
      var bit = (value >> i) & 1;
      this.Bit(ref weights[baseIndex + node], bit);
      node = (node << 1) | bit;
    }
  }

  private void Remember(int oldIndex, int distance) {
    for (var i = oldIndex; i > 0; --i) this._distances[i] = this._distances[i - 1];
    this._distances[0] = distance;
  }

  /// <summary>A binary range coder with carry propagation, whose output the carry-free decoder reads.</summary>
  private sealed class BitEncoder {
    private readonly MemoryStream _out = new();
    private ulong _low;
    private uint _range = 0xFFFFFFFF;
    private byte _cache;
    private long _cacheSize = 1;

    public void Encode(uint weight, int bit) {
      var bound = (this._range >> 12) * weight;
      if (bit == 0)
        this._range = bound;
      else {
        this._low += bound;
        this._range -= bound;
      }
      this.Normalize();
    }

    public void EncodeDirect(int bit) {
      this._range >>= 1;
      if (bit != 0)
        this._low += this._range;
      this.Normalize();
    }

    private void Normalize() {
      while (this._range < 1u << 24) {
        this._range <<= 8;
        this.ShiftLow();
      }
    }

    private void ShiftLow() {
      if (this._low < 0xFF000000UL || this._low >= 1UL << 32) {
        var carry = (byte)(this._low >> 32);
        var temp = this._cache;
        do {
          this._out.WriteByte((byte)(temp + carry));
          temp = 0xFF;
        } while (--this._cacheSize != 0);
        this._cache = (byte)(this._low >> 24);
      }
      ++this._cacheSize;
      this._low = (this._low & 0x00FFFFFF) << 8;
    }

    public byte[] Finish() {
      for (var i = 0; i < 5; ++i)
        this.ShiftLow();
      return this._out.ToArray();
    }
  }
}
