// The block framing, symbol coding and model state are converted from LZHAM (Rich Geldreich),
// public domain: lzham_symbol_codec.cpp (encoding side), lzham_lzcomp_state.cpp (state::encode,
// encode_eob) and lzham_lzcomp_internal.cpp (compress_block_internal, send_final_block). The
// match search and parsing are this repository's own. See THIRD-PARTY-NOTICE.lzham.txt.
namespace FileFormat.Lzham;

/// <summary>
/// Writes raw LZHAM streams that LZHAM's own decoder reads. The coding layer is LZHAM's; the parse
/// is a hash-chain search with one step of lazy evaluation and LZHAM's four repeat distances, so the
/// bytes differ from what LZHAM's optimal parser writes.
/// </summary>
internal sealed class LzhamEncoder {
  private const int HashBits = 16;
  private const int MaxChain = 48;
  private const int MinFullMatch = 3;
  private const int GoodEnoughMatch = 128;

  private readonly int _dictSizeLog2;
  private readonly int _dictSize;
  private readonly int _numSlots;
  private readonly int _blockSize;
  private readonly int _tableUpdateRate;

  private LzhamEncoderState _state;
  private readonly List<byte> _output = [];
  private int _blockIndex;

  private LzhamEncoder(int dictSizeLog2, int tableUpdateRate) {
    _dictSizeLog2 = dictSizeLog2;
    _dictSize = 1 << dictSizeLog2;
    _numSlots = LzhamTables.PositionSlots(dictSizeLog2);
    _blockSize = Math.Min(512 * 1024, _dictSize / 8);
    _tableUpdateRate = tableUpdateRate;
    _state = new LzhamEncoderState(dictSizeLog2, tableUpdateRate);
  }

