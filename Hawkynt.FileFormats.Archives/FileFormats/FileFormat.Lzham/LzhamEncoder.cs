// Converted from LZHAM (Rich Geldreich), public domain: lzham_lzcomp_internal.cpp
// (compress_block_internal, optimal_parse, extreme_parse, node::add_state, send_final_block),
// lzham_lzcomp_state.cpp (state::encode, the cost functions, partial_advance, encode_eob) and
// lzham_symbol_codec.cpp (the encoding side and g_prob_cost). See THIRD-PARTY-NOTICE.lzham.txt.
namespace FileFormat.Lzham;

/// <summary>LZHAM's compression levels (<c>lzham_compress_level</c>, lzhamtest <c>-m0</c>..<c>-m4</c>).</summary>
public enum LzhamCompressionLevel {
  /// <summary>Two probes per position, one match kept.</summary>
  Fastest = 0,
  /// <summary>Twelve probes, six matches.</summary>
  Faster = 1,
  /// <summary>Sixteen probes.</summary>
  Default = 2,
  /// <summary>Thirty-two probes.</summary>
  Better = 3,
  /// <summary>128 probes; lzhamtest's default.</summary>
  Uber = 4,
}

/// <summary>
/// LZHAM's compressor without helper threads: the match accelerator finds matches a block ahead, the
/// optimal parser (or, at <see cref="LzhamCompressionLevel.Uber"/> with extreme parsing, the parser
/// that keeps four candidate states per byte) picks decisions for up to 3072 bytes at a time, and
/// the coding state writes them. Given the same parameters the output is byte for byte what
/// <c>lzhamtest -t0</c> writes.
/// </summary>
internal sealed class LzhamEncoder {
  private const int MaxParseGraphNodes = 3072;

  // comp_settings: fast bytes, matches kept per position, probes per position.
  private static readonly (uint FastBytes, uint MaxMatches, uint MaxProbes)[] LevelSettings = [
    (8, 1, 2),
    (24, 6, 12),
    (32, uint.MaxValue, 16),
    (48, uint.MaxValue, 32),
    (64, uint.MaxValue, 128),
  ];

  private readonly byte[] _data;
  private readonly int _blockSize;
  private readonly uint _fastBytes;
  private readonly bool _extremeParsing;
  private readonly LzhamMatchAccelerator _accel;

  private LzhamEncoderState _state;
  private readonly List<byte> _output = [];
  private int _blockIndex;
  private readonly LzhamParser _parser;

  private LzhamEncoder(byte[] data, int dictSizeLog2, int tableUpdateRate, LzhamCompressionLevel level, bool extremeParsing) {
    _data = data;
    var dictSize = 1 << dictSizeLog2;
    _blockSize = Math.Min(512 * 1024, dictSize / 8);
    var settings = LevelSettings[(int)level];
    _fastBytes = settings.FastBytes;
    _extremeParsing = extremeParsing && level == LzhamCompressionLevel.Uber;
    _accel = new LzhamMatchAccelerator(data, dictSizeLog2, settings.MaxMatches, settings.MaxProbes);
    _state = new LzhamEncoderState(dictSizeLog2, tableUpdateRate);
    _parser = new LzhamParser(data, _accel, _fastBytes, _extremeParsing);
  }

  /// <summary>Compresses <paramref name="data"/> into a raw LZHAM stream.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, int dictSizeLog2, int tableUpdateRate, LzhamCompressionLevel level = LzhamCompressionLevel.Uber, bool extremeParsing = false) {
    if (dictSizeLog2 is < LzhamTables.MinDictSizeLog2 or > LzhamTables.MaxDictSizeLog2)
      throw new ArgumentOutOfRangeException(nameof(dictSizeLog2));
    if (level is < LzhamCompressionLevel.Fastest or > LzhamCompressionLevel.Uber)
      throw new ArgumentOutOfRangeException(nameof(level));
    var encoder = new LzhamEncoder(data.ToArray(), dictSizeLog2, tableUpdateRate, level, extremeParsing);
    return encoder.Run();
  }

  private byte[] Run() {
    // put_bytes hands the compressor whole blocks; compress_block splits a block where it would
    // cross the end of the dictionary ring.
    for (var offset = 0; offset < _data.Length;) {
      var remaining = (uint)Math.Min(_blockSize, _data.Length - offset);
      while (remaining > 0) {
        var length = Math.Min(_accel.MaxAddBytes, remaining);
        CompressBlock(offset, (int)length);
        offset += (int)length;
        remaining -= length;
      }
    }

    // send_final_block: the EOF block, then the Adler-32 of the input.
    var writer = new LzhamSymbolWriter();
    writer.EncodeBits(LzhamTables.EofBlock, LzhamTables.BlockHeaderBits);
    writer.EncodeAlignToByte();
    writer.EncodeBits(LzhamDecoder.Adler32(_data), 32);
    _output.AddRange(writer.Finish());
    return [.. _output];
  }

  /// <summary><c>compress_block_internal</c> with one parse thread.</summary>
  private void CompressBlock(int start, int length) {
    _accel.AddBytes((uint)length);
    var startState = _state.Clone();

    var writer = new LzhamSymbolWriter();
    writer.EncodeBits(LzhamTables.CompBlock, LzhamTables.BlockHeaderBits);
    writer.EncodeArithInit();
    _state.StartOfBlock();
    writer.EncodeBits(0, LzhamTables.BlockFlushTypeBits);

    var position = start;
    var bytesToMatch = length;
    while (bytesToMatch > 0) {
      // The first block is parsed in small pieces so the models get going sooner.
      var chunk = Math.Min(bytesToMatch, MaxParseGraphNodes);
      if (_blockIndex == 0 && position - start < MaxParseGraphNodes)
        chunk = Math.Min(chunk, 1536);

      var decisions = _parser.Parse(_state, position, chunk);
      for (var i = decisions.Count - 1; i >= 0; --i) {
        var decision = decisions[i];
        _state.Encode(writer, _data, decision);
        var len = decision.CodedLength;
        position += len;
        bytesToMatch -= len;
        _accel.AdvanceBytes((uint)len);
      }
    }

    _state.EncodeEndOfBlock(writer);
    var compressed = writer.Finish();

    if (compressed.Length >= length) {
      // A block that does not shrink is sent raw, from the state it started with.
      _state = startState;
      var raw = new LzhamSymbolWriter();
      raw.EncodeBits(LzhamTables.RawBlock, LzhamTables.BlockHeaderBits);
      raw.EncodeBits((uint)(length - 1), 24);
      var n = (uint)(length - 1);
      raw.EncodeBits((n & 0xFF) ^ ((n >> 8) & 0xFF) ^ ((n >> 16) & 0xFF), 8);
      raw.EncodeAlignToByte();
      for (var i = start; i < start + length; ++i)
        raw.EncodeBits(_data[i], 8);
      compressed = raw.Finish();
    }

    _output.AddRange(compressed);
    ++_blockIndex;
  }
}

/// <summary>
/// One LZ decision (<c>lzdecision</c>): a literal (<see cref="Len"/> 0), a repeat match
/// (<see cref="Dist"/> -1..-4 for repeat distance 0..3; length 1 with -1 is the single-byte repeat)
/// or a full match at a positive distance.
/// </summary>
internal readonly record struct LzhamDecision(int Pos, int Len, int Dist) {
  public bool IsMatch => Len > 0;
  public int CodedLength => Math.Max(Len, 1);
}

/// <summary>
/// The part of the coding state a parse follows from byte to byte (<c>state_base</c>): the literal /
/// match state and the four repeat distances. The models stay as they were when the parse began.
/// </summary>
internal struct LzhamPartialState : IEquatable<LzhamPartialState> {
  public int CurState;
  public uint Hist0, Hist1, Hist2, Hist3;

  public readonly uint Hist(int index) => index switch { 0 => Hist0, 1 => Hist1, 2 => Hist2, _ => Hist3 };

  /// <summary><c>partial_advance</c>.</summary>
  public void Advance(in LzhamDecision decision) {
    if (decision.Len == 0) {
      CurState = CurState < 4 ? 0 : CurState < 10 ? CurState - 3 : CurState - 6;
      return;
    }

    if (decision.Dist < 0) {
      var index = -decision.Dist - 1;
      if (index == 0) {
        CurState = decision.Len == 1
          ? (CurState < LzhamTables.NumLitStates ? 9 : 11)
          : (CurState < LzhamTables.NumLitStates ? 8 : 11);
        return;
      }

      if (index == 1)
        (Hist0, Hist1) = (Hist1, Hist0);
      else if (index == 2)
        (Hist0, Hist1, Hist2) = (Hist2, Hist0, Hist1);
      else
        (Hist0, Hist1, Hist2, Hist3) = (Hist3, Hist0, Hist1, Hist2);
      CurState = CurState < LzhamTables.NumLitStates ? 8 : 11;
      return;
    }

    (Hist3, Hist2, Hist1, Hist0) = (Hist2, Hist1, Hist0, (uint)decision.Dist);
    CurState = CurState < LzhamTables.NumLitStates ? LzhamTables.NumLitStates : LzhamTables.NumLitStates + 3;
  }

  public readonly bool Equals(LzhamPartialState other)
    => CurState == other.CurState && Hist0 == other.Hist0 && Hist1 == other.Hist1 && Hist2 == other.Hist2 && Hist3 == other.Hist3;

  public override readonly bool Equals(object? obj) => obj is LzhamPartialState other && Equals(other);

  public override readonly int GetHashCode() => HashCode.Combine(CurState, Hist0, Hist1, Hist2, Hist3);
}

