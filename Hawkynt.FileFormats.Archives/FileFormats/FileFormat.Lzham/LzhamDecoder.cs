// Converted from LZHAM (Rich Geldreich), public domain: lzham_lzdecomp.cpp and the decoding
// macros of lzham_symbol_codec.h. See THIRD-PARTY-NOTICE.lzham.txt in this directory.
namespace FileFormat.Lzham;

/// <summary>
/// Decodes a raw LZHAM stream (<c>lzham_decompress</c>) whose decompressed size is known: the
/// block loop of <c>lzham_decompressor::decompress</c> over the whole input at once.
/// </summary>
internal sealed class LzhamDecoder {
  private static readonly byte[] LiteralNextState = [0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 4, 5];
  private static readonly int[] HugeMatchBaseLen = [LzhamTables.MaxMatchLen + 1, LzhamTables.MaxMatchLen + 1 + 256, LzhamTables.MaxMatchLen + 1 + 256 + 1024, LzhamTables.MaxMatchLen + 1 + 256 + 1024 + 4096];
  private static readonly int[] HugeMatchCodeLen = [8, 10, 12, 16];

  private readonly byte[] _input;
  private int _inputPos;
  private ulong _bitBuffer;
  private int _bitCount;
  private uint _arithValue;
  private uint _arithLength;

  private readonly LzhamHuffmanModel _litTable;
  private readonly LzhamHuffmanModel _deltaLitTable;
  private readonly LzhamHuffmanModel _mainTable;
  private readonly LzhamHuffmanModel[] _repLenTable;
  private readonly LzhamHuffmanModel[] _largeLenTable;
  private readonly LzhamHuffmanModel _distLsbTable;

  private readonly ushort[] _isMatch = new ushort[LzhamTables.NumStates];
  private readonly ushort[] _isRep = new ushort[LzhamTables.NumStates];
  private readonly ushort[] _isRep0 = new ushort[LzhamTables.NumStates];
  private readonly ushort[] _isRep0SingleByte = new ushort[LzhamTables.NumStates];
  private readonly ushort[] _isRep1 = new ushort[LzhamTables.NumStates];
  private readonly ushort[] _isRep2 = new ushort[LzhamTables.NumStates];

  private LzhamDecoder(byte[] input, int dictSizeLog2, int tableUpdateRate) {
    _input = input;
    var (maxUpdateInterval, slowRate) = LzhamTables.UpdateSettings(tableUpdateRate);
    _litTable = new LzhamHuffmanModel(false, 256, maxUpdateInterval, slowRate);
    _deltaLitTable = _litTable.Clone();
    _mainTable = new LzhamHuffmanModel(false, LzhamTables.MainTableSymbols(dictSizeLog2), maxUpdateInterval, slowRate);
    var repLen = new LzhamHuffmanModel(false, LzhamTables.NumHugeMatchCodes + (LzhamTables.MaxMatchLen - LzhamTables.MinMatchLen + 1), maxUpdateInterval, slowRate);
    _repLenTable = [repLen, repLen.Clone()];
    var largeLen = new LzhamHuffmanModel(false, LzhamTables.NumHugeMatchCodes + LzhamTables.NumSecondaryLengths, maxUpdateInterval, slowRate);
    _largeLenTable = [largeLen, largeLen.Clone()];
    _distLsbTable = new LzhamHuffmanModel(false, 16, maxUpdateInterval, slowRate);
    ResetArithTables();
  }

  /// <summary>Decodes <paramref name="input"/> into exactly <paramref name="outputSize"/> bytes and checks the Adler-32 trailer.</summary>
  public static byte[] Decode(byte[] input, int outputSize, int dictSizeLog2, int tableUpdateRate) {
    if (dictSizeLog2 is < LzhamTables.MinDictSizeLog2 or > LzhamTables.MaxDictSizeLog2)
      throw new InvalidDataException($"LZHAM: dictionary size 2^{dictSizeLog2} outside 2^15..2^29");
    if (input.Length == 0)
      throw new InvalidDataException("LZHAM: empty stream");
    return new LzhamDecoder(input, dictSizeLog2, tableUpdateRate).Run(outputSize, dictSizeLog2);
  }

  private void ResetArithTables() {
    foreach (var models in new[] { _isMatch, _isRep, _isRep0, _isRep0SingleByte, _isRep1, _isRep2 })
      Array.Fill(models, (ushort)(1 << 10));
  }

  private void ResetHuffTables() {
    _litTable.Reset();
    _deltaLitTable.Reset();
    _mainTable.Reset();
    foreach (var model in _repLenTable)
      model.Reset();
    foreach (var model in _largeLenTable)
      model.Reset();
    _distLsbTable.Reset();
  }