  /// <summary>Compresses <paramref name="data"/> into a raw LZHAM stream.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, int dictSizeLog2, int tableUpdateRate) {
    if (dictSizeLog2 is < LzhamTables.MinDictSizeLog2 or > LzhamTables.MaxDictSizeLog2)
      throw new ArgumentOutOfRangeException(nameof(dictSizeLog2));
    var encoder = new LzhamEncoder(dictSizeLog2, tableUpdateRate);
    return encoder.Run(data.ToArray());
  }

  private byte[] Run(byte[] data) {
    var matcher = new HashChain(data, _dictSize);
    for (var offset = 0; offset < data.Length; offset += _blockSize)
      CompressBlock(data, offset, Math.Min(_blockSize, data.Length - offset), matcher);

    // send_final_block: the EOF block, then the Adler-32 of the input.
    var writer = new LzhamSymbolWriter();
    writer.EncodeBits(LzhamTables.EofBlock, LzhamTables.BlockHeaderBits);
    writer.EncodeAlignToByte();
    writer.EncodeBits(LzhamDecoder.Adler32(data), 32);
    _output.AddRange(writer.Finish());
    return [.. _output];
  }

  private void CompressBlock(byte[] data, int start, int length, HashChain matcher) {
    var startState = _state.Clone();

    var writer = new LzhamSymbolWriter();
    writer.EncodeBits(LzhamTables.CompBlock, LzhamTables.BlockHeaderBits);
    writer.EncodeArithInit();
    _state.StartOfBlock();
    writer.EncodeBits(0, LzhamTables.BlockFlushTypeBits);

    var end = start + length;
    var pos = start;
    var pending = FindBest(data, pos, end, matcher);
    while (pos < end) {
      var current = pending;
      if (current.Length >= 2 && current.Length < GoodEnoughMatch && pos + 1 < end) {
        matcher.Insert(pos);
        var next = FindBest(data, pos + 1, end, matcher);
        if (next.Length > current.Length + 1 || (next.Length == current.Length + 1 && current.RepIndex < 0 && next.RepIndex >= 0)) {
          // Lazy evaluation: a literal now buys a longer match one byte later.
          _state.EncodeLiteral(writer, data, pos);
          ++pos;
          pending = next;
          continue;
        }

        for (var i = 1; i < current.Length; ++i)
          matcher.Insert(pos + i);
      } else if (current.Length >= 2) {
        for (var i = 0; i < current.Length; ++i)
          matcher.Insert(pos + i);
      } else
        matcher.Insert(pos);

      if (current.Length < 2)
        _state.EncodeLiteral(writer, data, pos);
      else if (current.RepIndex >= 0)
        _state.EncodeRepMatch(writer, current.RepIndex, current.Length);
      else
        _state.EncodeFullMatch(writer, current.Distance, current.Length);

      pos += Math.Max(1, current.Length);
      if (pos < end)
        pending = FindBest(data, pos, end, matcher);
    }

    _state.EncodeEndOfBlock(writer);
    var compressed = writer.Finish();

    if (compressed.Length >= length) {
      // compress_block_internal: a block that does not shrink is sent raw, from the state it started with.
      _state = startState;
      var raw = new LzhamSymbolWriter();
      raw.EncodeBits(LzhamTables.RawBlock, LzhamTables.BlockHeaderBits);
      raw.EncodeBits((uint)(length - 1), 24);
      var n = (uint)(length - 1);
      raw.EncodeBits((n & 0xFF) ^ ((n >> 8) & 0xFF) ^ ((n >> 16) & 0xFF), 8);
      raw.EncodeAlignToByte();
      for (var i = start; i < end; ++i)
        raw.EncodeBits(data[i], 8);
      compressed = raw.Finish();
    }

    _output.AddRange(compressed);
    ++_blockIndex;
  }

  private readonly record struct Candidate(int Length, int Distance, int RepIndex);

  private Candidate FindBest(byte[] data, int pos, int end, HashChain matcher) {
    var maxLen = Math.Min(LzhamTables.MaxHugeMatchLen, end - pos);
    if (maxLen < 2)
      return new(0, 0, -1);

    // Repeat distances first: they code cheaply, so a slightly shorter one is preferred.
    var best = new Candidate(0, 0, -1);
    for (var r = 0; r < 4; ++r) {
      var dist = _state.MatchHistory(r);
      if (dist > pos)
        continue;
      var len = MatchLength(data, pos, pos - dist, maxLen);
      if (len >= 2 && len > best.Length)
        best = new(len, dist, r);
    }

    var maxDistance = Math.Min(_dictSize - 1, (int)Math.Min(LzhamTables.PositionBase[_numSlots] - 1, int.MaxValue));
    if (maxLen >= MinFullMatch) {
      var (length, distance) = matcher.Find(pos, maxLen, maxDistance, MaxChain);
      if (length >= MinFullMatch && length > best.Length + 1) {
        var rep = -1;
        for (var r = 0; r < 4; ++r)
          if (_state.MatchHistory(r) == distance) {
            rep = r;
            break;
          }

        best = new(length, distance, rep);
      }
    }

    return best;
  }

  private static int MatchLength(byte[] data, int a, int b, int max) {
    var len = 0;
    while (len < max && data[a + len] == data[b + len])
      ++len;
    return len;
  }

  /// <summary>Hash chains over 3-byte prefixes.</summary>
  private sealed class HashChain(byte[] data, int dictSize) {
    private readonly int[] _head = CreateHead();
    private readonly int[] _prev = new int[data.Length];

    private static int[] CreateHead() {
      var head = new int[1 << HashBits];
      Array.Fill(head, -1);
      return head;
    }

    private int Hash(int pos) => (int)(((uint)(data[pos] << 16 | data[pos + 1] << 8 | data[pos + 2]) * 2654435761u) >> (32 - HashBits));

    private int _inserted;

    public void Insert(int pos) {
      // Positions arrive in order; catch up any that were skipped.
      for (; _inserted <= pos; ++_inserted) {
        if (_inserted + 2 >= data.Length) {
          _prev[_inserted] = -1;
          continue;
        }

        var h = Hash(_inserted);
        _prev[_inserted] = _head[h];
        _head[h] = _inserted;
      }
    }

    public (int Length, int Distance) Find(int pos, int maxLen, int maxDistance, int maxChain) {
      if (pos + 2 >= data.Length)
        return (0, 0);
      Insert(pos - 1);
      int bestLen = 0, bestDist = 0;
      var candidate = _head[Hash(pos)];
      for (var chain = 0; candidate >= 0 && chain < maxChain; ++chain, candidate = _prev[candidate]) {
        var dist = pos - candidate;
        if (dist > maxDistance || dist > dictSize - 1)
          break;
        if (data[candidate + bestLen] != data[pos + bestLen])
          continue;
        var len = MatchLength(data, pos, candidate, maxLen);
        if (len > bestLen) {
          bestLen = len;
          bestDist = dist;
          if (len == maxLen)
            break;
        }
      }

      return (bestLen, bestDist);
    }
  }
}

