namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Decompressor for RAR 1.5 packed data (unpack version 15): LZSS over a 64 KB window, coded with
/// fixed Huffman tables picked by running averages, self-organising byte tables and a stored-run
/// mode.
/// </summary>
/// <remarks>
/// Converted from XADRAR15Handle.m of The Unarchiver's XADMaster (LGPL-2.1-or-later), see
/// THIRD-PARTY-NOTICE.RarLegacy.txt. One instance decodes one solid stream: call
/// <see cref="Decompress"/> once per file, in archive order, on the same instance for the files of
/// a solid archive and on a new instance for a file that does not continue one.
/// </remarks>
public sealed class Rar1Decoder {
  private const int WindowSize = 0x10000;
  private const int WindowMask = Rar1Decoder.WindowSize - 1;

  // The static code lengths, as (length, run) pairs in symbol order.
  private static readonly RarLegacyPrefixCode LengthCode1 = Canonical(256, (2, 2), (3, 1), (4, 2), (5, 2), (6, 4), (7, 5), (8, 4), (9, 4), (10, 8), (12, 224));
  private static readonly RarLegacyPrefixCode LengthCode2 = Canonical(256, (3, 5), (4, 2), (5, 2), (6, 4), (7, 5), (8, 4), (9, 4), (10, 8), (11, 2), (12, 220));
  private static readonly RarLegacyPrefixCode HuffmanCode0 = Canonical(257, (4, 8), (5, 8), (6, 8), (7, 9), (12, 224));
  private static readonly RarLegacyPrefixCode HuffmanCode1 = Canonical(257, (5, 4), (6, 40), (7, 16), (8, 16), (9, 4), (11, 47), (12, 130));
  private static readonly RarLegacyPrefixCode HuffmanCode2 = Canonical(257, (5, 2), (6, 5), (7, 46), (8, 64), (9, 116), (10, 24));
  private static readonly RarLegacyPrefixCode HuffmanCode3 = Canonical(257, (6, 2), (7, 14), (8, 202), (9, 33), (10, 6));
  private static readonly RarLegacyPrefixCode HuffmanCode4 = Canonical(257, (8, 255), (9, 2));

  // The four short-match selector codes: (selector, code, length).
  private static readonly RarLegacyPrefixCode ShortMatchCode0 = RarLegacyPrefixCode.FromCodes(8, [
    (0, 0x00, 1), (1, 0x0a, 4), (2, 0x0d, 4), (3, 0x0e, 4), (4, 0x1e, 5), (5, 0x3e, 6), (6, 0x7e, 7), (7, 0xfe, 8),
    (8, 0xff, 8), (9, 0x0c, 4), (10, 0x08, 4), (11, 0x12, 5), (12, 0x26, 6), (13, 0x27, 6), (14, 0x0b, 4),
  ]);

  private static readonly RarLegacyPrefixCode ShortMatchCode1 = RarLegacyPrefixCode.FromCodes(8, [
    (0, 0x00, 1), (1, 0x05, 3), (2, 0x0d, 4), (3, 0x0e, 4), (4, 0x1e, 5), (5, 0x3e, 6), (6, 0x7e, 7), (7, 0xfe, 8),
    (8, 0xff, 8), (9, 0x0c, 4), (10, 0x08, 4), (11, 0x12, 5), (12, 0x26, 6), (13, 0x27, 6),
  ]);

  // Upstream lists selector 5 as code 0x3e of length 4; only the low four bits (1110) take part.
  private static readonly RarLegacyPrefixCode ShortMatchCode2 = RarLegacyPrefixCode.FromCodes(6, [
    (0, 0x00, 2), (1, 0x02, 3), (2, 0x03, 3), (3, 0x0a, 4), (4, 0x0d, 4), (5, 0x0e, 4), (6, 0x1e, 5), (7, 0x3e, 6),
    (8, 0x3f, 6), (9, 0x0c, 4), (10, 0x08, 4), (11, 0x12, 5), (12, 0x26, 6), (13, 0x27, 6), (14, 0x0b, 4),
  ]);

