// Converted from LZHAM (Rich Geldreich), public domain: lzham_match_accel.cpp (search_accelerator,
// single-threaded path). See THIRD-PARTY-NOTICE.lzham.txt.
namespace FileFormat.Lzham;

/// <summary>
/// LZHAM's match finder: one binary tree per 3-byte hash, built a block at a time ahead of the parse,
/// plus a digram chain for length-2 matches. Positions are absolute offsets into the input; LZHAM's
/// own dictionary is a ring of the dictionary size, and every byte it reads through the ring is the
/// input byte at the same absolute position, so the two agree.
/// </summary>
internal sealed class LzhamMatchAccelerator {
  private const int HashSize = 65536;
  private const int DigramHashSize = 4096;
  private const uint LastMatchFlag = 0x80000000u;

  private static ReadOnlySpan<byte> HammingDistance => [
    0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4,
    1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5,
    1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5,
    2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
    1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5,
    2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
    2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
    3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
    1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5,
    2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
    2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
    3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
    2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
    3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
    3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
    4, 5, 5, 6, 5, 6, 6, 7, 5, 6, 6, 7, 6, 7, 7, 8,
  ];

  private readonly byte[] _data;
  private readonly uint _maxDictSize;
  private readonly uint _nodeMask;
  private readonly uint _maxProbes;
  private readonly uint _maxMatches;

  private readonly uint[] _hash = new uint[HashSize];
  private readonly uint[] _left;
  private readonly uint[] _right;
  private readonly uint[] _digramHash = new uint[DigramHashSize];
  private uint[] _digramNext = [];

  // m_matches / m_match_refs: per lookahead byte, an index into the match list or -2 for none.
  private ushort[] _matchLen = [];
  private uint[] _matchDist = [];
  private int[] _matchRefs = [];
  private int _nextMatchRef;

  private readonly ushort[] _tempLen;
  private readonly uint[] _tempDist;

  private uint _fillLookaheadPos;
  private uint _fillLookaheadSize;
  private uint _fillDictSize;

  public LzhamMatchAccelerator(byte[] data, int dictSizeLog2, uint maxMatches, uint maxProbes) {
    _data = data;
    _maxDictSize = 1u << dictSizeLog2;
    _maxProbes = Math.Min(128u, maxProbes);
    _maxMatches = Math.Min(_maxProbes, maxMatches);

    // The tree has one node per dictionary position. When the input fits the dictionary no position
    // ever wraps, so a node per input byte is enough.
    var nodes = (uint)data.Length <= _maxDictSize ? Math.Max(1, data.Length) : (int)_maxDictSize;
    _left = new uint[nodes];
    _right = new uint[nodes];
    _nodeMask = _maxDictSize - 1;
    _tempLen = new ushort[_maxProbes * 2];
    _tempDist = new uint[_maxProbes * 2];
  }

  /// <summary><c>m_lookahead_pos</c>: the absolute position of the next byte to code.</summary>
  public uint LookaheadPos { get; private set; }

  /// <summary><c>m_lookahead_size</c>: the bytes added but not yet coded.</summary>
  public uint LookaheadSize { get; private set; }

  /// <summary><c>m_cur_dict_size</c>: how far back matches may reach from the lookahead position.</summary>
  public uint CurDictSize { get; private set; }

  /// <summary><c>get_max_add_bytes</c>: a block never crosses the end of LZHAM's dictionary ring.</summary>
  public uint MaxAddBytes => _maxDictSize - (LookaheadPos & (_maxDictSize - 1));

  /// <summary><c>add_bytes_begin</c>: takes the next <paramref name="numBytes"/> input bytes and finds their matches.</summary>
  public void AddBytes(uint numBytes) {
    LookaheadSize = numBytes;
    CurDictSize = Math.Min(CurDictSize, _maxDictSize - numBytes);
    _nextMatchRef = 0;

    // find_all_matches
    if (_matchLen.Length < _maxProbes * numBytes) {
      _matchLen = new ushort[_maxProbes * numBytes];
      _matchDist = new uint[_maxProbes * numBytes];
    }

    if (_matchRefs.Length < numBytes)
      _matchRefs = new int[numBytes];
    _fillLookaheadPos = LookaheadPos;
    _fillLookaheadSize = numBytes;
    _fillDictSize = CurDictSize;
    FindAllMatches();
    FindLen2Matches();
  }