/// <summary>
/// The compressor's coding state (<c>lzcompressor::state</c>): the Huffman tables, the adaptive bit
/// models, the four repeat distances and the 12-state literal/match history.
/// </summary>
internal sealed class LzhamEncoderState {
  private readonly int _dictSizeLog2;
  private LzhamHuffmanModel _litTable = null!;
  private LzhamHuffmanModel _deltaLitTable = null!;
  private LzhamHuffmanModel _mainTable = null!;
  private LzhamHuffmanModel[] _repLenTable = null!;
  private LzhamHuffmanModel[] _largeLenTable = null!;
  private LzhamHuffmanModel _distLsbTable = null!;

  private ushort[] _isMatch = null!;
  private ushort[] _isRep = null!;
  private ushort[] _isRep0 = null!;
  private ushort[] _isRep0SingleByte = null!;
  private ushort[] _isRep1 = null!;
  private ushort[] _isRep2 = null!;

  private readonly int[] _matchHist = [1, 1, 1, 1];
  private int _curState;

  public LzhamEncoderState(int dictSizeLog2, int tableUpdateRate) {
    _dictSizeLog2 = dictSizeLog2;
    var (maxUpdateInterval, slowRate) = LzhamTables.UpdateSettings(tableUpdateRate);
    var repLen = new LzhamHuffmanModel(true, LzhamTables.NumHugeMatchCodes + (LzhamTables.MaxMatchLen - LzhamTables.MinMatchLen + 1), maxUpdateInterval, slowRate);
    _repLenTable = [repLen, repLen.Clone()];
    var largeLen = new LzhamHuffmanModel(true, LzhamTables.NumHugeMatchCodes + LzhamTables.NumSecondaryLengths, maxUpdateInterval, slowRate);
    _largeLenTable = [largeLen, largeLen.Clone()];
    _mainTable = new LzhamHuffmanModel(true, LzhamTables.MainTableSymbols(dictSizeLog2), maxUpdateInterval, slowRate);
    _distLsbTable = new LzhamHuffmanModel(true, 16, maxUpdateInterval, slowRate);
    _litTable = new LzhamHuffmanModel(true, 256, maxUpdateInterval, slowRate);
    _deltaLitTable = new LzhamHuffmanModel(true, 256, maxUpdateInterval, slowRate);
    _isMatch = NewBitModels();
    _isRep = NewBitModels();
    _isRep0 = NewBitModels();
    _isRep0SingleByte = NewBitModels();
    _isRep1 = NewBitModels();
    _isRep2 = NewBitModels();
  }

