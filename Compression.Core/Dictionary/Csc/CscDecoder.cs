using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// Decodes a libcsc stream (after its property header): a sequence of typed blocks — LZ77 with
/// optional E8/E9 or word filters, order-1 literal runs, delta-RLE tables and stored data — grouped
/// into segments that each restart both entropy coders, closed by an end-of-stream block.
/// </summary>
internal sealed class CscDecoder {
  private const uint TopValue = 1u << 24;

  private readonly CscBlockReader _reader;
  private readonly uint _rawBlockSize;

  // Range coder (input side).
  private byte[] _rangeBlock = [];
  private int _rangePos;
  private uint _range;
  private uint _code;

  // Bit coder (input side).
  private byte[] _bitBlock = [];
  private int _bitPos;
  private uint _bitValue;
  private int _bitCount;

  // Model — the same tables as CscModel, initialised once per stream.
  private readonly uint[] _state = InitProbabilities(64 * 3);
  private readonly uint[] _literal = InitProbabilities(256 * 256);
  private readonly uint[] _repeatDistance = InitProbabilities(64 * 3);
  private readonly uint[] _distanceSlot = InitProbabilities(CscConstants.DistanceSlotTableSize);
  private readonly uint[] _lengthSlot = InitProbabilities(2);
  private readonly uint[] _lengthLow = InitProbabilities(8);
  private readonly uint[] _lengthMid = InitProbabilities(8);
  private readonly uint[] _lengthHigh = InitProbabilities(128);
  private readonly uint[] _distanceExtra = InitProbabilities(29 * 16);
  private uint[]? _delta;
  private uint _longLength = CscConstants.ProbabilityInit;
  private uint _runFlag = CscConstants.ProbabilityInit;
  private uint _modelState;
  private uint _context;

  // LZ window.
  private readonly byte[] _window;
  private readonly uint _windowSize;
  private uint _windowPos;
  private readonly uint[] _repeatDistances = new uint[4];

  /// <summary>Observes every decoded block as (type, decoded length); lets tests prove which coder paths a stream takes.</summary>
  public Action<uint, int>? BlockDecoded { get; init; }

  public CscDecoder(Stream input, CscStreamProperties properties) {
    properties.Validate();
    this._reader = new(input, properties.CscBlockSize);
    this._rawBlockSize = properties.RawBlockSize;
    this._windowSize = properties.DictionarySize;
    this._window = new byte[this._windowSize + 8];
    this.StartSegment();
  }

  /// <summary>Decodes every block and writes the result to <paramref name="output"/>.</summary>
  public void DecodeTo(Stream output) {
    var buffer = new byte[this._rawBlockSize];
    for (;;) {
      var size = this.DecodeBlock(buffer);
      if (size < 0)
        return;
      output.Write(buffer, 0, size);
    }
  }

  /// <summary>Decodes one block into <paramref name="destination"/>.</summary>
  /// <returns>The decoded length, or -1 at the end-of-stream marker.</returns>
  private int DecodeBlock(byte[] destination) {
    var type = this.DecodeInt();
    int size;
    switch (type) {
      case CscConstants.TypeNormal:
        size = this.DecodeLz(destination);
        break;
      case CscConstants.TypeExecutable:
        size = this.DecodeLz(destination);
        CscFilters.InverseE89(destination.AsSpan(0, size));
        break;
      case CscConstants.TypeEnglishText:
        // The encoder records the block size first; the LZ block itself determines the length.
        this.DecodeInt();
        size = this.DecodeLz(destination);
        CscFilters.InverseDictionary(destination.AsSpan(0, size));
        break;
      case CscConstants.TypeBad:
        size = this.DecodeStored(destination);
        this.CopyToWindow(destination.AsSpan(0, size));
        break;
      case CscConstants.TypeEntropy:
        size = this.DecodeLiterals(destination);
        this.CopyToWindow(destination.AsSpan(0, size));
        break;
      case CscConstants.TypeEndOfStream:
        this.BlockDecoded?.Invoke(type, 0);
        return -1;
      case >= CscConstants.TypeDelta and < CscConstants.TypeDelta + CscConstants.DeltaChannelLayouts:
        size = this.DecodeRle(destination);
        CscFilters.InverseDelta(destination.AsSpan(0, size), CscConstants.DeltaChannels[(int)(type - CscConstants.TypeDelta)]);
        this.CopyToWindow(destination.AsSpan(0, size));
        break;
      default:
        throw new InvalidDataException($"Unknown CSC block type {type}.");
    }

    this.BlockDecoded?.Invoke(type, size);

    // 1 closes the segment: both coders restart on fresh blocks.
    if (this.DecodeInt() == 1)
      this.StartSegment();
    return size;
  }

  private void StartSegment() {
    this._rangeBlock = this._reader.ReadRangeCoderBlock();
    this._rangePos = 0;
    this._bitBlock = this._reader.ReadBitCoderBlock();
    this._bitPos = 0;
    this._bitValue = 0;
    this._bitCount = 0;

    // The first range-coder byte is the encoder's empty carry cache.
    this.NextRangeByte();
    this._code = 0;
    for (var i = 0; i < 4; ++i)
      this._code = (this._code << 8) | this.NextRangeByte();
    this._range = 0xFFFFFFFF;
  }