  /// <summary><c>advance_bytes</c>.</summary>
  public void AdvanceBytes(uint numBytes) {
    LookaheadPos += numBytes;
    LookaheadSize -= numBytes;
    CurDictSize += numBytes;
  }

  public byte this[uint position] => _data[position];

  /// <summary>
  /// <c>find_matches</c>: the index of the first match found for the byte at
  /// <paramref name="lookaheadOfs"/>, or -1. Matches run in order of increasing length; the last one
  /// is flagged (<see cref="IsLast"/>).
  /// </summary>
  public int FindMatches(uint lookaheadOfs) {
    var reference = _matchRefs[LookaheadPos - _fillLookaheadPos + lookaheadOfs];
    return reference == -2 ? -1 : reference;
  }

  public uint MatchLength(int index) => _matchLen[index] + 2u;

  public uint MatchDistance(int index) => _matchDist[index] & ~LastMatchFlag;

  public bool IsLast(int index) => (_matchDist[index] & LastMatchFlag) != 0;

  private static uint Hash3(uint c0, uint c1, uint c2) => (c0 | (c1 << 8)) ^ (c2 << 4);

  private static uint Hash2(uint c0, uint c1) => c0 ^ (c1 << 4);

  private int CommonPrefix(uint a, uint b, int max) => _data.AsSpan((int)a, max).CommonPrefixLength(_data.AsSpan((int)b, max));