/// <summary>
/// LZHAM's parsers. Both walk the bytes of one piece left to right and relax every decision that
/// starts at the current byte into the node it ends at; costs are fractional bits scaled by 2^24
/// and priced with the models as they stood when the piece began; ties go to the decision with the
/// lower "complexity" (literals cheapest, then rep0..rep3, then long and short matches).
/// </summary>
internal sealed class LzhamParser {
  private const int MaxParseGraphNodes = 3072;
  private const int MaxNodeStates = 4;
  private const uint LitComplexity = 1;
  private const uint Rep0Complexity = 2;
  private const uint LongMatchComplexity = 6;
  private const int LongMatchComplexityLenThresh = 9;
  private const uint ShortMatchComplexity = 7;

  private struct NodeState {
    public ulong TotalCost;
    public uint TotalComplexity;
    public short ParentIndex;
    public sbyte ParentStateIndex;
    public LzhamPartialState SavedState;
    public LzhamDecision Decision;
  }

  private readonly byte[] _data;
  private readonly LzhamMatchAccelerator _accel;
  private readonly uint _fastBytes;
  private readonly bool _extreme;

  private readonly NodeState[] _nodes;
  private readonly int[] _numNodeStates;
  private readonly ulong[] _bitCosts = new ulong[LzhamTables.MaxMatchLen + 1];
  private readonly uint[] _matchLens = new uint[128];
  private readonly uint[] _matchDistances = new uint[128];
  private readonly List<LzhamDecision> _decisions = new(MaxParseGraphNodes);

  public LzhamParser(byte[] data, LzhamMatchAccelerator accel, uint fastBytes, bool extreme) {
    _data = data;
    _accel = accel;
    _fastBytes = fastBytes;
    _extreme = extreme;
    _nodes = new NodeState[(MaxParseGraphNodes + 1) * (extreme ? MaxNodeStates : 1)];
    _numNodeStates = extreme ? new int[MaxParseGraphNodes + 1] : [];
  }

  /// <summary>The decisions for <paramref name="bytesToParse"/> bytes at <paramref name="start"/>, last one first.</summary>
  public List<LzhamDecision> Parse(LzhamEncoderState state, int start, int bytesToParse) {
    _decisions.Clear();
    if (_extreme)
      ExtremeParse(state, start, bytesToParse);
    else
      OptimalParse(state, start, bytesToParse);
    return _decisions;
  }

  private uint RepMatchLength(int position, uint dist, uint findDictSize, int maxLen) {
    if (dist > findDictSize)
      return 0;
    return (uint)_data.AsSpan(position - (int)dist, maxLen).CommonPrefixLength(_data.AsSpan(position, maxLen));
  }

  /// <summary><c>optimal_parse</c>: one state per node.</summary>
  private void OptimalParse(LzhamEncoderState state, int start, int bytesToParse) {
    var nodes = _nodes;
    nodes[0].ParentIndex = -1;
    nodes[0].TotalCost = 0;
    nodes[0].TotalComplexity = 0;
    for (var i = 1; i <= MaxParseGraphNodes; ++i) {
      nodes[i].TotalCost = ulong.MaxValue;
      nodes[i].TotalComplexity = uint.MaxValue;
      nodes[i].ParentIndex = -1;
    }

    var approx = state.PartialState;
    var bitCosts = _bitCosts;

    var curPos = start;
    var curLookaheadOfs = (uint)(start - _accel.LookaheadPos);
    var curNodeIndex = 0;

    while (curNodeIndex < bytesToParse) {
      var maxAdmissible = Math.Min(LzhamTables.MaxMatchLen, bytesToParse - curNodeIndex);
      var findDictSize = _accel.CurDictSize + curLookaheadOfs;

      if (curNodeIndex > 0) {
        approx = nodes[curNodeIndex].SavedState;
        approx.Advance(nodes[curNodeIndex].Decision);
      }

      var curTotalCost = nodes[curNodeIndex].TotalCost;
      var curTotalComplexity = nodes[curNodeIndex].TotalComplexity;
      var isMatchModelIndex = approx.CurState;

      // rep matches
      uint matchHistMaxLen = 0;
      var matchHistMinMatchLen = 1;
      for (var repIndex = 0; repIndex < 4; ++repIndex) {
        var histMatchLen = RepMatchLength(curPos, approx.Hist(repIndex), findDictSize, maxAdmissible);
        if (histMatchLen >= matchHistMinMatchLen) {
          matchHistMaxLen = Math.Max(matchHistMaxLen, histMatchLen);
          state.GetRepMatchCosts(approx, bitCosts, repIndex, matchHistMinMatchLen, (int)histMatchLen, isMatchModelIndex);
          var repComplexity = curTotalComplexity + (Rep0Complexity + (uint)repIndex);
          for (var l = matchHistMinMatchLen; l <= histMatchLen; ++l) {
            ref var dst = ref nodes[curNodeIndex + l];
            var cost = curTotalCost + bitCosts[l];
            if (cost > dst.TotalCost || (cost == dst.TotalCost && repComplexity >= dst.TotalComplexity))
              continue;
            dst.TotalCost = cost;
            dst.TotalComplexity = repComplexity;
            dst.ParentIndex = (short)curNodeIndex;
            dst.SavedState = approx;
            dst.Decision = new(curPos, l, -(repIndex + 1));
          }
        }

        matchHistMinMatchLen = LzhamTables.MinMatchLen;
      }

      var maxMatchLen = matchHistMaxLen;
      if (maxMatchLen >= _fastBytes) {
        curPos += (int)maxMatchLen;
        curLookaheadOfs += maxMatchLen;
        curNodeIndex += (int)maxMatchLen;
        continue;
      }

      // full matches
      if (maxAdmissible >= LzhamTables.MinMatchLen) {
        var numFullMatches = 0;

        if (matchHistMaxLen < 2) {
          // The nearest length-2 match, when no repeat distance gave one.
          var len2Dist = _accel.GetLen2Match(curLookaheadOfs);
          if (len2Dist != 0) {
            var cost = curTotalCost + state.GetLen2MatchCost(approx, len2Dist, isMatchModelIndex);
            var complexity = curTotalComplexity + ShortMatchComplexity;
            ref var dst = ref nodes[curNodeIndex + 2];
            if (cost < dst.TotalCost || (cost == dst.TotalCost && complexity < dst.TotalComplexity)) {
              dst.TotalCost = cost;
              dst.TotalComplexity = complexity;
              dst.ParentIndex = (short)curNodeIndex;
              dst.SavedState = approx;
              dst.Decision = new(curPos, 2, (int)len2Dist);
            }

            maxMatchLen = 2;
          }
        }

        var minTruncateMatchLen = maxMatchLen;

        var match = _accel.FindMatches(curLookaheadOfs);
        if (match >= 0)
          for (;; ++match) {
            var matchLen = Math.Min(_accel.MatchLength(match), (uint)maxAdmissible);
            if (matchLen > maxMatchLen) {
              maxMatchLen = matchLen;
              _matchLens[numFullMatches] = matchLen;
              _matchDistances[numFullMatches] = _accel.MatchDistance(match);
              ++numFullMatches;
            }

            if (_accel.IsLast(match))
              break;
          }

        if (numFullMatches > 0) {
          var prevMaxMatchLen = Math.Max(1u, minTruncateMatchLen);
          for (var i = 0; i < numFullMatches; ++i) {
            var startLen = (int)prevMaxMatchLen + 1;
            var endLen = (int)_matchLens[i];
            var matchDist = _matchDistances[i];
            state.GetFullMatchCosts(approx, bitCosts, matchDist, startLen, endLen, isMatchModelIndex);
            for (var l = startLen; l <= endLen; ++l) {
              var complexity = curTotalComplexity + (l >= LongMatchComplexityLenThresh ? LongMatchComplexity : ShortMatchComplexity);
              ref var dst = ref nodes[curNodeIndex + l];
              var cost = curTotalCost + bitCosts[l];
              if (cost > dst.TotalCost || (cost == dst.TotalCost && complexity >= dst.TotalComplexity))
                continue;
              dst.TotalCost = cost;
              dst.TotalComplexity = complexity;
              dst.ParentIndex = (short)curNodeIndex;
              dst.SavedState = approx;
              dst.Decision = new(curPos, l, (int)matchDist);
            }

            prevMaxMatchLen = (uint)endLen;
          }
        }
      }

      if (maxMatchLen >= _fastBytes) {
        curPos += (int)maxMatchLen;
        curLookaheadOfs += maxMatchLen;
        curNodeIndex += (int)maxMatchLen;
        continue;
      }

      // literal
      {
        var cost = curTotalCost + state.GetLitCost(approx, _data, curPos, isMatchModelIndex);
        var complexity = curTotalComplexity + LitComplexity;
        ref var dst = ref nodes[curNodeIndex + 1];
        if (cost < dst.TotalCost || (cost == dst.TotalCost && complexity < dst.TotalComplexity)) {
          dst.TotalCost = cost;
          dst.TotalComplexity = complexity;
          dst.ParentIndex = (short)curNodeIndex;
          dst.SavedState = approx;
          dst.Decision = new(curPos, 0, 0);
        }
      }

      ++curPos;
      ++curLookaheadOfs;
      ++curNodeIndex;
    }

    // Walk back from the goal node.
    var nodeIndex = bytesToParse;
    do {
      _decisions.Add(nodes[nodeIndex].Decision);
      nodeIndex = nodes[nodeIndex].ParentIndex;
    } while (nodeIndex > 0);
  }

