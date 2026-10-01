namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// The encoder-side statistical model of libcsc: packet kinds selected by a 6-bit history of the last
/// three packet types, order-1 literals, LZMA-like length and distance slots, plus the coders for the
/// non-LZ block types (order-1 literal run, delta RLE, stored). It also prices candidate packets for
/// the optimal parser.
/// </summary>
/// <remarks>
/// Packet kinds and their flag bits (probabilities <c>state * 3 + n</c>):
/// <list type="bullet">
///   <item><c>0</c> — literal byte (order-1 context: the previous byte).</item>
///   <item><c>1 1</c> — match: length, then distance slot + extra bits.</item>
///   <item><c>1 0 0</c> — one byte from the most recent distance.</item>
///   <item><c>1 0 1 ii</c> — match reusing one of the four most recent distances.</item>
/// </list>
/// Lengths are coded minus two: 0-7, 8-15 and 16-142 in three trees; 143 escapes to a run of
/// "another 143" flags followed by the remainder.
/// </remarks>
internal sealed class CscModel(CscEntropyEncoder coder) {
  /// <summary>Price (in 1/128 bit) of a bit whose probability is <c>index * 8 + 4</c> out of 4096.</summary>
  private static readonly uint[] _bitPrice = BuildBitPrices();

  private readonly uint[] _state = InitProbabilities(64 * 3);
  private readonly uint[] _literal = InitProbabilities(256 * 256);
  private readonly uint[] _repeatDistance = InitProbabilities(64 * 3);
  private readonly uint[] _distanceSlot = InitProbabilities(CscConstants.DistanceSlotTableSize);
  private readonly uint[] _lengthSlot = InitProbabilities(2);
  private readonly uint[] _lengthLow = InitProbabilities(8);
  private readonly uint[] _lengthMid = InitProbabilities(8);
  private readonly uint[] _lengthHigh = InitProbabilities(128);
  private readonly uint[] _distanceExtra = InitProbabilities(29 * 16);
  private readonly uint[] _lengthPrice = new uint[32];
  private uint[]? _delta;
  private uint _longLength = CscConstants.ProbabilityInit;
  private uint _runFlag = CscConstants.ProbabilityInit;
  private uint _context;
  private uint _lengthPriceCountdown;

  /// <summary>History of the last three packet kinds (2 bits each).</summary>
  public uint State { get; private set; }

  /// <summary>Sets the literal context to the byte just produced by a match.</summary>
  public void SetLiteralContext(uint value) => this._context = value;

  /// <summary>Codes a literal byte.</summary>
  public void EncodeLiteral(uint value) {
    coder.EncodeBit(0, ref this._state[this.State * 3]);
    this.State = (this.State * 4) & 0x3F;
    var offset = (int)this._context * 256;
    this._context = value;
    this.EncodeByteTree(this._literal, offset, value);
  }

  /// <summary>Codes a one-byte match at the most recent distance.</summary>
  public void EncodeRep0Len1() {
    var s = (int)this.State * 3;
    coder.EncodeBit(1, ref this._state[s]);
    coder.EncodeBit(0, ref this._state[s + 1]);
    coder.EncodeBit(0, ref this._state[s + 2]);
    this._context = 0;
    this.State = (this.State * 4 + 2) & 0x3F;
  }

  /// <summary>Codes a match at repeat distance <paramref name="index"/> (0-3) of encoded length <paramref name="length"/>.</summary>
  public void EncodeRepeatMatch(uint index, uint length) {
    var s = (int)this.State * 3;
    coder.EncodeBit(1, ref this._state[s]);
    coder.EncodeBit(0, ref this._state[s + 1]);
    coder.EncodeBit(1, ref this._state[s + 2]);

    var high = (index >> 1) & 1;
    coder.EncodeBit(high, ref this._repeatDistance[s]);
    coder.EncodeBit(index & 1, ref this._repeatDistance[s + 1 + (int)high]);

    this.EncodeLength(length);
    this.State = (this.State * 4 + 3) & 0x3F;
  }