  private static readonly RarLegacyPrefixCode ShortMatchCode3 = RarLegacyPrefixCode.FromCodes(6, [
    (0, 0x00, 2), (1, 0x02, 3), (2, 0x03, 3), (3, 0x05, 3), (4, 0x0d, 4), (5, 0x0e, 4), (6, 0x1e, 5), (7, 0x3e, 6),
    (8, 0x3f, 6), (9, 0x0c, 4), (10, 0x08, 4), (11, 0x12, 5), (12, 0x26, 6), (13, 0x27, 6),
  ]);

  private readonly byte[] _window = new byte[Rar1Decoder.WindowSize];
  private long _position;

  private RarLegacyBitReader _input = new([]);
  private byte[] _output = [];
  private int _outputPos;

  private bool _storedBlock;
  private int _flags, _flagBits;
  private int _literalWeight = 0x80, _matchWeight = 0x80;
  private int _repeatedLiterals, _repeatedLastMatches;
  private int _averageLiteral = 0x3500, _averageSelector, _averageLength, _averageOffset, _averageBelowMaximum;
  private int _maximumOffset = 0x2001;
  private bool _bugFix;
  private int _lastOffset, _lastLength;
  private readonly int[] _oldOffsets = new int[4];
  private int _oldOffsetIndex;

  private readonly int[] _flagTable = new int[256], _flagReverse = new int[256];
  private readonly int[] _literalTable = new int[256], _literalReverse = new int[256];
  private readonly int[] _offsetTable = new int[256], _offsetReverse = new int[256];
  private readonly int[] _shortOffsetTable = new int[256];

  /// <summary>Starts a new solid stream.</summary>
  public Rar1Decoder() {
    for (var i = 0; i < 256; ++i) {
      this._flagTable[i] = ((-i) & 0xff) << 8;
      this._literalTable[i] = i << 8;
      this._offsetTable[i] = i << 8;
      this._shortOffsetTable[i] = i;
    }

    ResetTable(this._offsetTable, this._offsetReverse);
  }

  /// <summary>
  /// Decodes the next file of the stream.
  /// </summary>
  /// <param name="compressed">The file's packed data.</param>
  /// <param name="unpackedSize">The file's unpacked size.</param>
  /// <returns>The unpacked bytes.</returns>
  public byte[] Decompress(ReadOnlySpan<byte> compressed, int unpackedSize) {
    ArgumentOutOfRangeException.ThrowIfNegative(unpackedSize);
    this._input = new(compressed.ToArray());
    this._output = new byte[unpackedSize];
    this._outputPos = 0;
    this._flagBits = 0;
    this._storedBlock = false;
    this._repeatedLastMatches = 0;

    while (this._outputPos < unpackedSize) {
      if (this._storedBlock) {
        this.EmitLiteral();
        continue;
      }

      var expectingMatch = this._matchWeight > this._literalWeight;
      if (this.NextFlagBit()) {
        if (expectingMatch)
          this.EmitLongMatch();
        else
          this.EmitLiteral();
      } else if (this.NextFlagBit()) {
        if (expectingMatch)
          this.EmitLiteral();
        else
          this.EmitLongMatch();
      } else
        this.EmitShortMatch();
    }

    return this._output;
  }

  private bool NextFlagBit() {
    if (this._flagBits == 0) {
      var index = Rar1Decoder.HuffmanCode2.Decode(this._input);
      if (index == 256)
        throw new InvalidDataException("Invalid flag byte in RAR 1.5 packed data.");

      this._flags = LookupByte(this._flagTable, this._flagReverse, 0xff, index);
      this._flagBits = 8;
    }

    --this._flagBits;
    return ((this._flags >> this._flagBits) & 1) != 0;
  }