  /// <summary><c>node::add_state</c>: keeps a node's best four distinct partial states, cheapest first.</summary>
  private void AddState(int nodeIndex, int parentIndex, int parentStateIndex, in LzhamDecision decision, in LzhamPartialState parentState, ulong totalCost, uint totalComplexity) {
    var trial = parentState;
    trial.Advance(decision);

    var states = _nodes.AsSpan(nodeIndex * MaxNodeStates, MaxNodeStates);
    ref var count = ref _numNodeStates[nodeIndex];

    for (var i = count - 1; i >= 0; --i) {
      ref var current = ref states[i];
      if (!current.SavedState.Equals(trial))
        continue;

      if (totalCost < current.TotalCost || (totalCost == current.TotalCost && totalComplexity < current.TotalComplexity)) {
        current.ParentIndex = (short)parentIndex;
        current.ParentStateIndex = (sbyte)parentStateIndex;
        current.Decision = decision;
        current.TotalCost = totalCost;
        current.TotalComplexity = totalComplexity;

        while (i > 0) {
          if (states[i].TotalCost < states[i - 1].TotalCost
              || (states[i].TotalCost == states[i - 1].TotalCost && states[i].TotalComplexity < states[i - 1].TotalComplexity)) {
            (states[i], states[i - 1]) = (states[i - 1], states[i]);
            --i;
          } else
            break;
        }
      }

      return;
    }

    int insertIndex;
    for (insertIndex = count; insertIndex > 0; --insertIndex) {
      ref var current = ref states[insertIndex - 1];
      if (totalCost > current.TotalCost || (totalCost == current.TotalCost && totalComplexity >= current.TotalComplexity))
        break;
    }

    if (insertIndex == MaxNodeStates)
      return;

    var numBehind = count - insertIndex;
    var numToMove = count < MaxNodeStates ? numBehind : numBehind - 1;
    if (numToMove > 0)
      states.Slice(insertIndex, numToMove).CopyTo(states[(insertIndex + 1)..]);

    ref var inserted = ref states[insertIndex];
    inserted.ParentIndex = (short)parentIndex;
    inserted.ParentStateIndex = (sbyte)parentStateIndex;
    inserted.Decision = decision;
    inserted.TotalCost = totalCost;
    inserted.TotalComplexity = totalComplexity;
    inserted.SavedState = trial;

    count = Math.Min(count + 1, MaxNodeStates);
  }