  private int DecodeLz(byte[] destination) {
    var limit = this._rawBlockSize;
    var window = this._window;
    var copiedSize = 0u;
    var copiedWindowPos = this._windowPos;
    var reps = this._repeatDistances;
    uint i = 0;

    for (;;) {
      var s = (int)this._modelState * 3;
      if (this.DecodeBit(ref this._state[s]) == 0) {
        if (i >= limit)
          throw new InvalidDataException("CSC block decodes past its raw block size.");
        window[this._windowPos++] = (byte)this.DecodeLiteral();
        ++i;
      } else {
        uint distance, length;
        if (this.DecodeBit(ref this._state[s + 1]) != 0) {
          this.DecodeMatch(out distance, out length);
          if (length == 0 && distance == CscConstants.EndOfBlockDistance)
            break;

          ++distance;
          length += 2;
          reps[3] = reps[2];
          reps[2] = reps[1];
          reps[1] = reps[0];
          reps[0] = distance;
        } else if (this.DecodeBit(ref this._state[s + 2]) == 0) {
          // One byte from the most recent distance.
          this._modelState = (this._modelState * 4 + 2) & 0x3F;
          distance = reps[0];
          length = 1;
        } else {
          var index = this.DecodeRepeatIndex();
          length = this.DecodeLength() + 2;
          this._modelState = (this._modelState * 4 + 3) & 0x3F;
          distance = reps[index];
          for (var j = (int)index; j > 0; --j)
            reps[j] = reps[j - 1];
          reps[0] = distance;
        }

        // Copies never wrap: the encoder only matches within the window's linear extent.
        var copyPos = this._windowPos >= distance ? this._windowPos - distance : this._windowPos + this._windowSize - distance;
        if (distance == 0 || copyPos >= this._windowSize || copyPos + length > this._windowSize
            || (ulong)length + i > limit || this._windowPos + length > this._windowSize)
          throw new InvalidDataException("CSC match reaches outside the window.");

        CopyForward(window, (int)copyPos, (int)this._windowPos, (int)length);
        i += length;
        this._windowPos += length;
        this._context = window[this._windowPos - 1];
      }

      if (this._windowPos == this._windowSize) {
        this._windowPos = 0;
        window.AsSpan((int)copiedWindowPos, (int)(i - copiedSize)).CopyTo(destination.AsSpan((int)copiedSize));
        copiedWindowPos = 0;
        copiedSize = i;
      }
    }

    window.AsSpan((int)copiedWindowPos, (int)(i - copiedSize)).CopyTo(destination.AsSpan((int)copiedSize));
    return (int)i;
  }

  /// <summary>Byte-wise forward copy; overlapping source and destination repeat the pattern.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void CopyForward(byte[] window, int source, int target, int length) {
    if (target - source >= length) {
      window.AsSpan(source, length).CopyTo(window.AsSpan(target));
      return;
    }