  private void ResetHuffmanTableUpdateRates() {
    _litTable.ResetUpdateRate();
    _deltaLitTable.ResetUpdateRate();
    _mainTable.ResetUpdateRate();
    foreach (var model in _repLenTable)
      model.ResetUpdateRate();
    foreach (var model in _largeLenTable)
      model.ResetUpdateRate();
    _distLsbTable.ResetUpdateRate();
  }

  private void ApplyFlushType(uint flushType) {
    if (flushType == 1)
      ResetHuffmanTableUpdateRates();
    else if (flushType == 2) {
      ResetHuffTables();
      ResetArithTables();
    }
  }

  // ── bit reader (MSB first; past the end it reads zeros, as lzham does at EOF) ──

  private void FillTo(int bits) {
    while (_bitCount < bits) {
      uint r = _inputPos < _input.Length ? _input[_inputPos] : 0u;
      ++_inputPos;
      _bitCount += 8;
      _bitBuffer |= (ulong)r << (64 - _bitCount);
    }
  }

  private uint GetBits(int numBits) {
    if (numBits == 0)
      return 0;
    FillTo(numBits);
    var result = (uint)(_bitBuffer >> (64 - numBits));
    _bitBuffer <<= numBits;
    _bitCount -= numBits;
    return result;
  }

  private void AlignToByte() {
    if ((_bitCount & 7) != 0)
      GetBits(_bitCount & 7);
  }

  private int ReadRawByte() {
    if (_bitCount >= 8)
      return (int)GetBits(8);
    var b = _inputPos < _input.Length ? _input[_inputPos] : throw new InvalidDataException("LZHAM: raw block runs past the end of the stream");
    ++_inputPos;
    return b;
  }

  private uint DecodeBit(ushort[] models, int index) {
    while (_arithLength < 0x01000000u) {
      var c = GetBits(8);
      _arithValue = _arithValue << 8 | c;
      _arithLength <<= 8;
    }

    var prob0 = models[index];
    var x = prob0 * (_arithLength >> 11);
    if (_arithValue < x) {
      models[index] = (ushort)(prob0 + ((2048 - prob0) >> 5));
      _arithLength = x;
      return 0;
    }

    models[index] = (ushort)(prob0 - (prob0 >> 5));
    _arithValue -= x;
    _arithLength -= x;
    return 1;
  }

  private int DecodeSymbol(LzhamHuffmanModel model) {
    FillTo(24);
    var symbol = model.DecodeSymbol(_bitBuffer, out var length);
    if (length > _bitCount)
      FillTo(length);
    _bitBuffer <<= length;
    _bitCount -= length;
    model.Update(symbol);
    return symbol;
  }

  private int DecodeHugeMatchLen() {
    var index = 0;
    do {
      if (GetBits(1) == 0)
        break;
      ++index;
    } while (index < 3);
    return HugeMatchBaseLen[index] + (int)GetBits(HugeMatchCodeLen[index]);
  }