  /// <summary><c>extreme_parse</c>: up to four candidate states per node.</summary>
  private void ExtremeParse(LzhamEncoderState state, int start, int bytesToParse) {
    Array.Clear(_numNodeStates);

    var initial = state.PartialState;
    _numNodeStates[0] = 1;
    ref var first = ref _nodes[0];
    first.SavedState = initial;
    first.ParentIndex = -1;
    first.ParentStateIndex = -1;
    first.TotalCost = 0;
    first.TotalComplexity = 0;

    var bitCosts = _bitCosts;
    var curPos = start;
    var curLookaheadOfs = (uint)(start - _accel.LookaheadPos);
    var curNodeIndex = 0;

    while (curNodeIndex < bytesToParse) {
      var maxAdmissible = Math.Min(LzhamTables.MaxMatchLen, bytesToParse - curNodeIndex);
      var findDictSize = _accel.CurDictSize + curLookaheadOfs;

      // full matches, shared by every state of this node
      uint maxFullMatchLen = 0;
      var numFullMatches = 0;
      uint len2MatchDist = 0;
      if (maxAdmissible >= LzhamTables.MinMatchLen) {
        var match = _accel.FindMatches(curLookaheadOfs);
        if (match >= 0)
          for (;; ++match) {
            var matchLen = Math.Min(_accel.MatchLength(match), (uint)maxAdmissible);
            if (matchLen > maxFullMatchLen) {
              maxFullMatchLen = matchLen;
              _matchLens[numFullMatches] = matchLen;
              _matchDistances[numFullMatches] = _accel.MatchDistance(match);
              ++numFullMatches;
            }

            if (_accel.IsLast(match))
              break;
          }

        len2MatchDist = _accel.GetLen2Match(curLookaheadOfs);
      }

      var approx = initial;
      for (var stateIndex = 0; stateIndex < _numNodeStates[curNodeIndex]; ++stateIndex) {
        var nodeState = _nodes[curNodeIndex * MaxNodeStates + stateIndex];
        if (curNodeIndex > 0)
          approx = nodeState.SavedState;

        var isMatchModelIndex = approx.CurState;
        var curTotalCost = nodeState.TotalCost;
        var curTotalComplexity = nodeState.TotalComplexity;

        // rep matches
        uint matchHistMaxLen = 0;
        var matchHistMinMatchLen = 1;
        for (var repIndex = 0; repIndex < 4; ++repIndex) {
          var histMatchLen = RepMatchLength(curPos, approx.Hist(repIndex), findDictSize, maxAdmissible);
          if (histMatchLen >= matchHistMinMatchLen) {
            matchHistMaxLen = Math.Max(matchHistMaxLen, histMatchLen);
            state.GetRepMatchCosts(approx, bitCosts, repIndex, matchHistMinMatchLen, (int)histMatchLen, isMatchModelIndex);
            var repComplexity = curTotalComplexity + (Rep0Complexity + (uint)repIndex);
            for (var l = matchHistMinMatchLen; l <= histMatchLen; ++l)
              AddState(curNodeIndex + l, curNodeIndex, stateIndex, new(curPos, l, -(repIndex + 1)), approx, curTotalCost + bitCosts[l], repComplexity);
          }

          matchHistMinMatchLen = LzhamTables.MinMatchLen;
        }

        var minTruncateMatchLen = matchHistMaxLen;

        // nearest length-2 match
        if (len2MatchDist != 0) {
          var decision = new LzhamDecision(curPos, 2, (int)len2MatchDist);
          AddState(curNodeIndex + 2, curNodeIndex, stateIndex, decision, approx,
            curTotalCost + state.GetLen2MatchCost(approx, len2MatchDist, isMatchModelIndex), curTotalComplexity + ShortMatchComplexity);
          minTruncateMatchLen = Math.Max(minTruncateMatchLen, 2);
        }

        // full matches
        if (maxFullMatchLen > minTruncateMatchLen) {
          var prevMaxMatchLen = Math.Max(1u, minTruncateMatchLen);
          for (var i = 0; i < numFullMatches; ++i) {
            var endLen = _matchLens[i];
            if (endLen <= minTruncateMatchLen)
              continue;
            var startLen = (int)prevMaxMatchLen + 1;
            var matchDist = _matchDistances[i];
            state.GetFullMatchCosts(approx, bitCosts, matchDist, startLen, (int)endLen, isMatchModelIndex);
            for (var l = startLen; l <= endLen; ++l) {
              var complexity = l >= LongMatchComplexityLenThresh ? LongMatchComplexity : ShortMatchComplexity;
              AddState(curNodeIndex + l, curNodeIndex, stateIndex, new(curPos, l, (int)matchDist), approx, curTotalCost + bitCosts[l], curTotalComplexity + complexity);
            }

            prevMaxMatchLen = endLen;
          }
        }

        // literal
        AddState(curNodeIndex + 1, curNodeIndex, stateIndex, new(curPos, 0, 0), approx,
          curTotalCost + state.GetLitCost(approx, _data, curPos, isMatchModelIndex), curTotalComplexity + LitComplexity);
      }

      ++curPos;
      ++curLookaheadOfs;
      ++curNodeIndex;
    }

    // The cheapest state at the goal node, then back through the parents.
    var lowestFinalCost = ulong.MaxValue;
    var nodeStateIndex = 0;
    for (var i = 0; i < _numNodeStates[bytesToParse]; ++i)
      if (_nodes[bytesToParse * MaxNodeStates + i].TotalCost < lowestFinalCost) {
        lowestFinalCost = _nodes[bytesToParse * MaxNodeStates + i].TotalCost;
        nodeStateIndex = i;
      }

    var nodeIndex = bytesToParse;
    do {
      ref var current = ref _nodes[nodeIndex * MaxNodeStates + nodeStateIndex];
      _decisions.Add(current.Decision);
      nodeIndex = current.ParentIndex;
      nodeStateIndex = current.ParentStateIndex;
    } while (nodeIndex > 0);
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

  public void StartOfBlock() {
    _matchHist.AsSpan().Fill(1);
    _curState = 0;
  }

  /// <summary>The state a parse starts from (<c>save_partial_state</c>).</summary>
  public LzhamPartialState PartialState => new() {
    CurState = _curState,
    Hist0 = (uint)_matchHist[0],
    Hist1 = (uint)_matchHist[1],
    Hist2 = (uint)_matchHist[2],
    Hist3 = (uint)_matchHist[3],
  };

  /// <summary><c>state::encode</c>: codes one decision and moves the models on.</summary>
  public void Encode(LzhamSymbolWriter writer, byte[] data, in LzhamDecision decision) {
    if (!decision.IsMatch)
      EncodeLiteral(writer, data, decision.Pos);
    else if (decision.Dist < 0)
      EncodeRepMatch(writer, -decision.Dist - 1, decision.Len);
    else
      EncodeFullMatch(writer, decision.Dist, decision.Len);
  }

  // ── Costs (lzham_lzcomp_state.cpp): fractional bits scaled by 2^24 ──────────────────

  private const int BitCostScaleShift = 24;

  /// <summary><c>g_prob_cost</c>: the scaled cost of a bit whose probability is <c>i</c>/2048.</summary>
  private static readonly uint[] ProbCost = [
    0x0, 0xB000000, 0xA000000, 0x96A3FE6, 0x9000000, 0x8AD961F, 0x86A3FE6, 0x8315130, 0x8000000, 0x7D47FCC, 0x7AD961F,
    0x78A62B0, 0x76A3FE6, 0x74CAFFC, 0x7315130, 0x717D605, 0x7000000, 0x6E99C09, 0x6D47FCC, 0x6C087D3, 0x6AD961F, 0x69B9116,
    0x68A62B0, 0x679F7D8, 0x66A3FE6, 0x65B2C3E, 0x64CAFFC, 0x63EBFB1, 0x6315130, 0x6245B5C, 0x617D605, 0x60BB9CA, 0x6000000,
    0x5F4A296, 0x5E99C09, 0x5DEE74F, 0x5D47FCC, 0x5CA6144, 0x5C087D3, 0x5B6EFE1, 0x5AD961F, 0x5A47779, 0x59B9116, 0x592E050,
    0x58A62B0, 0x58215EA, 0x579F7D8, 0x5720677, 0x56A3FE6, 0x562A260, 0x55B2C3E, 0x553DBEF, 0x54CAFFC, 0x545A701, 0x53EBFB1,
    0x537F8CF, 0x5315130, 0x52AC7B8, 0x5245B5C, 0x51E0B1B, 0x517D605, 0x511BB33, 0x50BB9CA, 0x505D0FC, 0x5000000, 0x4FA461A,
    0x4F4A296, 0x4EF14C7, 0x4E99C09, 0x4E437BE, 0x4DEE74F, 0x4D9AA2C, 0x4D47FCC, 0x4CF67A8, 0x4CA6144, 0x4C56C23, 0x4C087D3,
    0x4BBB3E1, 0x4B6EFE1, 0x4B23B6D, 0x4AD961F, 0x4A8FF97, 0x4A47779, 0x49FFD6A, 0x49B9116, 0x4973228, 0x492E050, 0x48E9B41,
    0x48A62B0, 0x4863655, 0x48215EA, 0x47E012C, 0x479F7D8, 0x475F9B0, 0x4720677, 0x46E1DF1, 0x46A3FE6, 0x4666C1D, 0x462A260,
    0x45EE27C, 0x45B2C3E, 0x4577F74, 0x453DBEF, 0x4504180, 0x44CAFFC, 0x4492735, 0x445A701, 0x4422F38, 0x43EBFB1, 0x43B5846,
    0x437F8CF, 0x434A129, 0x4315130, 0x42E08C0, 0x42AC7B8, 0x4278DF7, 0x4245B5C, 0x4212FC7, 0x41E0B1B, 0x41AED39, 0x417D605,
    0x414C561, 0x411BB33, 0x40EB75F, 0x40BB9CA, 0x408C25C, 0x405D0FC, 0x402E58F, 0x4000000, 0x3FD2036, 0x3FA461A, 0x3F77197,
    0x3F4A296, 0x3F1D903, 0x3EF14C7, 0x3EC55D0, 0x3E99C09, 0x3E6E75F, 0x3E437BE, 0x3E18D14, 0x3DEE74F, 0x3DC465D, 0x3D9AA2C,
    0x3D712AC, 0x3D47FCC, 0x3D1F17A, 0x3CF67A8, 0x3CCE246, 0x3CA6144, 0x3C7E492, 0x3C56C23, 0x3C2F7E8, 0x3C087D3, 0x3BE1BD5,
    0x3BBB3E1, 0x3B94FE9, 0x3B6EFE1, 0x3B493BC, 0x3B23B6D, 0x3AFE6E7, 0x3AD961F, 0x3AB4908, 0x3A8FF97, 0x3A6B9C0, 0x3A47779,
    0x3A238B5, 0x39FFD6A, 0x39DC58E, 0x39B9116, 0x3995FF7, 0x3973228, 0x395079E, 0x392E050, 0x390BC34, 0x38E9B41, 0x38C7D6E,
    0x38A62B0, 0x3884B01, 0x3863655, 0x38424A6, 0x38215EA, 0x3800A1A, 0x37E012C, 0x37BFB18, 0x379F7D8, 0x377F762, 0x375F9B0,
    0x373FEBA, 0x3720677, 0x37010E1, 0x36E1DF1, 0x36C2DA0, 0x36A3FE6, 0x36854BC, 0x3666C1D, 0x3648600, 0x362A260, 0x360C136,
    0x35EE27C, 0x35D062B, 0x35B2C3E, 0x35954AD, 0x3577F74, 0x355AC8C, 0x353DBEF, 0x3520D98, 0x3504180, 0x34E77A4, 0x34CAFFC,
    0x34AEA83, 0x3492735, 0x347660B, 0x345A701, 0x343EA12, 0x3422F38, 0x340766F, 0x33EBFB1, 0x33D0AFA, 0x33B5846, 0x339A78E,
    0x337F8CF, 0x3364C05, 0x334A129, 0x332F839, 0x3315130, 0x32FAC09, 0x32E08C0, 0x32C6751, 0x32AC7B8, 0x32929F1, 0x3278DF7,
    0x325F3C6, 0x3245B5C, 0x322C4B2, 0x3212FC7, 0x31F9C96, 0x31E0B1B, 0x31C7B53, 0x31AED39, 0x31960CB, 0x317D605, 0x3164CE2,
    0x314C561, 0x3133F7D, 0x311BB33, 0x310387F, 0x30EB75F, 0x30D37CE, 0x30BB9CA, 0x30A3D50, 0x308C25C, 0x30748EC, 0x305D0FC,
    0x3045A88, 0x302E58F, 0x301720E, 0x3000000, 0x2FE8F64, 0x2FD2036, 0x2FBB274, 0x2FA461A, 0x2F8DB27, 0x2F77197, 0x2F60968,
    0x2F4A296, 0x2F33D20, 0x2F1D903, 0x2F0763B, 0x2EF14C7, 0x2EDB4A5, 0x2EC55D0, 0x2EAF848, 0x2E99C09, 0x2E84111, 0x2E6E75F,
    0x2E58EEE, 0x2E437BE, 0x2E2E1CB, 0x2E18D14, 0x2E03996, 0x2DEE74F, 0x2DD963D, 0x2DC465D, 0x2DAF7AD, 0x2D9AA2C, 0x2D85DD7,
    0x2D712AC, 0x2D5C8A9, 0x2D47FCC, 0x2D33812, 0x2D1F17A, 0x2D0AC02, 0x2CF67A8, 0x2CE246A, 0x2CCE246, 0x2CBA13A, 0x2CA6144,
    0x2C92262, 0x2C7E492, 0x2C6A7D4, 0x2C56C23, 0x2C43180, 0x2C2F7E8, 0x2C1BF5A, 0x2C087D3, 0x2BF5151, 0x2BE1BD5, 0x2BCE75A,
    0x2BBB3E1, 0x2BA8166, 0x2B94FE9, 0x2B81F68, 0x2B6EFE1, 0x2B5C153, 0x2B493BC, 0x2B3671A, 0x2B23B6D, 0x2B110B1, 0x2AFE6E7,
    0x2AEBE0C, 0x2AD961F, 0x2AC6F1E, 0x2AB4908, 0x2AA23DC, 0x2A8FF97, 0x2A7DC39, 0x2A6B9C0, 0x2A5982B, 0x2A47779, 0x2A357A7,
    0x2A238B5, 0x2A11AA1, 0x29FFD6A, 0x29EE10F, 0x29DC58E, 0x29CAAE6, 0x29B9116, 0x29A781C, 0x2995FF7, 0x29848A6, 0x2973228,
    0x2961C7B, 0x295079E, 0x293F390, 0x292E050, 0x291CDDD, 0x290BC34, 0x28FAB56, 0x28E9B41, 0x28D8BF4, 0x28C7D6E, 0x28B6FAD,
    0x28A62B0, 0x2895677, 0x2884B01, 0x287404B, 0x2863655, 0x2852D1F, 0x28424A6, 0x2831CEA, 0x28215EA, 0x2810FA5, 0x2800A1A,
    0x27F0547, 0x27E012C, 0x27CFDC7, 0x27BFB18, 0x27AF91E, 0x279F7D8, 0x278F744, 0x277F762, 0x276F831, 0x275F9B0, 0x274FBDE,
    0x273FEBA, 0x2730242, 0x2720677, 0x2710B57, 0x27010E1, 0x26F1715, 0x26E1DF1, 0x26D2575, 0x26C2DA0, 0x26B3670, 0x26A3FE6,
    0x26949FF, 0x26854BC, 0x267601C, 0x2666C1D, 0x26578BE, 0x2648600, 0x26393E1, 0x262A260, 0x261B17D, 0x260C136, 0x25FD18C,
    0x25EE27C, 0x25DF407, 0x25D062B, 0x25C18E8, 0x25B2C3E, 0x25A402A, 0x25954AD, 0x25869C6, 0x2577F74, 0x25695B6, 0x255AC8C,
    0x254C3F4, 0x253DBEF, 0x252F47B, 0x2520D98, 0x2512744, 0x2504180, 0x24F5C4B, 0x24E77A4, 0x24D9389, 0x24CAFFC, 0x24BCCFA,
    0x24AEA83, 0x24A0897, 0x2492735, 0x248465C, 0x247660B, 0x2468643, 0x245A701, 0x244C847, 0x243EA12, 0x2430C63, 0x2422F38,
    0x2415292, 0x240766F, 0x23F9ACF, 0x23EBFB1, 0x23DE515, 0x23D0AFA, 0x23C3160, 0x23B5846, 0x23A7FAB, 0x239A78E, 0x238CFF0,
    0x237F8CF, 0x237222C, 0x2364C05, 0x2357659, 0x234A129, 0x233CC74, 0x232F839, 0x2322478, 0x2315130, 0x2307E61, 0x22FAC09,
    0x22EDA29, 0x22E08C0, 0x22D37CE, 0x22C6751, 0x22B974A, 0x22AC7B8, 0x229F89B, 0x22929F1, 0x2285BBA, 0x2278DF7, 0x226C0A6,
    0x225F3C6, 0x2252758, 0x2245B5C, 0x2238FCF, 0x222C4B2, 0x221FA05, 0x2212FC7, 0x22065F7, 0x21F9C96, 0x21ED3A2, 0x21E0B1B,
    0x21D4301, 0x21C7B53, 0x21BB410, 0x21AED39, 0x21A26CD, 0x21960CB, 0x2189B33, 0x217D605, 0x217113F, 0x2164CE2, 0x21588EE,
    0x214C561, 0x214023B, 0x2133F7D, 0x2127D25, 0x211BB33, 0x210F9A6, 0x210387F, 0x20F77BD, 0x20EB75F, 0x20DF765, 0x20D37CE,
    0x20C789B, 0x20BB9CA, 0x20AFB5C, 0x20A3D50, 0x2097FA6, 0x208C25C, 0x2080574, 0x20748EC, 0x2068CC4, 0x205D0FC, 0x2051593,
    0x2045A88, 0x2039FDD, 0x202E58F, 0x2022BA0, 0x201720E, 0x200B8D8, 0x2000000, 0x1FF4784, 0x1FE8F64, 0x1FDD79F, 0x1FD2036,
    0x1FC6928, 0x1FBB274, 0x1FAFC1A, 0x1FA461A, 0x1F99074, 0x1F8DB27, 0x1F82633, 0x1F77197, 0x1F6BD53, 0x1F60968, 0x1F555D3,
    0x1F4A296, 0x1F3EFB0, 0x1F33D20, 0x1F28AE6, 0x1F1D903, 0x1F12774, 0x1F0763B, 0x1EFC557, 0x1EF14C7, 0x1EE648C, 0x1EDB4A5,
    0x1ED0511, 0x1EC55D0, 0x1EBA6E3, 0x1EAF848, 0x1EA49FF, 0x1E99C09, 0x1E8EE64, 0x1E84111, 0x1E79410, 0x1E6E75F, 0x1E63AFE,
    0x1E58EEE, 0x1E4E32E, 0x1E437BE, 0x1E38C9D, 0x1E2E1CB, 0x1E23748, 0x1E18D14, 0x1E0E32E, 0x1E03996, 0x1DF904C, 0x1DEE74F,
    0x1DE3E9F, 0x1DD963D, 0x1DCEE27, 0x1DC465D, 0x1DB9EDF, 0x1DAF7AD, 0x1DA50C7, 0x1D9AA2C, 0x1D903DC, 0x1D85DD7, 0x1D7B81C,
    0x1D712AC, 0x1D66D86, 0x1D5C8A9, 0x1D52416, 0x1D47FCC, 0x1D3DBCA, 0x1D33812, 0x1D294A2, 0x1D1F17A, 0x1D14E9B, 0x1D0AC02,
    0x1D009B2, 0x1CF67A8, 0x1CEC5E6, 0x1CE246A, 0x1CD8335, 0x1CCE246, 0x1CC419D, 0x1CBA13A, 0x1CB011C, 0x1CA6144, 0x1C9C1B0,
    0x1C92262, 0x1C88358, 0x1C7E492, 0x1C74611, 0x1C6A7D4, 0x1C609DA, 0x1C56C23, 0x1C4CEB0, 0x1C43180, 0x1C39493, 0x1C2F7E8,
    0x1C25B80, 0x1C1BF5A, 0x1C12375, 0x1C087D3, 0x1BFEC71, 0x1BF5151, 0x1BEB673, 0x1BE1BD5, 0x1BD8177, 0x1BCE75A, 0x1BC4D7D,
    0x1BBB3E1, 0x1BB1A84, 0x1BA8166, 0x1B9E888, 0x1B94FE9, 0x1B8B789, 0x1B81F68, 0x1B78786, 0x1B6EFE1, 0x1B6587B, 0x1B5C153,
    0x1B52A69, 0x1B493BC, 0x1B3FD4D, 0x1B3671A, 0x1B2D125, 0x1B23B6D, 0x1B1A5F1, 0x1B110B1, 0x1B07BAE, 0x1AFE6E7, 0x1AF525C,
    0x1AEBE0C, 0x1AE29F8, 0x1AD961F, 0x1AD0281, 0x1AC6F1E, 0x1ABDBF6, 0x1AB4908, 0x1AAB655, 0x1AA23DC, 0x1A9919C, 0x1A8FF97,
    0x1A86DCB, 0x1A7DC39, 0x1A74AE0, 0x1A6B9C0, 0x1A628DA, 0x1A5982B, 0x1A507B6, 0x1A47779, 0x1A3E774, 0x1A357A7, 0x1A2C812,
    0x1A238B5, 0x1A1A98F, 0x1A11AA1, 0x1A08BEA, 0x19FFD6A, 0x19F6F21, 0x19EE10F, 0x19E5333, 0x19DC58E, 0x19D381F, 0x19CAAE6,
    0x19C1DE3, 0x19B9116, 0x19B047E, 0x19A781C, 0x199EBEF, 0x1995FF7, 0x198D434, 0x19848A6, 0x197BD4D, 0x1973228, 0x196A737,
    0x1961C7B, 0x19591F3, 0x195079E, 0x1947D7D, 0x193F390, 0x19369D7, 0x192E050, 0x19256FD, 0x191CDDD, 0x19144EF, 0x190BC34,
    0x19033AC, 0x18FAB56, 0x18F2333, 0x18E9B41, 0x18E1382, 0x18D8BF4, 0x18D0498, 0x18C7D6E, 0x18BF675, 0x18B6FAD, 0x18AE916,
    0x18A62B0, 0x189DC7C, 0x1895677, 0x188D0A4, 0x1884B01, 0x187C58E, 0x187404B, 0x186BB38, 0x1863655, 0x185B1A2, 0x1852D1F,
    0x184A8CB, 0x18424A6, 0x183A0B1, 0x1831CEA, 0x1829953, 0x18215EA, 0x18192B0, 0x1810FA5, 0x1808CC8, 0x1800A1A, 0x17F8799,
    0x17F0547, 0x17E8322, 0x17E012C, 0x17D7F63, 0x17CFDC7, 0x17C7C59, 0x17BFB18, 0x17B7A05, 0x17AF91E, 0x17A7865, 0x179F7D8,
    0x1797778, 0x178F744, 0x178773D, 0x177F762, 0x17777B4, 0x176F831, 0x17678DB, 0x175F9B0, 0x1757AB1, 0x174FBDE, 0x1747D36,
    0x173FEBA, 0x1738068, 0x1730242, 0x1728447, 0x1720677, 0x17188D2, 0x1710B57, 0x1708E07, 0x17010E1, 0x16F93E6, 0x16F1715,
    0x16E9A6E, 0x16E1DF1, 0x16DA19E, 0x16D2575, 0x16CA976, 0x16C2DA0, 0x16BB1F3, 0x16B3670, 0x16ABB16, 0x16A3FE6, 0x169C4DE,
    0x16949FF, 0x168CF49, 0x16854BC, 0x167DA58, 0x167601C, 0x166E608, 0x1666C1D, 0x165F25A, 0x16578BE, 0x164FF4B, 0x1648600,
    0x1640CDD, 0x16393E1, 0x1631B0D, 0x162A260, 0x16229DB, 0x161B17D, 0x1613946, 0x160C136, 0x160494D, 0x15FD18C, 0x15F59F0,
    0x15EE27C, 0x15E6B2E, 0x15DF407, 0x15D7D06, 0x15D062B, 0x15C8F77, 0x15C18E8, 0x15BA280, 0x15B2C3E, 0x15AB621, 0x15A402A,
    0x159CA59, 0x15954AD, 0x158DF27, 0x15869C6, 0x157F48A, 0x1577F74, 0x1570A82, 0x15695B6, 0x156210E, 0x155AC8C, 0x155382E,
    0x154C3F4, 0x1544FDF, 0x153DBEF, 0x1536823, 0x152F47B, 0x15280F7, 0x1520D98, 0x1519A5C, 0x1512744, 0x150B450, 0x1504180,
    0x14FCED4, 0x14F5C4B, 0x14EE9E6, 0x14E77A4, 0x14E0585, 0x14D9389, 0x14D21B1, 0x14CAFFC, 0x14C3E69, 0x14BCCFA, 0x14B5BAD,
    0x14AEA83, 0x14A797C, 0x14A0897, 0x14997D5, 0x1492735, 0x148B6B7, 0x148465C, 0x147D622, 0x147660B, 0x146F616, 0x1468643,
    0x1461691, 0x145A701, 0x1453793, 0x144C847, 0x144591C, 0x143EA12, 0x1437B2A, 0x1430C63, 0x1429DBD, 0x1422F38, 0x141C0D5,
    0x1415292, 0x140E470, 0x140766F, 0x140088F, 0x13F9ACF, 0x13F2D30, 0x13EBFB1, 0x13E5253, 0x13DE515, 0x13D77F8, 0x13D0AFA,
    0x13C9E1D, 0x13C3160, 0x13BC4C3, 0x13B5846, 0x13AEBE8, 0x13A7FAB, 0x13A138D, 0x139A78E, 0x1393BAF, 0x138CFF0, 0x1386450,
    0x137F8CF, 0x1378D6E, 0x137222C, 0x136B709, 0x1364C05, 0x135E11F, 0x1357659, 0x1350BB2, 0x134A129, 0x13436C0, 0x133CC74,
    0x1336248, 0x132F839, 0x1328E4A, 0x1322478, 0x131BAC5, 0x1315130, 0x130E7B9, 0x1307E61, 0x1301526, 0x12FAC09, 0x12F430A,
    0x12EDA29, 0x12E7166, 0x12E08C0, 0x12DA038, 0x12D37CE, 0x12CCF81, 0x12C6751, 0x12BFF3F, 0x12B974A, 0x12B2F73, 0x12AC7B8,
    0x12A601B, 0x129F89B, 0x1299137, 0x12929F1, 0x128C2C7, 0x1285BBA, 0x127F4CA, 0x1278DF7, 0x1272740, 0x126C0A6, 0x1265A28,
    0x125F3C6, 0x1258D81, 0x1252758, 0x124C14C, 0x1245B5C, 0x123F587, 0x1238FCF, 0x1232A33, 0x122C4B2, 0x1225F4E, 0x121FA05,
    0x12194D8, 0x1212FC7, 0x120CAD1, 0x12065F7, 0x1200139, 0x11F9C96, 0x11F380E, 0x11ED3A2, 0x11E6F51, 0x11E0B1B, 0x11DA700,
    0x11D4301, 0x11CDF1C, 0x11C7B53, 0x11C17A4, 0x11BB410, 0x11B5097, 0x11AED39, 0x11A89F6, 0x11A26CD, 0x119C3BF, 0x11960CB,
    0x118FDF2, 0x1189B33, 0x118388F, 0x117D605, 0x1177395, 0x117113F, 0x116AF04, 0x1164CE2, 0x115EADB, 0x11588EE, 0x115271A,
    0x114C561, 0x11463C1, 0x114023B, 0x113A0CF, 0x1133F7D, 0x112DE44, 0x1127D25, 0x1121C1F, 0x111BB33, 0x1115A60, 0x110F9A6,
    0x1109906, 0x110387F, 0x10FD811, 0x10F77BD, 0x10F1781, 0x10EB75F, 0x10E5755, 0x10DF765, 0x10D978D, 0x10D37CE, 0x10CD828,
    0x10C789B, 0x10C1926, 0x10BB9CA, 0x10B5A87, 0x10AFB5C, 0x10A9C4A, 0x10A3D50, 0x109DE6F, 0x1097FA6, 0x10920F5, 0x108C25C,
    0x10863DC, 0x1080574, 0x107A724, 0x10748EC, 0x106EACC, 0x1068CC4, 0x1062ED4, 0x105D0FC, 0x105733B, 0x1051593, 0x104B802,
    0x1045A88, 0x103FD27, 0x1039FDD, 0x10342AA, 0x102E58F, 0x102888C, 0x1022BA0, 0x101CECB, 0x101720E, 0x1011567, 0x100B8D8,
    0x1005C61, 0x1000000, 0xFFA3B6, 0xFF4784, 0xFEEB68, 0xFE8F64, 0xFE3376, 0xFDD79F, 0xFD7BDF, 0xFD2036, 0xFCC4A3,
    0xFC6928, 0xFC0DC2, 0xFBB274, 0xFB573C, 0xFAFC1A, 0xFAA10F, 0xFA461A, 0xF9EB3C, 0xF99074, 0xF935C2, 0xF8DB27,
    0xF880A2, 0xF82633, 0xF7CBDA, 0xF77197, 0xF7176A, 0xF6BD53, 0xF66353, 0xF60968, 0xF5AF93, 0xF555D3, 0xF4FC2A,
    0xF4A296, 0xF44918, 0xF3EFB0, 0xF3965D, 0xF33D20, 0xF2E3F9, 0xF28AE6, 0xF231EA, 0xF1D903, 0xF18031, 0xF12774,
    0xF0CECD, 0xF0763B, 0xF01DBF, 0xEFC557, 0xEF6D05, 0xEF14C7, 0xEEBC9F, 0xEE648C, 0xEE0C8E, 0xEDB4A5, 0xED5CD0,
    0xED0511, 0xECAD66, 0xEC55D0, 0xEBFE4F, 0xEBA6E3, 0xEB4F8B, 0xEAF848, 0xEAA119, 0xEA49FF, 0xE9F2FA, 0xE99C09,
    0xE9452D, 0xE8EE64, 0xE897B1, 0xE84111, 0xE7EA86, 0xE79410, 0xE73DAD, 0xE6E75F, 0xE69124, 0xE63AFE, 0xE5E4EC,
    0xE58EEE, 0xE53904, 0xE4E32E, 0xE48D6C, 0xE437BE, 0xE3E223, 0xE38C9D, 0xE3372A, 0xE2E1CB, 0xE28C80, 0xE23748,
    0xE1E224, 0xE18D14, 0xE13817, 0xE0E32E, 0xE08E58, 0xE03996, 0xDFE4E7, 0xDF904C, 0xDF3BC4, 0xDEE74F, 0xDE92ED,
    0xDE3E9F, 0xDDEA64, 0xDD963D, 0xDD4228, 0xDCEE27, 0xDC9A38, 0xDC465D, 0xDBF295, 0xDB9EDF, 0xDB4B3D, 0xDAF7AD,
    0xDAA431, 0xDA50C7, 0xD9FD70, 0xD9AA2C, 0xD956FB, 0xD903DC, 0xD8B0D0, 0xD85DD7, 0xD80AF1, 0xD7B81C, 0xD7655B,
    0xD712AC, 0xD6C010, 0xD66D86, 0xD61B0E, 0xD5C8A9, 0xD57656, 0xD52416, 0xD4D1E7, 0xD47FCC, 0xD42DC2, 0xD3DBCA,
    0xD389E5, 0xD33812, 0xD2E651, 0xD294A2, 0xD24305, 0xD1F17A, 0xD1A001, 0xD14E9B, 0xD0FD46, 0xD0AC02, 0xD05AD1,
    0xD009B2, 0xCFB8A4, 0xCF67A8, 0xCF16BE, 0xCEC5E6, 0xCE751F, 0xCE246A, 0xCDD3C7, 0xCD8335, 0xCD32B5, 0xCCE246,
    0xCC91E9, 0xCC419D, 0xCBF163, 0xCBA13A, 0xCB5122, 0xCB011C, 0xCAB127, 0xCA6144, 0xCA1171, 0xC9C1B0, 0xC97200,
    0xC92262, 0xC8D2D4, 0xC88358, 0xC833ED, 0xC7E492, 0xC79549, 0xC74611, 0xC6F6EA, 0xC6A7D4, 0xC658CE, 0xC609DA,
    0xC5BAF6, 0xC56C23, 0xC51D61, 0xC4CEB0, 0xC48010, 0xC43180, 0xC3E301, 0xC39493, 0xC34635, 0xC2F7E8, 0xC2A9AC,
    0xC25B80, 0xC20D64, 0xC1BF5A, 0xC1715F, 0xC12375, 0xC0D59C, 0xC087D3, 0xC03A1A, 0xBFEC71, 0xBF9ED9, 0xBF5151,
    0xBF03DA, 0xBEB673, 0xBE691B, 0xBE1BD5, 0xBDCE9E, 0xBD8177, 0xBD3461, 0xBCE75A, 0xBC9A64, 0xBC4D7D, 0xBC00A7,
    0xBBB3E1, 0xBB672A, 0xBB1A84, 0xBACDED, 0xBA8166, 0xBA34EF, 0xB9E888, 0xB99C31, 0xB94FE9, 0xB903B1, 0xB8B789,
    0xB86B71, 0xB81F68, 0xB7D36F, 0xB78786, 0xB73BAC, 0xB6EFE1, 0xB6A427, 0xB6587B, 0xB60CDF, 0xB5C153, 0xB575D6,
    0xB52A69, 0xB4DF0B, 0xB493BC, 0xB4487D, 0xB3FD4D, 0xB3B22C, 0xB3671A, 0xB31C18, 0xB2D125, 0xB28641, 0xB23B6D,
    0xB1F0A7, 0xB1A5F1, 0xB15B4A, 0xB110B1, 0xB0C628, 0xB07BAE, 0xB03143, 0xAFE6E7, 0xAF9C9A, 0xAF525C, 0xAF082C,
    0xAEBE0C, 0xAE73FA, 0xAE29F8, 0xADE004, 0xAD961F, 0xAD4C49, 0xAD0281, 0xACB8C8, 0xAC6F1E, 0xAC2583, 0xABDBF6,
    0xAB9278, 0xAB4908, 0xAAFFA7, 0xAAB655, 0xAA6D11, 0xAA23DC, 0xA9DAB5, 0xA9919C, 0xA94893, 0xA8FF97, 0xA8B6AA,
    0xA86DCB, 0xA824FB, 0xA7DC39, 0xA79386, 0xA74AE0, 0xA70249, 0xA6B9C0, 0xA67146, 0xA628DA, 0xA5E07B, 0xA5982B,
    0xA54FEA, 0xA507B6, 0xA4BF90, 0xA47779, 0xA42F6F, 0xA3E774, 0xA39F87, 0xA357A7, 0xA30FD6, 0xA2C812, 0xA2805D,
    0xA238B5, 0xA1F11B, 0xA1A98F, 0xA16211, 0xA11AA1, 0xA0D33F, 0xA08BEA, 0xA044A4, 0x9FFD6A, 0x9FB63F, 0x9F6F21,
    0x9F2811, 0x9EE10F, 0x9E9A1B, 0x9E5333, 0x9E0C5A, 0x9DC58E, 0x9D7ED0, 0x9D381F, 0x9CF17C, 0x9CAAE6, 0x9C645E,
    0x9C1DE3, 0x9BD776, 0x9B9116, 0x9B4AC3, 0x9B047E, 0x9ABE46, 0x9A781C, 0x9A31FF, 0x99EBEF, 0x99A5EC, 0x995FF7,
    0x991A0F, 0x98D434, 0x988E67, 0x9848A6, 0x9802F3, 0x97BD4D, 0x9777B4, 0x973228, 0x96ECA9, 0x96A737, 0x9661D3,
    0x961C7B, 0x95D730, 0x9591F3, 0x954CC2, 0x95079E, 0x94C287, 0x947D7D, 0x943880, 0x93F390, 0x93AEAD, 0x9369D7,
    0x93250D, 0x92E050, 0x929BA0, 0x9256FD, 0x921266, 0x91CDDD, 0x91895F, 0x9144EF, 0x91008B, 0x90BC34, 0x9077EA,
    0x9033AC, 0x8FEF7B, 0x8FAB56, 0x8F673E, 0x8F2333, 0x8EDF34, 0x8E9B41, 0x8E575B, 0x8E1382, 0x8DCFB5, 0x8D8BF4,
    0x8D4840, 0x8D0498, 0x8CC0FD, 0x8C7D6E, 0x8C39EB, 0x8BF675, 0x8BB30B, 0x8B6FAD, 0x8B2C5B, 0x8AE916, 0x8AA5DD,
    0x8A62B0, 0x8A1F90, 0x89DC7C, 0x899973, 0x895677, 0x891388, 0x88D0A4, 0x888DCC, 0x884B01, 0x880841, 0x87C58E,
    0x8782E6, 0x87404B, 0x86FDBC, 0x86BB38, 0x8678C1, 0x863655, 0x85F3F6, 0x85B1A2, 0x856F5B, 0x852D1F, 0x84EAEF,
    0x84A8CB, 0x8466B3, 0x8424A6, 0x83E2A6, 0x83A0B1, 0x835EC8, 0x831CEA, 0x82DB19, 0x829953, 0x825799, 0x8215EA,
    0x81D448, 0x8192B0, 0x815125, 0x810FA5, 0x80CE31, 0x808CC8, 0x804B6B, 0x800A1A, 0x7FC8D4, 0x7F8799, 0x7F466A,
    0x7F0547, 0x7EC42F, 0x7E8322, 0x7E4221, 0x7E012C, 0x7DC041, 0x7D7F63, 0x7D3E8F, 0x7CFDC7, 0x7CBD0B, 0x7C7C59,
    0x7C3BB3, 0x7BFB18, 0x7BBA89, 0x7B7A05, 0x7B398C, 0x7AF91E, 0x7AB8BC, 0x7A7865, 0x7A3819, 0x79F7D8, 0x79B7A2,
    0x797778, 0x793759, 0x78F744, 0x78B73B, 0x78773D, 0x78374A, 0x77F762, 0x77B786, 0x7777B4, 0x7737ED, 0x76F831,
    0x76B881, 0x7678DB, 0x763940, 0x75F9B0, 0x75BA2B, 0x757AB1, 0x753B42, 0x74FBDE, 0x74BC84, 0x747D36, 0x743DF2,
    0x73FEBA, 0x73BF8C, 0x738068, 0x734150, 0x730242, 0x72C33F, 0x728447, 0x72455A, 0x720677, 0x71C79F, 0x7188D2,
    0x714A0F, 0x710B57, 0x70CCAA, 0x708E07, 0x704F6F, 0x7010E1, 0x6FD25E, 0x6F93E6, 0x6F5578, 0x6F1715, 0x6ED8BC,
    0x6E9A6E, 0x6E5C2B, 0x6E1DF1, 0x6DDFC3, 0x6DA19E, 0x6D6385, 0x6D2575, 0x6CE770, 0x6CA976, 0x6C6B86, 0x6C2DA0,
    0x6BEFC4, 0x6BB1F3, 0x6B742D, 0x6B3670, 0x6AF8BE, 0x6ABB16, 0x6A7D79, 0x6A3FE6, 0x6A025D, 0x69C4DE, 0x69876A,
    0x6949FF, 0x690C9F, 0x68CF49, 0x6891FE, 0x6854BC, 0x681785, 0x67DA58, 0x679D35, 0x67601C, 0x67230D, 0x66E608,
    0x66A90D, 0x666C1D, 0x662F36, 0x65F25A, 0x65B587, 0x6578BE, 0x653C00, 0x64FF4B, 0x64C2A1, 0x648600, 0x644969,
    0x640CDD, 0x63D05A, 0x6393E1, 0x635772, 0x631B0D, 0x62DEB2, 0x62A260, 0x626619, 0x6229DB, 0x61EDA7, 0x61B17D,
    0x61755D, 0x613946, 0x60FD39, 0x60C136, 0x60853D, 0x60494D, 0x600D68, 0x5FD18C, 0x5F95B9, 0x5F59F0, 0x5F1E31,
    0x5EE27C, 0x5EA6D0, 0x5E6B2E, 0x5E2F96, 0x5DF407, 0x5DB882, 0x5D7D06, 0x5D4194, 0x5D062B, 0x5CCACC, 0x5C8F77,
    0x5C542B, 0x5C18E8, 0x5BDDAF, 0x5BA280, 0x5B675A, 0x5B2C3E, 0x5AF12B, 0x5AB621, 0x5A7B21, 0x5A402A, 0x5A053D,
    0x59CA59, 0x598F7E, 0x5954AD, 0x5919E5, 0x58DF27, 0x58A472, 0x5869C6, 0x582F23, 0x57F48A, 0x57B9FA, 0x577F74,
    0x5744F6, 0x570A82, 0x56D018, 0x5695B6, 0x565B5E, 0x56210E, 0x55E6C8, 0x55AC8C, 0x557258, 0x55382E, 0x54FE0C,
    0x54C3F4, 0x5489E5, 0x544FDF, 0x5415E2, 0x53DBEF, 0x53A204, 0x536823, 0x532E4A, 0x52F47B, 0x52BAB5, 0x5280F7,
    0x524743, 0x520D98, 0x51D3F5, 0x519A5C, 0x5160CC, 0x512744, 0x50EDC6, 0x50B450, 0x507AE4, 0x504180, 0x500826,
    0x4FCED4, 0x4F958B, 0x4F5C4B, 0x4F2314, 0x4EE9E6, 0x4EB0C0, 0x4E77A4, 0x4E3E90, 0x4E0585, 0x4DCC83, 0x4D9389,
    0x4D5A99, 0x4D21B1, 0x4CE8D2, 0x4CAFFC, 0x4C772E, 0x4C3E69, 0x4C05AD, 0x4BCCFA, 0x4B944F, 0x4B5BAD, 0x4B2314,
    0x4AEA83, 0x4AB1FB, 0x4A797C, 0x4A4105, 0x4A0897, 0x49D031, 0x4997D5, 0x495F80, 0x492735, 0x48EEF2, 0x48B6B7,
    0x487E85, 0x48465C, 0x480E3B, 0x47D622, 0x479E13, 0x47660B, 0x472E0C, 0x46F616, 0x46BE28, 0x468643, 0x464E66,
    0x461691, 0x45DEC5, 0x45A701, 0x456F46, 0x453793, 0x44FFE9, 0x44C847, 0x4490AD, 0x44591C, 0x442193, 0x43EA12,
    0x43B29A, 0x437B2A, 0x4343C2, 0x430C63, 0x42D50C, 0x429DBD, 0x426676, 0x422F38, 0x41F802, 0x41C0D5, 0x4189AF,
    0x415292, 0x411B7D, 0x40E470, 0x40AD6B, 0x40766F, 0x403F7B, 0x40088F, 0x3FD1AB, 0x3F9ACF, 0x3F63FB, 0x3F2D30,
    0x3EF66D, 0x3EBFB1, 0x3E88FE, 0x3E5253, 0x3E1BB0, 0x3DE515, 0x3DAE83, 0x3D77F8, 0x3D4175, 0x3D0AFA, 0x3CD488,
    0x3C9E1D, 0x3C67BB, 0x3C3160, 0x3BFB0E, 0x3BC4C3, 0x3B8E80, 0x3B5846, 0x3B2213, 0x3AEBE8, 0x3AB5C5, 0x3A7FAB,
    0x3A4998, 0x3A138D, 0x39DD89, 0x39A78E, 0x39719B, 0x393BAF, 0x3905CC, 0x38CFF0, 0x389A1C, 0x386450, 0x382E8C,
    0x37F8CF, 0x37C31B, 0x378D6E, 0x3757C9, 0x37222C, 0x36EC96, 0x36B709, 0x368183, 0x364C05, 0x36168E, 0x35E11F,
    0x35ABB9, 0x357659, 0x354102, 0x350BB2, 0x34D66A, 0x34A129, 0x346BF1, 0x3436C0, 0x340196, 0x33CC74, 0x33975A,
    0x336248, 0x332D3D, 0x32F839, 0x32C33E, 0x328E4A, 0x32595D, 0x322478, 0x31EF9B, 0x31BAC5, 0x3185F7, 0x315130,
    0x311C71, 0x30E7B9, 0x30B309, 0x307E61, 0x3049C0, 0x301526, 0x2FE094, 0x2FAC09, 0x2F7786, 0x2F430A, 0x2F0E96,
    0x2EDA29, 0x2EA5C4, 0x2E7166, 0x2E3D0F, 0x2E08C0, 0x2DD479, 0x2DA038, 0x2D6BFF, 0x2D37CE, 0x2D03A4, 0x2CCF81,
    0x2C9B66, 0x2C6751, 0x2C3345, 0x2BFF3F, 0x2BCB41, 0x2B974A, 0x2B635B, 0x2B2F73, 0x2AFB92, 0x2AC7B8, 0x2A93E6,
    0x2A601B, 0x2A2C57, 0x29F89B, 0x29C4E5, 0x299137, 0x295D90, 0x2929F1, 0x28F658, 0x28C2C7, 0x288F3D, 0x285BBA,
    0x28283F, 0x27F4CA, 0x27C15D, 0x278DF7, 0x275A98, 0x272740, 0x26F3EF, 0x26C0A6, 0x268D63, 0x265A28, 0x2626F4,
    0x25F3C6, 0x25C0A0, 0x258D81, 0x255A69, 0x252758, 0x24F44F, 0x24C14C, 0x248E50, 0x245B5C, 0x24286E, 0x23F587,
    0x23C2A8, 0x238FCF, 0x235CFD, 0x232A33, 0x22F76F, 0x22C4B2, 0x2291FD, 0x225F4E, 0x222CA6, 0x21FA05, 0x21C76B,
    0x2194D8, 0x21624C, 0x212FC7, 0x20FD49, 0x20CAD1, 0x209861, 0x2065F7, 0x203395, 0x200139, 0x1FCEE4, 0x1F9C96,
    0x1F6A4F, 0x1F380E, 0x1F05D5, 0x1ED3A2, 0x1EA176, 0x1E6F51, 0x1E3D32, 0x1E0B1B, 0x1DD90A, 0x1DA700, 0x1D74FD,
    0x1D4301, 0x1D110B, 0x1CDF1C, 0x1CAD34, 0x1C7B53, 0x1C4978, 0x1C17A4, 0x1BE5D7, 0x1BB410, 0x1B8250, 0x1B5097,
    0x1B1EE5, 0x1AED39, 0x1ABB94, 0x1A89F6, 0x1A585E, 0x1A26CD, 0x19F542, 0x19C3BF, 0x199241, 0x1960CB, 0x192F5B,
    0x18FDF2, 0x18CC8F, 0x189B33, 0x1869DE, 0x18388F, 0x180746, 0x17D605, 0x17A4C9, 0x177395, 0x174267, 0x17113F,
    0x16E01E, 0x16AF04, 0x167DF0, 0x164CE2, 0x161BDC, 0x15EADB, 0x15B9E1, 0x1588EE, 0x155801, 0x15271A, 0x14F63A,
    0x14C561, 0x14948E, 0x1463C1, 0x1432FB, 0x14023B, 0x13D182, 0x13A0CF, 0x137023, 0x133F7D, 0x130EDD, 0x12DE44,
    0x12ADB1, 0x127D25, 0x124C9F, 0x121C1F, 0x11EBA6, 0x11BB33, 0x118AC6, 0x115A60, 0x112A00, 0x10F9A6, 0x10C953,
    0x109906, 0x1068BF, 0x10387F, 0x100845, 0xFD811, 0xFA7E4, 0xF77BD, 0xF479C, 0xF1781, 0xEE76D, 0xEB75F,
    0xE8757, 0xE5755, 0xE275A, 0xDF765, 0xDC776, 0xD978D, 0xD67AA, 0xD37CE, 0xD07F8, 0xCD828, 0xCA85E,
    0xC789B, 0xC48DD, 0xC1926, 0xBE975, 0xBB9CA, 0xB8A26, 0xB5A87, 0xB2AEF, 0xAFB5C, 0xACBD0, 0xA9C4A,
    0xA6CCA, 0xA3D50, 0xA0DDC, 0x9DE6F, 0x9AF07, 0x97FA6, 0x9504A, 0x920F5, 0x8F1A6, 0x8C25C, 0x89319,
    0x863DC, 0x834A5, 0x80574, 0x7D649, 0x7A724, 0x77805, 0x748EC, 0x719D9, 0x6EACC, 0x6BBC5, 0x68CC4,
    0x65DC9, 0x62ED4, 0x5FFE5, 0x5D0FC, 0x5A218, 0x5733B, 0x54464, 0x51593, 0x4E6C7, 0x4B802, 0x48942,
    0x45A88, 0x42BD5, 0x3FD27, 0x3CE7F, 0x39FDD, 0x37141, 0x342AA, 0x3141A, 0x2E58F, 0x2B70B, 0x2888C,
    0x25A13, 0x22BA0, 0x1FD33, 0x1CECB, 0x1A069, 0x1720E, 0x143B8, 0x11567, 0xE71D, 0xB8D8, 0x8A9A,
    0x5C61, 0x2E2D,
  ];

  private static ulong BitCost(ushort[] models, int index, int bit) => ProbCost[bit != 0 ? 2048 - models[index] : models[index]];

  private static ulong SymbolCost(LzhamHuffmanModel model, int symbol) => (ulong)((uint)model.CodeSizes[symbol] << BitCostScaleShift);

  private static ulong ScaledBits(int bits) => (ulong)((uint)bits << BitCostScaleShift);

  /// <summary>
  /// <c>get_huge_match_code_len</c>. LZHAM adds this bit count to the scaled cost unscaled; the
  /// parsers never price lengths above 257, so it never matters, but it is kept as it is.
  /// </summary>
  private static ulong HugeMatchCodeLen(int length) {
    var len = length - (LzhamTables.MaxMatchLen + 1);
    return len < 256 ? 1 + 8u : len < 256 + 1024 ? 2 + 10u : len < 256 + 1024 + 4096 ? 3 + 12u : 3 + 16u;
  }

  /// <summary><c>get_lit_cost</c>.</summary>
  public ulong GetLitCost(in LzhamPartialState s, byte[] data, int position, int isMatchModelIndex) {
    var cost = BitCost(_isMatch, isMatchModelIndex, 0);
    var lit = data[position];
    if (s.CurState < LzhamTables.NumLitStates)
      cost += SymbolCost(_litTable, lit);
    else
      cost += SymbolCost(_deltaLitTable, lit ^ data[position - (int)s.Hist0]);
    return cost;
  }

  /// <summary><c>get_rep_match_costs</c>: the cost of every length in [<paramref name="minLen"/>, <paramref name="maxLen"/>].</summary>
  public void GetRepMatchCosts(in LzhamPartialState s, ulong[] costs, int repIndex, int minLen, int maxLen, int isMatchModelIndex) {
    var cs = s.CurState;
    var table = _repLenTable[cs >= LzhamTables.NumLitStates ? 1 : 0];
    var baseCost = BitCost(_isMatch, isMatchModelIndex, 1) + BitCost(_isRep, cs, 1);
    if (repIndex == 0)
      baseCost += BitCost(_isRep0, cs, 1);
    else {
      baseCost += BitCost(_isRep0, cs, 0);
      if (repIndex == 1)
        baseCost += BitCost(_isRep1, cs, 1);
      else
        baseCost += BitCost(_isRep1, cs, 0) + BitCost(_isRep2, cs, repIndex == 2 ? 1 : 0);
    }

    if (repIndex == 0) {
      if (minLen == 1) {
        costs[1] = baseCost + BitCost(_isRep0SingleByte, cs, 1);
        ++minLen;
      }

      baseCost += BitCost(_isRep0SingleByte, cs, 0);
    }

    for (var len = minLen; len <= maxLen; ++len)
      costs[len] = len > LzhamTables.MaxMatchLen
        ? HugeMatchCodeLen(len) + baseCost + SymbolCost(table, LzhamTables.MaxMatchLen + 1 - LzhamTables.MinMatchLen)
        : baseCost + SymbolCost(table, len - LzhamTables.MinMatchLen);
  }

  private ulong DistanceCost(uint distance, out int highSymbol) {
    var slot = PositionSlot(distance);
    var extra = distance - LzhamTables.PositionBase[slot];
    highSymbol = slot - LzhamTables.LowestUsableMatchSlot;
    int numExtraBits = LzhamTables.PositionExtraBits[slot];
    if (numExtraBits < 3)
      return ScaledBits(numExtraBits);
    var cost = SymbolCost(_distLsbTable, (int)(extra & 15));
    if (numExtraBits > 4)
      cost += ScaledBits(numExtraBits - 4);
    return cost;
  }

  /// <summary><c>get_full_match_costs</c>: the cost of every length in [<paramref name="minLen"/>, <paramref name="maxLen"/>] at one distance.</summary>
  public void GetFullMatchCosts(in LzhamPartialState s, ulong[] costs, uint distance, int minLen, int maxLen, int isMatchModelIndex) {
    var cs = s.CurState;
    var cost = BitCost(_isMatch, isMatchModelIndex, 1) + BitCost(_isRep, cs, 0) + DistanceCost(distance, out var highSymbol);
    var largeLenTable = _largeLenTable[cs >= LzhamTables.NumLitStates ? 1 : 0];
    for (var len = minLen; len <= maxLen; ++len) {
      var lenCost = cost;
      int lowSymbol;
      if (len >= 9) {
        lowSymbol = 7;
        lenCost += len > LzhamTables.MaxMatchLen
          ? HugeMatchCodeLen(len) + SymbolCost(largeLenTable, LzhamTables.MaxMatchLen + 1 - 9)
          : SymbolCost(largeLenTable, len - 9);
      } else
        lowSymbol = len - 2;

      costs[len] = lenCost + SymbolCost(_mainTable, LzhamTables.NumSpecialLengths + (lowSymbol | (highSymbol << 3)));
    }
  }

  /// <summary><c>get_len2_match_cost</c>.</summary>
  public ulong GetLen2MatchCost(in LzhamPartialState s, uint distance, int isMatchModelIndex) {
    var cost = BitCost(_isMatch, isMatchModelIndex, 1) + BitCost(_isRep, s.CurState, 0) + DistanceCost(distance, out var highSymbol);
    return cost + SymbolCost(_mainTable, LzhamTables.NumSpecialLengths + (highSymbol << 3));
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

  internal static int PositionSlot(uint distance) {
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