  /// <summary>Codes a match of encoded distance <paramref name="distance"/> and encoded length <paramref name="length"/>.</summary>
  public void EncodeMatch(uint distance, uint length) {
    var s = (int)this.State * 3;
    coder.EncodeBit(1, ref this._state[s]);
    coder.EncodeBit(1, ref this._state[s + 1]);
    this.EncodeLength(length);

    var slotBits = CscConstants.DistanceSlotBits(length);
    var offset = CscConstants.DistanceSlotOffset(length);
    var slot = DistanceSlot(distance);

    var c = slot | (1u << slotBits);
    do {
      coder.EncodeBit((c >> (slotBits - 1)) & 1, ref this._distanceSlot[offset + (int)(c >> slotBits)]);
      c <<= 1;
    } while (c < 1u << (slotBits * 2));

    if (slot > 2) {
      var extraBits = (int)slot - 2;
      var extra = distance - (1u << extraBits) - 1;
      if (extraBits > 4)
        coder.EncodeDirect(extra >> 4, extraBits - 4);

      // The low four bits go through a reversed tree, LSB first.
      var tree = (extraBits - 1) * 16;
      c = CscConstants.Reverse4[(int)(extra & 0x0F)] | 0x10u;
      do {
        coder.EncodeBit((c >> 3) & 1, ref this._distanceExtra[tree + (int)(c >> 4)]);
        c <<= 1;
      } while (c < 1 << 8);
    }

    this.State = (this.State * 4 + 1) & 0x3F;
  }

  /// <summary>Marks the end of an LZ block.</summary>
  public void EncodeEndOfBlock() => this.EncodeMatch(CscConstants.EndOfBlockDistance, 0);

  /// <summary>Writes an unsigned integer to the bit coder as a 5-bit magnitude slot plus its bits.</summary>
  public void EncodeInt(uint value) {
    var slot = value == 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount(value);
    coder.EncodeDirect((uint)slot, 5);
    if (slot == 0)
      coder.EncodeDirect(value, 1);
    else
      coder.EncodeDirect(value - (1u << slot), slot);
  }

  /// <summary>Codes a high-entropy block as order-1 literals that share the LZ literal model.</summary>
  public void CompressLiterals(ReadOnlySpan<byte> source) {
    this.EncodeInt((uint)source.Length);
    foreach (var b in source) {
      var offset = (int)this._context * 256;
      this._context = b;
      this.EncodeByteTree(this._literal, offset, b);
    }
  }

  /// <summary>Stores a block as raw bytes on the bit coder.</summary>
  public void CompressBad(ReadOnlySpan<byte> source) {
    this.EncodeInt((uint)source.Length);
    foreach (var b in source)
      coder.EncodeDirect(b, 8);
  }

  /// <summary>Codes a delta-filtered block: order-1 bytes with runs of more than ten repeats.</summary>
  public void CompressRle(ReadOnlySpan<byte> source) {
    this.EncodeInt((uint)source.Length);
    var delta = this._delta ??= InitProbabilities(256 * 256);

    var size = source.Length;
    uint context = 0;
    for (var i = 0; i < size;) {
      if (i > 0 && size - i > 3 && source[i - 1] == source[i] && source[i] == source[i + 1] && source[i] == source[i + 2]) {
        var j = i + 3;
        var length = 3u;
        while (j < size && source[j] == source[j - 1]) {
          ++length;
          ++j;
        }

        if (length > 10) {
          context = source[j - 1];
          coder.EncodeBit(1, ref this._runFlag);
          this.EncodeLength(length - 11);
          i = j;
          continue;
        }
      }

      coder.EncodeBit(0, ref this._runFlag);
      this.EncodeByteTree(delta, (int)context * 256, source[i]);
      context = source[i];
      ++i;
    }
  }

  /// <summary>Price of coding <paramref name="value"/> as a literal from <paramref name="state"/> and <paramref name="context"/>.</summary>
  public uint GetLiteralPrice(uint state, uint context, uint value) {
    var price = Price(0, this._state[state * 3]);
    var offset = (int)context * 256;
    var c = value | 0x100;
    do {
      price += Price((c >> 7) & 1, this._literal[offset + (int)(c >> 8)]);
      c <<= 1;
    } while (c < 0x10000);
    return price;
  }

  /// <summary>Price of the packet flags of a one-byte rep0 match.</summary>
  public uint GetRep0Len1Price(uint state) {
    var s = state * 3;
    return Price(1, this._state[s]) + Price(0, this._state[s + 1]) + Price(0, this._state[s + 2]);
  }

  /// <summary>Price of the packet flags and index of a repeat-distance match.</summary>
  public uint GetRepeatDistancePrice(uint state, uint index) {
    var s = state * 3;
    var high = (index >> 1) & 1;
    return Price(1, this._state[s]) + Price(0, this._state[s + 1]) + Price(1, this._state[s + 2])
      + Price(high, this._repeatDistance[s]) + Price(index & 1, this._repeatDistance[s + 1 + high]);
  }