    for (var k = 0; k < length; ++k)
      window[target + k] = window[source + k];
  }

  private uint DecodeLiteral() {
    var offset = (int)this._context * 256;
    var c = this.DecodeByteTree(this._literal, offset);
    this._context = c;
    this._modelState = (this._modelState * 4) & 0x3F;
    return c;
  }

  private uint DecodeRepeatIndex() {
    var s = (int)this._modelState * 3;
    var high = this.DecodeBit(ref this._repeatDistance[s]);
    var low = this.DecodeBit(ref this._repeatDistance[s + 1 + (int)high]);
    return (high << 1) | low;
  }

  private void DecodeMatch(out uint distance, out uint length) {
    length = this.DecodeLength();
    var slotBits = CscConstants.DistanceSlotBits(length);
    var offset = CscConstants.DistanceSlotOffset(length);
    uint i = 1;
    do
      i = (i << 1) | this.DecodeBit(ref this._distanceSlot[offset + (int)i]);
    while (i < 1u << slotBits);

    var slot = i & ((1u << slotBits) - 1);
    if (slot <= 2)
      distance = slot;
    else {
      var extraBits = (int)slot - 2;
      var high = extraBits > 4 ? this.DecodeDirect(extraBits - 4) : 0;
      var tree = (extraBits - 1) * 16;
      i = 1;
      do
        i = (i << 1) | this.DecodeBit(ref this._distanceExtra[tree + (int)i]);
      while (i < 0x10);
      distance = CscConstants.DistanceBase[(int)slot] + (high << 4) + CscConstants.Reverse4[(int)(i & 0x0F)];
    }

    this._modelState = (this._modelState * 4 + 1) & 0x3F;
  }

  private uint DecodeShortLength() {
    uint[] tree;
    uint lengthBase;
    if (this.DecodeBit(ref this._lengthSlot[0]) == 0) {
      tree = this._lengthLow;
      lengthBase = 0;
    } else if (this.DecodeBit(ref this._lengthSlot[1]) == 0) {
      tree = this._lengthMid;
      lengthBase = 8;
    } else {
      uint j = 1;
      do
        j = (j << 1) | this.DecodeBit(ref this._lengthHigh[(int)j]);
      while (j < 0x80);
      return 16 + (j & 0x7F);
    }

    uint i = 1;
    do
      i = (i << 1) | this.DecodeBit(ref tree[(int)i]);
    while (i < 0x08);
    return lengthBase + (i & 0x07);
  }

  private uint DecodeLength() {
    var length = this.DecodeShortLength();
    if (length != CscConstants.LongLengthEscape)
      return length;

    while (this.DecodeBit(ref this._longLength) == 0)
      length += CscConstants.LongLengthEscape;
    return length + this.DecodeShortLength();
  }

  private int DecodeStored(byte[] destination) {
    var size = this.DecodeBlockSize();
    for (var i = 0; i < size; ++i)
      destination[i] = (byte)this.DecodeDirect(8);
    return size;
  }

  private int DecodeLiterals(byte[] destination) {
    var size = this.DecodeBlockSize();
    for (var i = 0; i < size; ++i) {
      var c = this.DecodeByteTree(this._literal, (int)this._context * 256);
      this._context = c;
      destination[i] = (byte)c;
    }

    return size;
  }

  private int DecodeRle(byte[] destination) {
    var delta = this._delta ??= InitProbabilities(256 * 256);
    var size = this.DecodeBlockSize();
    uint context = 0;
    for (var i = 0; i < size;) {
      if (this.DecodeBit(ref this._runFlag) == 0) {
        var c = (byte)this.DecodeByteTree(delta, (int)context * 256);
        destination[i++] = c;
        context = c;
        continue;
      }

      if (i == 0)
        throw new InvalidDataException("CSC run-length block starts with a run.");

      var length = this.DecodeLength() + 11;
      var value = destination[i - 1];
      var run = (int)Math.Min(length, (uint)(size - i));
      destination.AsSpan(i, run).Fill(value);
      i += run;
      context = value;
    }

    return size;
  }

  private int DecodeBlockSize() {
    var size = this.DecodeInt();
    if (size > this._rawBlockSize)
      throw new InvalidDataException($"CSC block of {size} bytes exceeds the raw block size {this._rawBlockSize}.");
    return (int)size;
  }

  private void CopyToWindow(ReadOnlySpan<byte> data) {
    for (var i = 0; i < data.Length;) {
      var chunk = (int)Math.Min(Math.Min(this._windowSize - this._windowPos, (uint)(data.Length - i)), CscConstants.MinBlockSize);
      data.Slice(i, chunk).CopyTo(this._window.AsSpan((int)this._windowPos));
      this._windowPos += (uint)chunk;
      if (this._windowPos >= this._windowSize)
        this._windowPos = 0;
      i += chunk;
    }
  }

  private uint DecodeInt() {
    var slot = (int)this.DecodeDirect(5);
    var value = this.DecodeDirect(slot == 0 ? 1 : slot);
    return slot == 0 ? value : value + (1u << slot);
  }

  private uint DecodeByteTree(uint[] probabilities, int offset) {
    uint c = 1;
    do
      c = (c << 1) | this.DecodeBit(ref probabilities[offset + (int)c]);
    while (c < 0x100);
    return c & 0xFF;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private uint DecodeBit(ref uint probability) {
    if (this._range < TopValue) {
      this._range <<= 8;
      this._code = (this._code << 8) | this.NextRangeByte();
    }

    var bound = (this._range >> 12) * probability;
    if (this._code < bound) {
      this._range = bound;
      probability += (0xFFF - probability) >> 5;
      return 1;
    }

    this._range -= bound;
    this._code -= bound;
    probability -= probability >> 5;
    return 0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private uint NextRangeByte() {
    if (this._rangePos >= this._rangeBlock.Length) {
      this._rangeBlock = this._reader.ReadRangeCoderBlock();
      this._rangePos = 0;
    }

    return this._rangeBlock[this._rangePos++];
  }

  /// <summary>Reads <paramref name="bits"/> (up to 32) raw bits, MSB first.</summary>
  private uint DecodeDirect(int bits) => bits <= 16
    ? this.DecodeDirect16(bits)
    : (this.DecodeDirect16(bits - 16) << 16) | this.DecodeDirect16(16);

  private uint DecodeDirect16(int bits) {
    while (this._bitCount < bits) {
      if (this._bitPos >= this._bitBlock.Length) {
        this._bitBlock = this._reader.ReadBitCoderBlock();
        this._bitPos = 0;
      }

      this._bitValue = (this._bitValue << 8) | this._bitBlock[this._bitPos++];
      this._bitCount += 8;
    }

    this._bitCount -= bits;
    return (this._bitValue >> this._bitCount) & ((1u << bits) - 1);
  }

  private static uint[] InitProbabilities(int count) {
    var result = new uint[count];
    Array.Fill(result, CscConstants.ProbabilityInit);
    return result;
  }
}