  private LzhamEncoderState(LzhamEncoderState other) {
    _dictSizeLog2 = other._dictSizeLog2;
    _litTable = other._litTable.Clone();
    _deltaLitTable = other._deltaLitTable.Clone();
    _mainTable = other._mainTable.Clone();
    _repLenTable = [other._repLenTable[0].Clone(), other._repLenTable[1].Clone()];
    _largeLenTable = [other._largeLenTable[0].Clone(), other._largeLenTable[1].Clone()];
    _distLsbTable = other._distLsbTable.Clone();
    _isMatch = (ushort[])other._isMatch.Clone();
    _isRep = (ushort[])other._isRep.Clone();
    _isRep0 = (ushort[])other._isRep0.Clone();
    _isRep0SingleByte = (ushort[])other._isRep0SingleByte.Clone();
    _isRep1 = (ushort[])other._isRep1.Clone();
    _isRep2 = (ushort[])other._isRep2.Clone();
    other._matchHist.CopyTo(_matchHist, 0);
    _curState = other._curState;
  }

  public LzhamEncoderState Clone() => new(this);

  private static ushort[] NewBitModels() {
    var models = new ushort[LzhamTables.NumStates];
    Array.Fill(models, (ushort)(1 << 10));
    return models;
  }

  public int MatchHistory(int index) => _matchHist[index];

  public void StartOfBlock() {
    _matchHist.AsSpan().Fill(1);
    _curState = 0;
  }

  public void EncodeLiteral(LzhamSymbolWriter writer, byte[] data, int pos) {
    writer.EncodeBit(0, _isMatch, _curState);
    var lit = data[pos];
    if (_curState < LzhamTables.NumLitStates)
      writer.EncodeSymbol(lit, _litTable);
    else
      writer.EncodeSymbol(lit ^ data[pos - _matchHist[0]], _deltaLitTable);

    _curState = _curState < 4 ? 0 : _curState < 10 ? _curState - 3 : _curState - 6;
  }

  private void EncodeLength(LzhamSymbolWriter writer, int length, LzhamHuffmanModel table, int baseLength) {
    if (length > LzhamTables.MaxMatchLen) {
      writer.EncodeSymbol(LzhamTables.MaxMatchLen + 1 - baseLength, table);
      var (bits, count) = HugeMatchCode(length);
      writer.EncodeBits(bits, count);
    } else
      writer.EncodeSymbol(length - baseLength, table);
  }

  private static (uint Bits, int Count) HugeMatchCode(int length) {
    var len = (uint)(length - (LzhamTables.MaxMatchLen + 1));
    if (len < 256)
      return (len, 1 + 8);
    if (len < 256 + 1024)
      return ((len - 256) | (2u << 10), 2 + 10);
    if (len < 256 + 1024 + 4096)
      return ((len - (256 + 1024)) | (6u << 12), 3 + 12);
    return ((len - (256 + 1024 + 4096)) | (7u << 16), 3 + 16);
  }

  public void EncodeRepMatch(LzhamSymbolWriter writer, int repIndex, int length) {
    writer.EncodeBit(1, _isMatch, _curState);
    writer.EncodeBit(1, _isRep, _curState);
    var bank = _repLenTable[_curState >= LzhamTables.NumLitStates ? 1 : 0];
    if (repIndex == 0) {
      writer.EncodeBit(1, _isRep0, _curState);
      if (length == 1) {
        writer.EncodeBit(1, _isRep0SingleByte, _curState);
        _curState = _curState < LzhamTables.NumLitStates ? 9 : 11;
        return;
      }

      writer.EncodeBit(0, _isRep0SingleByte, _curState);
      EncodeLength(writer, length, bank, LzhamTables.MinMatchLen);
    } else {
      writer.EncodeBit(0, _isRep0, _curState);
      EncodeLength(writer, length, bank, LzhamTables.MinMatchLen);
      if (repIndex == 1) {
        writer.EncodeBit(1, _isRep1, _curState);
        (_matchHist[0], _matchHist[1]) = (_matchHist[1], _matchHist[0]);
      } else {
        writer.EncodeBit(0, _isRep1, _curState);
        if (repIndex == 2) {
          writer.EncodeBit(1, _isRep2, _curState);
          (_matchHist[0], _matchHist[1], _matchHist[2]) = (_matchHist[2], _matchHist[0], _matchHist[1]);
        } else {
          writer.EncodeBit(0, _isRep2, _curState);
          (_matchHist[0], _matchHist[1], _matchHist[2], _matchHist[3]) = (_matchHist[3], _matchHist[0], _matchHist[1], _matchHist[2]);
        }
      }
    }

    _curState = _curState < LzhamTables.NumLitStates ? 8 : 11;
  }