  /// <summary><c>find_all_matches_callback</c> without helper threads.</summary>
  private void FindAllMatches() {
    var data = _data;
    var fillLookaheadPos = _fillLookaheadPos;
    var fillDictSize = _fillDictSize;
    var fillLookaheadSize = _fillLookaheadSize;

    uint c0 = 0, c1 = 0;
    if (fillLookaheadSize >= 2) {
      c0 = data[fillLookaheadPos];
      c1 = data[fillLookaheadPos + 1];
    }

    while (fillLookaheadSize >= 3) {
      var insertPos = fillLookaheadPos;
      var insertNode = (int)(insertPos & _nodeMask);
      uint c2 = data[insertPos + 2];
      var h = Hash3(c0, c1, c2);
      c0 = c1;
      c1 = c2;

      var numMatches = 0;
      var curPos = _hash[h];
      _hash[h] = fillLookaheadPos;

      ref var pLeft = ref _left[insertNode];
      ref var pRight = ref _right[insertNode];

      var maxMatchLen = (int)Math.Min(LzhamTables.MaxMatchLen, fillLookaheadSize);
      var bestMatchLen = 2;
      var n = _maxProbes;

      for (;;) {
        var deltaPos = fillLookaheadPos - curPos;
        if (n-- == 0 || deltaPos == 0 || deltaPos >= fillDictSize) {
          pLeft = 0;
          pRight = 0;
          break;
        }

        var node = (int)(curPos & _nodeMask);
        var matchLen = CommonPrefix(curPos, insertPos, maxMatchLen);

        if (matchLen > bestMatchLen) {
          _tempLen[numMatches] = (ushort)(matchLen - LzhamTables.MinMatchLen);
          _tempDist[numMatches] = deltaPos;
          ++numMatches;
          bestMatchLen = matchLen;
          if (matchLen == maxMatchLen) {
            pLeft = _left[node];
            pRight = _right[node];
            break;
          }
        } else if (bestMatchLen > 2 && bestMatchLen == matchLen) {
          var bestMatchDist = _tempDist[numMatches - 1];
          var bestSlot = LzhamEncoderState.PositionSlot(bestMatchDist);
          var bestOfs = bestMatchDist - LzhamTables.PositionBase[bestSlot];
          var compSlot = LzhamEncoderState.PositionSlot(deltaPos);
          var compOfs = deltaPos - LzhamTables.PositionBase[compSlot];

          // Within one slot prefer the distance whose low nibble is smaller: it is coded on its own.
          if (compSlot < bestSlot || (compSlot >= 8 && compSlot == bestSlot && (compOfs & 15) < (bestOfs & 15)))
            _tempDist[numMatches - 1] = deltaPos;
          else if (matchLen < maxMatchLen && compSlot <= bestSlot) {
            // Otherwise prefer the mismatch byte closest in Hamming distance to the wanted one.
            uint desired = data[insertPos + matchLen];
            uint current = data[insertPos - bestMatchDist + matchLen];
            uint candidate = data[curPos + matchLen];
            if (HammingDistance[(int)(candidate ^ desired)] < HammingDistance[(int)(current ^ desired)])
              _tempDist[numMatches - 1] = deltaPos;
          }
        }

        uint newPos;
        if (data[curPos + matchLen] < data[insertPos + matchLen]) {
          pLeft = curPos;
          pLeft = ref _right[node];
          newPos = _right[node];
        } else {
          pRight = curPos;
          pRight = ref _left[node];
          newPos = _left[node];
        }

        if (newPos == curPos)
          break;
        curPos = newPos;
      }

      var refIndex = (int)(fillLookaheadPos - _fillLookaheadPos);
      if (numMatches > 0) {
        _tempDist[numMatches - 1] |= LastMatchFlag;
        var toWrite = (int)Math.Min((uint)numMatches, _maxMatches);
        var at = _nextMatchRef;
        _nextMatchRef += toWrite;
        _tempLen.AsSpan(numMatches - toWrite, toWrite).CopyTo(_matchLen.AsSpan(at));
        _tempDist.AsSpan(numMatches - toWrite, toWrite).CopyTo(_matchDist.AsSpan(at));
        _matchRefs[refIndex] = at;
      } else
        _matchRefs[refIndex] = -2;

      ++fillLookaheadPos;
      --fillLookaheadSize;
      ++fillDictSize;
    }

    while (fillLookaheadSize > 0) {
      var insertNode = (int)(fillLookaheadPos & _nodeMask);
      _left[insertNode] = 0;
      _right[insertNode] = 0;
      _matchRefs[(int)(fillLookaheadPos - _fillLookaheadPos)] = -2;
      ++fillLookaheadPos;
      --fillLookaheadSize;
      ++fillDictSize;
    }
  }

  /// <summary><c>find_len2_matches</c>: chains each digram of the block to its previous occurrence.</summary>
  private void FindLen2Matches() {
    if (_digramNext.Length < LookaheadSize)
      _digramNext = new uint[LookaheadSize];
    var data = _data;
    var position = LookaheadPos;
    for (var ofs = 0; ofs < (int)LookaheadSize - 1; ++ofs, ++position) {
      var h = Hash2(data[position], data[position + 1]) & (DigramHashSize - 1);
      _digramNext[ofs] = _digramHash[h];
      _digramHash[h] = LookaheadPos + (uint)ofs;
    }

    _digramNext[LookaheadSize - 1] = 0;
  }

  /// <summary><c>get_len2_match</c>: the distance of the nearest earlier digram that matches, or 0.</summary>
  public uint GetLen2Match(uint lookaheadOfs) {
    if (_fillLookaheadSize - lookaheadOfs < 2)
      return 0;
    var curPos = LookaheadPos + lookaheadOfs;
    var nextMatchPos = _digramNext[curPos - _fillLookaheadPos];
    var matchDist = curPos - nextMatchPos;
    if (matchDist == 0 || matchDist > LzhamTables.MaxLen2MatchDist || matchDist > CurDictSize + lookaheadOfs)
      return 0;
    return _data[curPos] == _data[nextMatchPos] && _data[curPos + 1] == _data[nextMatchPos + 1] ? matchDist : 0;
  }
}