  /// <summary>Estimated price of the packet flags and distance of a plain match.</summary>
  public uint GetMatchDistancePrice(uint state, uint distance) {
    var s = state * 3;
    var price = Price(1, this._state[s]) + Price(1, this._state[s + 1]);
    var slot = DistanceSlot(distance);
    // A rough estimate is enough here: a slot costs about four bits plus its extra bits.
    return price + (slot > 2 ? slot + 2 : 2) * 128;
  }

  /// <summary>Price of an encoded match length; refreshed from the live model every 4096 queries.</summary>
  public uint GetMatchLengthPrice(uint length) {
    if (length >= 32)
      return 128 * 6;

    if (this._lengthPriceCountdown-- == 0)
      this.RebuildLengthPrices();
    return this._lengthPrice[length];
  }

  private void EncodeLength(uint length) {
    if (length >= CscConstants.LongLengthEscape) {
      this.EncodeShortLength(CscConstants.LongLengthEscape);
      length -= CscConstants.LongLengthEscape;
      while (length >= CscConstants.LongLengthEscape) {
        length -= CscConstants.LongLengthEscape;
        coder.EncodeBit(0, ref this._longLength);
      }

      coder.EncodeBit(1, ref this._longLength);
    }

    this.EncodeShortLength(length);
  }

  private void EncodeShortLength(uint length) {
    if (length < 16) {
      uint[] tree;
      if (length < 8) {
        coder.EncodeBit(0, ref this._lengthSlot[0]);
        tree = this._lengthLow;
      } else {
        coder.EncodeBit(1, ref this._lengthSlot[0]);
        coder.EncodeBit(0, ref this._lengthSlot[1]);
        length -= 8;
        tree = this._lengthMid;
      }

      var c = length | 0x08;
      do {
        coder.EncodeBit((c >> 2) & 1, ref tree[(int)(c >> 3)]);
        c <<= 1;
      } while (c < 0x40);
    } else {
      coder.EncodeBit(1, ref this._lengthSlot[0]);
      coder.EncodeBit(1, ref this._lengthSlot[1]);
      var c = (length - 16) | 0x80;
      do {
        coder.EncodeBit((c >> 6) & 1, ref this._lengthHigh[(int)(c >> 7)]);
        c <<= 1;
      } while (c < 0x4000);
    }
  }

  private void RebuildLengthPrices() {
    for (var i = 0u; i < 32; ++i) {
      var length = i;
      uint price;
      uint c;
      if (length < 16) {
        uint[] tree;
        if (length < 8) {
          price = Price(0, this._lengthSlot[0]);
          tree = this._lengthLow;
        } else {
          price = Price(1, this._lengthSlot[0]) + Price(0, this._lengthSlot[1]);
          length -= 8;
          tree = this._lengthMid;
        }

        c = length | 0x08;
        do {
          price += Price((c >> 2) & 1, tree[(int)(c >> 3)]);
          c <<= 1;
        } while (c < 0x40);
      } else {
        price = Price(1, this._lengthSlot[0]) + Price(1, this._lengthSlot[1]);
        c = (length - 16) | 0x80;
        do {
          price += Price((c >> 6) & 1, this._lengthHigh[(int)(c >> 7)]);
          c <<= 1;
        } while (c < 0x4000);
      }

      this._lengthPrice[i] = price;
    }

    this._lengthPriceCountdown = 4096;
  }

  private void EncodeByteTree(uint[] probabilities, int offset, uint value) {
    var c = value | 0x100;
    do {
      coder.EncodeBit((c >> 7) & 1, ref probabilities[offset + (int)(c >> 8)]);
      c <<= 1;
    } while (c < 0x10000);
  }

  /// <summary>Distance slot by binary search over <see cref="CscConstants.DistanceBase"/>, exactly as libcsc searches it.</summary>
  private static uint DistanceSlot(uint distance) {
    var table = CscConstants.DistanceBase;
    uint l = 0, r = 32;
    while (l + 1 < r) {
      var mid = l + (r - l) / 2;
      if (table[(int)mid] > distance)
        r = mid;
      else if (table[(int)mid] < distance)
        l = mid;
      else
        l = r = mid;
    }

    return l;
  }

  private static uint Price(uint bit, uint probability) => _bitPrice[(bit != 0 ? probability : 4096 - probability) >> 3];

  private static uint[] InitProbabilities(int count) {
    var result = new uint[count];
    Array.Fill(result, CscConstants.ProbabilityInit);
    return result;
  }

  private static uint[] BuildBitPrices() {
    // libcsc evaluates this in single precision (logf) before the double division by log(0.5).
    var result = new uint[4096 >> 3];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (uint)(128f * MathF.Log((float)(i * 8 + 4) / 4096) / Math.Log(0.5));
    return result;
  }
}