  public void EncodeFullMatch(LzhamSymbolWriter writer, int distance, int length) {
    writer.EncodeBit(1, _isMatch, _curState);
    writer.EncodeBit(0, _isRep, _curState);

    var slot = PositionSlot((uint)distance);
    var extra = (uint)distance - LzhamTables.PositionBase[slot];
    var lowSym = length >= 9 ? 7 : length - 2;
    var mainSym = lowSym | ((slot - LzhamTables.LowestUsableMatchSlot) << 3);
    writer.EncodeSymbol(LzhamTables.NumSpecialLengths + mainSym, _mainTable);

    if (length >= 9)
      EncodeLength(writer, length, _largeLenTable[_curState >= LzhamTables.NumLitStates ? 1 : 0], 9);

    int numExtraBits = LzhamTables.PositionExtraBits[slot];
    if (numExtraBits < 3)
      writer.EncodeBits(extra, numExtraBits);
    else {
      if (numExtraBits > 4)
        writer.EncodeBits(extra >> 4, numExtraBits - 4);
      writer.EncodeSymbol((int)(extra & 15), _distLsbTable);
    }

    (_matchHist[3], _matchHist[2], _matchHist[1], _matchHist[0]) = (_matchHist[2], _matchHist[1], _matchHist[0], distance);
    _curState = _curState < LzhamTables.NumLitStates ? LzhamTables.NumLitStates : LzhamTables.NumLitStates + 3;
  }

  public void EncodeEndOfBlock(LzhamSymbolWriter writer) {
    writer.EncodeBit(1, _isMatch, _curState);
    writer.EncodeBit(0, _isRep, _curState);
    writer.EncodeSymbol(LzhamTables.SpecialCodeEndOfBlock, _mainTable);
  }

  private static int PositionSlot(uint distance) {
    var slot = Array.BinarySearch(LzhamTables.PositionBase, distance);
    return slot >= 0 ? slot : ~slot - 1;
  }
}

/// <summary>
/// LZHAM's <c>symbol_codec</c> on the encoding side: plain bits and Huffman codes are recorded,
/// adaptive bits go through the 32-bit arithmetic coder, and <see cref="Finish"/> interleaves them
/// into one bit stream exactly where the decoder will read them (<c>assemble_output_buf</c>).
/// </summary>
internal sealed class LzhamSymbolWriter {
  private const int ArithSym = -1;
  private const int AlignToByteSym = -2;
  private const int ArithInit = -3;
  private const uint ArithMinLen = 0x01000000u;

  private readonly List<(uint Bits, int NumBits, ushort Prob0)> _symbols = [];
  private readonly List<byte> _arithOutput = [];
  private uint _arithBase;
  private uint _arithLength = uint.MaxValue;

  public void EncodeBits(uint bits, int numBits) {
    if (numBits == 0)
      return;
    if (numBits > 16) {
      _symbols.Add((bits >> 16, numBits - 16, 0));
      _symbols.Add((bits & 0xFFFF, 16, 0));
    } else
      _symbols.Add((bits, numBits, 0));
  }

  public void EncodeArithInit() => _symbols.Add((0, ArithInit, 0));

  public void EncodeAlignToByte() => _symbols.Add((0, AlignToByteSym, 0));

  public void EncodeSymbol(int symbol, LzhamHuffmanModel model) {
    if (model.CodeSizes[symbol] != 0)
      _symbols.Add((model.Codes[symbol], model.CodeSizes[symbol], 0));
    model.Update(symbol);
  }