  private byte[] Run(int outputSize, int dictSizeLog2) {
    var output = new byte[outputSize];
    var dst = 0;
    int hist0 = 1, hist1 = 1, hist2 = 1, hist3 = 1;
    var finished = false;

    while (!finished) {
      var blockType = GetBits(LzhamTables.BlockHeaderBits);
      switch (blockType) {
        case LzhamTables.SyncBlock: {
          var flushType = GetBits(LzhamTables.BlockFlushTypeBits);
          ApplyFlushType(flushType);
          AlignToByte();
          if (GetBits(16) != 0 || GetBits(16) != 0xFFFF)
            throw new InvalidDataException("LZHAM: bad sync block");
          break;
        }

        case LzhamTables.RawBlock: {
          var count = (int)GetBits(24);
          var check = GetBits(8);
          if (check != ((count & 0xFF) ^ ((count >> 8) & 0xFF) ^ ((count >> 16) & 0xFF)))
            throw new InvalidDataException("LZHAM: bad raw block length check");
          ++count;
          AlignToByte();
          if (count > outputSize - dst)
            throw new InvalidDataException("LZHAM: raw block runs past the decompressed size");
          for (var i = 0; i < count; ++i)
            output[dst++] = (byte)ReadRawByte();
          break;
        }

        case LzhamTables.CompBlock: {
          _arithValue = 0;
          for (var i = 0; i < 4; ++i)
            _arithValue = _arithValue << 8 | GetBits(8);
          _arithLength = uint.MaxValue;

          hist0 = hist1 = hist2 = hist3 = 1;
          var state = 0;
          ApplyFlushType(GetBits(LzhamTables.BlockFlushTypeBits));

          for (;;) {
            if (DecodeBit(_isMatch, state) == 0) {
              if (dst >= outputSize)
                throw new InvalidDataException("LZHAM: literal past the decompressed size");
              if (state < LzhamTables.NumLitStates)
                output[dst] = (byte)DecodeSymbol(_litTable);
              else {
                var repLit0 = dst - hist0 >= 0 ? output[dst - hist0] : throw new InvalidDataException("LZHAM: delta literal before the start");
                output[dst] = (byte)(DecodeSymbol(_deltaLitTable) ^ repLit0);
              }

              state = LiteralNextState[state];
              ++dst;
              continue;
            }

            int matchLen;
            if (DecodeBit(_isRep, state) != 0) {
              var bank = state >= LzhamTables.NumLitStates ? 1 : 0;
              if (DecodeBit(_isRep0, state) != 0) {
                if (DecodeBit(_isRep0SingleByte, state) != 0) {
                  matchLen = 1;
                  state = state < LzhamTables.NumLitStates ? 9 : 11;
                } else {
                  matchLen = DecodeSymbol(_repLenTable[bank]) + LzhamTables.MinMatchLen;
                  if (matchLen == LzhamTables.MaxMatchLen + 1)
                    matchLen = DecodeHugeMatchLen();
                  state = state < LzhamTables.NumLitStates ? 8 : 11;
                }
              } else {
                matchLen = DecodeSymbol(_repLenTable[bank]) + LzhamTables.MinMatchLen;
                if (matchLen == LzhamTables.MaxMatchLen + 1)
                  matchLen = DecodeHugeMatchLen();

                if (DecodeBit(_isRep1, state) != 0)
                  (hist0, hist1) = (hist1, hist0);
                else if (DecodeBit(_isRep2, state) != 0)
                  (hist0, hist1, hist2) = (hist2, hist0, hist1);
                else
                  (hist0, hist1, hist2, hist3) = (hist3, hist0, hist1, hist2);

                state = state < LzhamTables.NumLitStates ? 8 : 11;
              }
            } else {
              var sym = DecodeSymbol(_mainTable) - LzhamTables.NumSpecialLengths;
              if (sym < 0) {
                if (sym == LzhamTables.SpecialCodeEndOfBlock - LzhamTables.NumSpecialLengths)
                  break;
                hist0 = hist1 = hist2 = hist3 = 1;
                state = 0;
                continue;
              }

              matchLen = (sym & 7) + 2;
              var matchSlot = (sym >> 3) + LzhamTables.LowestUsableMatchSlot;
              if (matchLen == 9) {
                matchLen += DecodeSymbol(_largeLenTable[state >= LzhamTables.NumLitStates ? 1 : 0]);
                if (matchLen == LzhamTables.MaxMatchLen + 1)
                  matchLen = DecodeHugeMatchLen();
              }

              int numExtraBits = LzhamTables.PositionExtraBits[matchSlot];
              uint extraBits;
              if (numExtraBits < 3)
                extraBits = GetBits(numExtraBits);
              else {
                extraBits = 0;
                if (numExtraBits > 4)
                  extraBits = GetBits(numExtraBits - 4) << 4;
                extraBits += (uint)DecodeSymbol(_distLsbTable);
              }

              (hist3, hist2, hist1) = (hist2, hist1, hist0);
              var distance = LzhamTables.PositionBase[matchSlot] + extraBits;
              if (distance > int.MaxValue)
                throw new InvalidDataException("LZHAM: match distance out of range");
              hist0 = (int)distance;
              state = state < LzhamTables.NumLitStates ? LzhamTables.NumLitStates : LzhamTables.NumLitStates + 3;
            }

            if (hist0 > dst || matchLen > outputSize - dst)
              throw new InvalidDataException("LZHAM: match outside the decompressed data");
            var src = dst - hist0;
            for (var i = 0; i < matchLen; ++i)
              output[dst++] = output[src++];
          }

          AlignToByte();
          break;
        }

        default:
          finished = true;
          break;
      }
    }

    AlignToByte();
    var adler = GetBits(16) << 16 | GetBits(16);
    if (dst != outputSize)
      throw new InvalidDataException($"LZHAM: decoded {dst} bytes, expected {outputSize}");
    if (adler != Adler32(output))
      throw new InvalidDataException("LZHAM: Adler-32 mismatch");
    return output;
  }

  internal static uint Adler32(ReadOnlySpan<byte> data) {
    uint s1 = 1, s2 = 0;
    while (!data.IsEmpty) {
      var chunk = Math.Min(data.Length, 5552);
      foreach (var b in data[..chunk]) {
        s1 += b;
        s2 += s1;
      }

      s1 %= 65521;
      s2 %= 65521;
      data = data[chunk..];
    }

    return s2 << 16 | s1;
  }
}