  private void EmitLiteral() {
    var code = this._averageLiteral switch {
      < 0xe00 => Rar1Decoder.HuffmanCode0,
      < 0x3600 => Rar1Decoder.HuffmanCode1,
      < 0x5e00 => Rar1Decoder.HuffmanCode2,
      < 0x7600 => Rar1Decoder.HuffmanCode3,
      _ => Rar1Decoder.HuffmanCode4,
    };
    var index = code.Decode(this._input);

    if (this._storedBlock) {
      if (index == 0) {
        if (this._input.ReadBit()) {
          this._storedBlock = false;
          this._repeatedLiterals = 0;
          return;
        }

        var length = this._input.ReadBit() ? 4 : 3;
        var offset = Rar1Decoder.HuffmanCode2.Decode(this._input) << 5;
        offset |= this._input.ReadBits(5);
        this.EmitMatch(offset, length);
        return;
      }

      --index;
    } else {
      index &= 0xff;
      if (this._repeatedLiterals++ >= 16 && this._flagBits == 0)
        this._storedBlock = true;
    }

    this._averageLiteral += index;
    this._averageLiteral -= this._averageLiteral >> 8;

    this._literalWeight += 16;
    if (this._literalWeight > 0xff) {
      this._literalWeight = 0x90;
      this._matchWeight >>= 1;
    }

    this.EmitByte((byte)LookupByte(this._literalTable, this._literalReverse, 0xa1, index));
  }

  private void EmitLongMatch() {
    this._repeatedLiterals = 0;
    this._matchWeight += 16;
    if (this._matchWeight > 0xff) {
      this._matchWeight = 0x90;
      this._literalWeight >>= 1;
    }

    int rawLength;
    if (this._averageLength >= 122)
      rawLength = Rar1Decoder.LengthCode2.Decode(this._input);
    else if (this._averageLength >= 64)
      rawLength = Rar1Decoder.LengthCode1.Decode(this._input);
    else {
      rawLength = 0;
      while (rawLength < 8 && !this._input.ReadBit())
        ++rawLength;

      if (rawLength == 8)
        rawLength = this._input.ReadBits(8);
    }

    var offsetCode = this._averageOffset switch {
      < 0x700 => Rar1Decoder.HuffmanCode0,
      < 0x2900 => Rar1Decoder.HuffmanCode1,
      _ => Rar1Decoder.HuffmanCode2,
    };
    var offsetIndex = offsetCode.Decode(this._input);
    if (offsetIndex == 0x100)
      throw new InvalidDataException("Invalid match offset in RAR 1.5 packed data.");

    var offset = LookupByte(this._offsetTable, this._offsetReverse, 0xff, offsetIndex) << 7;
    offset |= this._input.ReadBits(7);

    var length = rawLength + 3;
    if (offset >= this._maximumOffset)
      ++length;
    if (offset <= 256)
      length += 8;

    var previousMaximumOffset = this._maximumOffset;
    this._maximumOffset = this._averageBelowMaximum > 0xb0 || (this._averageLiteral >= 0x2a00 && this._averageLength < 0x40)
      ? 0x7f00
      : 0x2001;

    this._averageLength += rawLength;
    this._averageLength -= this._averageLength >> 5;

    this._averageOffset += offsetIndex;
    this._averageOffset -= this._averageOffset >> 8;

    if (rawLength == 0 && offset <= previousMaximumOffset) {
      ++this._averageBelowMaximum;
      this._averageBelowMaximum -= this._averageBelowMaximum >> 8;
    } else if (rawLength != 1 && rawLength != 4 && this._averageBelowMaximum > 0)
      --this._averageBelowMaximum;

    this.RememberAndEmit(offset, length);
  }