  public void EncodeBit(uint bit, ushort[] models, int index) {
    var prob0 = models[index];
    _symbols.Add((bit, ArithSym, prob0));
    var x = prob0 * (_arithLength >> 11);
    if (bit == 0) {
      models[index] = (ushort)(prob0 + ((2048 - prob0) >> 5));
      _arithLength = x;
    } else {
      models[index] = (ushort)(prob0 - (prob0 >> 5));
      var origBase = _arithBase;
      _arithBase += x;
      _arithLength -= x;
      if (origBase > _arithBase)
        PropagateCarry();
    }

    if (_arithLength < ArithMinLen)
      Renormalize();
  }

  private void PropagateCarry() {
    for (var index = _arithOutput.Count - 1; index >= 0; --index) {
      if (_arithOutput[index] == 0xFF)
        _arithOutput[index] = 0;
      else {
        ++_arithOutput[index];
        break;
      }
    }
  }

  private void Renormalize() {
    do {
      _arithOutput.Add((byte)(_arithBase >> 24));
      _arithBase <<= 8;
    } while ((_arithLength <<= 8) < ArithMinLen);
  }

  /// <summary><c>stop_encoding(true)</c>: flushes the arithmetic coder and assembles the bytes.</summary>
  public byte[] Finish() {
    var origBase = _arithBase;
    if (_arithLength > 2 * ArithMinLen) {
      _arithBase += ArithMinLen;
      _arithLength = ArithMinLen >> 1;
    } else {
      _arithBase += ArithMinLen >> 1;
      _arithLength = ArithMinLen >> 9;
    }

    if (origBase > _arithBase)
      PropagateCarry();
    Renormalize();
    while (_arithOutput.Count < 4)
      _arithOutput.Add(0);

    return Assemble();
  }

  private byte[] Assemble() {
    var writer = new BitWriter();
    var arithOfs = 0;
    uint arithValue = 0, arithLength = 0;
    foreach (var (bits, numBits, prob0) in _symbols) {
      switch (numBits) {
        case AlignToByteSym:
          writer.AlignToByte();
          break;
        case ArithInit:
          arithLength = uint.MaxValue;
          arithValue = 0;
          for (var i = 0; i < 4; ++i) {
            uint c = _arithOutput[arithOfs++];
            arithValue = arithValue << 8 | c;
            writer.Put(c, 8);
          }

          break;
        case ArithSym: {
          if (arithLength < ArithMinLen)
            do {
              uint c = arithOfs < _arithOutput.Count ? _arithOutput[arithOfs++] : 0u;
              writer.Put(c, 8);
              arithValue = arithValue << 8 | c;
            } while ((arithLength <<= 8) < ArithMinLen);

          var x = prob0 * (arithLength >> 11);
          var bit = arithValue >= x ? 1u : 0u;
          if (bit == 0)
            arithLength = x;
          else {
            arithValue -= x;
            arithLength -= x;
          }

          if (bit != bits)
            throw new InvalidOperationException("LZHAM: arithmetic coder out of step");
          break;
        }

        default:
          writer.Put(bits, numBits);
          break;
      }
    }

    // flush_bits: seven zero bits push out any partial byte.
    writer.Put(0, 7);
    return writer.ToArray();
  }

  private sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private ulong _buffer;
    private int _count;

    public void Put(uint bits, int numBits) {
      if (numBits == 0)
        return;
      _buffer = (_buffer << numBits) | (bits & (numBits == 32 ? uint.MaxValue : (1u << numBits) - 1));
      _count += numBits;
      while (_count >= 8) {
        _count -= 8;
        _bytes.Add((byte)(_buffer >> _count));
      }
    }

    public void AlignToByte() {
      if ((_count & 7) != 0)
        Put(0, 8 - (_count & 7));
    }

    public byte[] ToArray() => [.. _bytes];
  }
}