  private void EmitShortMatch() {
    this._repeatedLiterals = 0;

    if (this._repeatedLastMatches == 2) {
      if (this._input.ReadBit()) {
        this.EmitMatch(this._lastOffset, this._lastLength);
        return;
      }

      this._repeatedLastMatches = 0;
    }

    var selectorCode = this._averageSelector < 37
      ? this._bugFix ? Rar1Decoder.ShortMatchCode0 : Rar1Decoder.ShortMatchCode1
      : this._bugFix ? Rar1Decoder.ShortMatchCode2 : Rar1Decoder.ShortMatchCode3;
    var selector = selectorCode.Decode(this._input);

    switch (selector) {
      case < 9: {
        this._repeatedLastMatches = 0;
        this._averageSelector += selector;
        this._averageSelector -= this._averageSelector >> 4;

        var offsetIndex = Rar1Decoder.HuffmanCode2.Decode(this._input) & 0xff;
        var offset = this._shortOffsetTable[offsetIndex];
        if (offsetIndex != 0) {
          this._shortOffsetTable[offsetIndex] = this._shortOffsetTable[offsetIndex - 1];
          this._shortOffsetTable[offsetIndex - 1] = offset;
        }

        this.RememberAndEmit(offset + 1, selector + 2);
        return;
      }
      case 9:
        ++this._repeatedLastMatches;
        this.EmitMatch(this._lastOffset, this._lastLength);
        return;
      case < 14: {
        this._repeatedLastMatches = 0;
        var offset = this._oldOffsets[(this._oldOffsetIndex - (selector - 9)) & 3];
        var length = Rar1Decoder.LengthCode1.Decode(this._input) + 2;
        if (length == 0x101 && selector == 10) {
          this._bugFix = !this._bugFix;
          return;
        }

        if (offset > 256)
          ++length;
        if (offset >= this._maximumOffset)
          ++length;

        this.RememberAndEmit(offset, length);
        return;
      }
      default: {
        this._repeatedLastMatches = 0;
        var length = Rar1Decoder.LengthCode2.Decode(this._input) + 5;
        var offset = this._input.ReadBits(15) + 0x8000;
        this._lastOffset = offset;
        this._lastLength = length;
        this.EmitMatch(offset, length);
        return;
      }
    }
  }

  private void RememberAndEmit(int offset, int length) {
    this._lastOffset = this._oldOffsets[this._oldOffsetIndex++ & 3] = offset;
    this._lastLength = length;
    this.EmitMatch(offset, length);
  }

  private void EmitByte(byte value) {
    this._window[(int)(this._position++ & Rar1Decoder.WindowMask)] = value;
    this._output[this._outputPos++] = value;
  }

  private void EmitMatch(int offset, int length) {
    var end = Math.Min(this._output.Length, this._outputPos + length);
    while (this._outputPos < end)
      this.EmitByte(this._window[(int)((this._position - offset) & Rar1Decoder.WindowMask)]);
  }

  /// <summary>Maps <paramref name="index"/> through a self-organising table and moves the hit one rank up.</summary>
  private static int LookupByte(int[] table, int[] reverse, int limit, int index) {
    var value = table[index];
    var newIndex = reverse[value & 0xff]++;

    if ((value & 0xff) >= limit) {
      ResetTable(table, reverse);
      value = table[index];
      newIndex = reverse[value & 0xff]++;
    }

    table[index] = table[newIndex];
    table[newIndex] = value + 1;
    return value >> 8;
  }

  private static void ResetTable(int[] table, int[] reverse) {
    for (var i = 0; i < 8; ++i)
    for (var j = 0; j < 32; ++j)
      table[i * 32 + j] = (table[i * 32 + j] & ~0xff) | (7 - i);

    Array.Clear(reverse);
    for (var i = 0; i < 7; ++i)
      reverse[i] = (7 - i) * 32;
  }

  private static RarLegacyPrefixCode Canonical(int symbols, params ReadOnlySpan<(int Length, int Run)> runs) {
    var lengths = new int[symbols];
    var at = 0;
    foreach (var (length, run) in runs)
      for (var i = 0; i < run; ++i)
        lengths[at++] = length;

    return RarLegacyPrefixCode.FromLengths(lengths);
  }
}
