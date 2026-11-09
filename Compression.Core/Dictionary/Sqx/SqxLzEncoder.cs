namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// Encoder of SQX methods 1-4, converted from Squeez-c's <c>create.c</c>, <c>enc.c</c> and
/// <c>henc.c</c> (MIT; see <c>THIRD-PARTY-NOTICE.Squeez-c.txt</c>): a hash-chain parse into
/// literals, repeat-distance matches and length 2, 3 and longer matches, then blocks of up to
/// 16,383 symbols with one set of Huffman tables sent in the meta-coded form. The stream decodes
/// with <see cref="SqxLzDecoder"/> and needs no extended header.
/// </summary>
public static class SqxLzEncoder {
  private const int HashBits = 16;
  private const int BlockSymbols = 16383;
  private const int MinMatch = 3;

  private struct Op {
    public uint Code, A, B, C, Long14;
  }

  /// <summary>Encodes <paramref name="data"/> as one file's stream.</summary>
  /// <param name="data">The file.</param>
  /// <param name="format"><see cref="SqxTables.Format11"/> or <see cref="SqxTables.Format20"/>.</param>
  /// <param name="dictionarySize">Largest match distance allowed.</param>
  /// <param name="effort">1-4, the archive method: longer hash chains, and lazy matching from 2.</param>
  public static byte[] Encode(ReadOnlySpan<byte> data, byte format, int dictionarySize, int effort) {
    if (data.IsEmpty)
      return [];
    var format20 = format >= SqxTables.Format20;
    var ops = Parse(data, dictionarySize, Math.Clamp(effort, 1, 4), format20, out var litFreq, out var repFreq, out var distFreq);
    var litCodes = format20 ? SqxTables.LitLenCodes20 : SqxTables.LitLenCodes11;
    var distCodes = format20 ? SqxTables.DistCodes20 : SqxTables.DistCodes11;

    var litLens = LengthsFromFrequencies(litFreq.AsSpan(0, litCodes), SqxTables.CodeBits)
      ?? throw new InvalidOperationException("SQX literal table cannot be built.");
    var repLens = LengthsFromFrequencies(repFreq, SqxTables.CodeBits) ?? TwoSymbols(SqxTables.LenCodes);
    var distLens = LengthsFromFrequencies(distFreq.AsSpan(0, distCodes), SqxTables.CodeBits) ?? TwoSymbols(distCodes);
    var metaFreq = new uint[SqxTables.MetaCodes];
    MetaFrequencies(litLens, metaFreq);
    MetaFrequencies(repLens, metaFreq);
    MetaFrequencies(distLens, metaFreq);
    var metaLens = LengthsFromFrequencies(metaFreq, SqxTables.MetaCodeBits)
      ?? throw new InvalidOperationException("SQX meta table cannot be built.");

    var metaCodes = SqxHuffman.Codes(metaLens, SqxTables.MetaCodeBits)!;
    var litCodeWords = SqxHuffman.Codes(litLens, SqxTables.CodeBits)!;
    var repCodeWords = SqxHuffman.Codes(repLens, SqxTables.CodeBits)!;
    var distCodeWords = SqxHuffman.Codes(distLens, SqxTables.CodeBits)!;

    var writer = new SqxBitWriter(data.Length / 2 + 1024);
    for (var first = 0; first < ops.Count; first += BlockSymbols) {
      var count = Math.Min(BlockSymbols, ops.Count - first);
      writer.Write(0, 1);                 // LZ block
      writer.Write((uint)count, 15);
      if (format20)
        writer.Write(0, 1);               // no low16 table
      writer.Write(0, 1);                 // three trees
      writer.Write(0, 1);                 // meta-coded tables
      foreach (var length in metaLens)
        writer.Write(length, 4);
      WriteTree(writer, litLens, metaCodes, metaLens);
      WriteTree(writer, repLens, metaCodes, metaLens);
      WriteTree(writer, distLens, metaCodes, metaLens);
      for (var i = first; i < first + count; ++i)
        WriteOp(writer, ops[i], format20, litCodeWords, litLens, repCodeWords, repLens, distCodeWords, distLens);
    }

    return writer.ToArray();
  }

  private static byte[] TwoSymbols(int n) {
    var lens = new byte[n];
    lens[0] = lens[1] = 1;
    return lens;
  }

  private static void WriteOp(SqxBitWriter w, Op op, bool format20, ushort[] litCodes, byte[] litLens,
      ushort[] repCodes, byte[] repLens, ushort[] distCodes, byte[] distLens) {
    w.Write(litCodes[op.Code], litLens[op.Code]);
    var symbol = op.Code;
    if (!format20)
      symbol = op.Code + (op.Code > 256 ? 1u : 0u) + (op.Code > 283 ? 1u : 0u);
    if (symbol < SqxTables.Len2Start20) {
      if (symbol > 257) {
        w.Write(repCodes[op.B], repLens[op.B]);
        if (op.B == SqxTables.LenCodes - 1)
          w.Write(op.Long14, 14);
        else if (SqxTables.LenExtraBits[(int)op.B] > 0)
          w.Write(op.C, SqxTables.LenExtraBits[(int)op.B]);
      }
    } else if (symbol < SqxTables.Len3Start20) {
      var k = (int)(symbol - SqxTables.Len2Start20);
      if (SqxTables.Len2ExtraBits[k] > 0)
        w.Write(op.A, SqxTables.Len2ExtraBits[k]);
    } else if (symbol < SqxTables.LenStart20) {
      var k = (int)(symbol - SqxTables.Len3Start20);
      if (SqxTables.Len3ExtraBits[k] > 0)
        w.Write(op.A, SqxTables.Len3ExtraBits[k]);
    } else {
      if (symbol == SqxTables.LitLenCodes20 - 1)
        w.Write(op.Long14, 14);
      else {
        var k = (int)(symbol - SqxTables.LenStart20);
        if (SqxTables.LenExtraBits[k] > 0)
          w.Write(op.A, SqxTables.LenExtraBits[k]);
      }

      w.Write(distCodes[op.B], distLens[op.B]);
      var extra = SqxTables.DistExtraBits[(int)op.B];
      if (extra > 0)
        w.Write(op.C, extra);
    }
  }

  // ---------------------------------------------------------------- parse

  private static uint ToRaw(uint s, bool format20) => !format20 || s <= 256 ? s : s >= 284 ? s + 2 : s + 1;

  private static List<Op> Parse(ReadOnlySpan<byte> buf, int dictionarySize, int effort, bool format20,
      out uint[] litFreq, out uint[] repFreq, out uint[] distFreq) {
    var n = buf.Length;
    var ops = new List<Op>(n / 2 + 16);
    litFreq = new uint[SqxTables.LitLenCodes20];
    repFreq = new uint[SqxTables.LenCodes];
    distFreq = new uint[SqxTables.DistCodes11];
    var prev = new int[n + 1];
    var head = new int[1 << HashBits];
    var last2 = new int[65536];
    Array.Fill(head, -1);
    Array.Fill(last2, -1);
    Array.Fill(prev, -1);
    var maxChain = effort * 32;
    var lazy = effort >= 2;
    var dic = (uint)dictionarySize;
    uint lastLen = 0, lastDist = 0, pi = 0;
    Span<uint> prevDists = stackalloc uint[4];
    var pos = 0;

    while (pos < n) {
      var op = default(Op);
      var maxLen = Math.Min(n - pos, SqxTables.MaxMatch);
      var useRep = false;
      uint repCode = 0, repDist = 0, repLen = 0;

      if (lastDist != 0 && lastDist <= pos && lastLen <= maxLen && MatchLength(buf, pos, lastDist, (int)lastLen) >= lastLen) {
        repCode = 256;
        repDist = lastDist;
        repLen = lastLen;
        useRep = true;
      }

      if (!useRep) {
        for (var k = 1u; k <= 4; ++k) {
          var d = prevDists[(int)((pi - k) & 3)];
          if (d == 0 || d > pos)
            continue;
          var l = MatchLength(buf, pos, d, maxLen);
          if (l >= 2 && (!useRep || l > repLen)) {
            useRep = true;
            repCode = 256 + k;
            repDist = d;
            repLen = l;
          }
        }
      }

      uint bestLen = 0, bestDist = 0, len2Dist = 0;
      var hasLen2 = false;
      if (!BestMatch(buf, pos, head, prev, maxChain, dic, out bestLen, out bestDist)) {
        bestLen = 0;
        hasLen2 = Match2(buf, pos, last2, out len2Dist);
      }

      if (useRep && (bestLen == 0 || repLen >= bestLen)) {
        var rs = RepLengthSymbol(repLen, repDist, format20, out var eb, out var l14);
        if (rs != uint.MaxValue) {
          op.Code = ToRaw(repCode, format20);
          op.B = rs;
          if (rs == SqxTables.LenCodes - 1)
            op.Long14 = l14;
          else
            op.C = eb;
          lastLen = repLen;
          lastDist = repDist;
          prevDists[(int)(pi++ & 3)] = repDist;
          Push(ops, op, format20, litFreq, repFreq, distFreq);
          for (var i = 1; i < repLen; ++i)
            Insert(buf, pos + i, head, prev, last2);
          pos += (int)repLen;
          continue;
        }
      }

      Insert(buf, pos, head, prev, last2);

      if (lazy && bestLen >= MinMatch && bestLen < SqxTables.MaxMatch && pos + 1 < n
          && BestMatch(buf, pos + 1, head, prev, maxChain, dic, out var nextLen, out _) && nextLen > bestLen && nextLen > 3)
        bestLen = 0;

      if (bestLen >= MinMatch) {
        if (bestLen == 3) {
          var k = Len3Symbol(bestDist, format20, out var eb);
          if (k != uint.MaxValue && k < SqxTables.Len3Codes && bestDist <= SqxTables.MinLen3) {
            op.Code = ToRaw(SqxTables.Len3Start + k, format20);
            op.A = eb;
          } else
            bestLen = 0;
        } else {
          // Lengths from 258 take the 14-bit long form, which the decoder never corrects for
          // distance; shorter ones take a length code.
          uint k = 0, eb = 0;
          if (bestLen < 258)
            k = LengthSymbol(bestLen, bestDist, format20, out eb);
          if (!format20 && bestLen == 4 && bestDist > SqxTables.MinLen4)
            bestLen = 0;
          else if (k == uint.MaxValue)
            bestLen = 0;
          else {
            var ds = DistanceSymbol(bestDist, out var de);
            if (ds == uint.MaxValue)
              bestLen = 0;
            else if (bestLen >= 258) {
              op.Code = ToRaw(SqxTables.LitLenCodes11 - 2, format20);
              op.Long14 = bestLen - 257;
              op.B = ds;
              op.C = de;
            } else {
              op.Code = ToRaw(SqxTables.LenStart + k, format20);
              op.A = eb;
              op.B = ds;
              op.C = de;
            }
          }
        }

        if (bestLen != 0) {
          prevDists[(int)(pi++ & 3)] = bestDist;
          Push(ops, op, format20, litFreq, repFreq, distFreq);
          for (var i = 1; i < bestLen; ++i)
            Insert(buf, pos + i, head, prev, last2);
          pos += (int)bestLen;
          continue;
        }
      }

      if (hasLen2) {
        var k = Len2Symbol(len2Dist, out var eb);
        if (k != uint.MaxValue) {
          op.Code = ToRaw(SqxTables.Len2Start + k, format20);
          op.A = eb;
          prevDists[(int)(pi++ & 3)] = len2Dist;
          Push(ops, op, format20, litFreq, repFreq, distFreq);
          Insert(buf, pos + 1, head, prev, last2);
          pos += 2;
          continue;
        }
      }

      op.Code = ToRaw(buf[pos], format20);
      Push(ops, op, format20, litFreq, repFreq, distFreq);
      ++pos;
    }

    return ops;
  }

  private static uint FromRaw(uint s, bool format20) => !format20 || s <= 256 ? s : s >= 286 ? s - 2 : s - 1;

  private static void Push(List<Op> ops, Op op, bool format20, uint[] litFreq, uint[] repFreq, uint[] distFreq) {
    var v = FromRaw(op.Code, format20);
    ++litFreq[op.Code];
    if (v is >= 257 and <= 260)
      ++repFreq[op.B];
    else if (v >= SqxTables.LenStart)
      ++distFreq[op.B];
    ops.Add(op);
  }

  private static int Hash3(ReadOnlySpan<byte> buf, int p) => (int)(((uint)(buf[p] << 16 | buf[p + 1] << 8 | buf[p + 2]) * 2654435761u) >> (32 - HashBits));

  private static void Insert(ReadOnlySpan<byte> buf, int pos, int[] head, int[] prev, int[] last2) {
    if (pos + 2 >= buf.Length)
      return;
    var h = Hash3(buf, pos);
    prev[pos] = head[h];
    head[h] = pos;
    last2[buf[pos] << 8 | buf[pos + 1]] = pos;
  }

  private static uint MatchLength(ReadOnlySpan<byte> buf, int pos, uint dist, int maxLen) {
    var l = 0;
    var from = pos - (int)dist;
    while (l < maxLen && buf[pos + l] == buf[from + l])
      ++l;
    return (uint)l;
  }

  private static bool BestMatch(ReadOnlySpan<byte> buf, int pos, int[] head, int[] prev, int maxChain, uint dic, out uint length, out uint distance) {
    length = distance = 0;
    var maxLen = Math.Min(buf.Length - pos, SqxTables.MaxMatch);
    if (maxLen < MinMatch || pos + 2 >= buf.Length)
      return false;
    uint best = 0, bestDist = 0;
    var chain = maxChain;
    var cand = head[Hash3(buf, pos)];
    while (cand >= 0 && chain-- > 0) {
      var dist = (uint)(pos - cand);
      if (dist == 0 || dist > dic)
        break;
      if (best < maxLen && buf[cand + (int)best] == buf[pos + (int)best]) {
        var l = MatchLength(buf, pos, dist, maxLen);
        if (l > best) {
          best = l;
          bestDist = dist;
          if (l >= maxLen)
            break;
        }
      }

      cand = prev[cand];
    }

    if (best < MinMatch)
      return false;
    length = best;
    distance = bestDist;
    return true;
  }

  private static bool Match2(ReadOnlySpan<byte> buf, int pos, int[] last2, out uint distance) {
    distance = 0;
    if (pos + 1 >= buf.Length)
      return false;
    var c = last2[buf[pos] << 8 | buf[pos + 1]];
    if (c < 0)
      return false;
    var dist = (uint)(pos - c);
    if (dist == 0 || dist > SqxTables.MinLen2)
      return false;
    distance = dist;
    return true;
  }

  private static uint Len2Symbol(uint dist, out uint extra) {
    var d = dist - 1;
    for (var k = 0; k < SqxTables.Len2Codes; ++k)
      if (d >= SqxTables.Len2Offsets[k] && d - SqxTables.Len2Offsets[k] < 1u << SqxTables.Len2ExtraBits[k]) {
        extra = d - SqxTables.Len2Offsets[k];
        return (uint)k;
      }

    extra = 0;
    return uint.MaxValue;
  }

  private static uint Len3Symbol(uint dist, bool format20, out uint extra) {
    var n = format20 ? SqxTables.Len3Codes + 1 : SqxTables.Len3Codes;
    var d = dist - 1;
    for (var k = 0; k < n; ++k)
      if (d >= SqxTables.Len3Offsets[k] && d - SqxTables.Len3Offsets[k] < 1u << SqxTables.Len3ExtraBits[k]) {
        extra = d - SqxTables.Len3Offsets[k];
        return (uint)k;
      }

    extra = 0;
    return uint.MaxValue;
  }

  private static uint LengthSymbol(uint len, uint dist, bool format20, out uint extra) {
    var l = len - 4;
    if (!format20 && dist > SqxTables.MinLen4 && len > 4)
      l = len - 5;
    extra = 0;
    if (l > 255)
      return uint.MaxValue;
    for (var k = 0; k < SqxTables.LenCodes - 1; ++k) {
      var room = k + 1 < SqxTables.LenCodes - 1 ? SqxTables.LenOffsets[k + 1] - SqxTables.LenOffsets[k] : 256 - SqxTables.LenOffsets[k];
      if (l >= SqxTables.LenOffsets[k] && l - SqxTables.LenOffsets[k] < room) {
        extra = l - SqxTables.LenOffsets[k];
        return (uint)k;
      }
    }

    return uint.MaxValue;
  }

  private static uint RepLengthSymbol(uint len, uint dist, bool format20, out uint extra, out uint long14) {
    extra = long14 = 0;
    var l = len - 2;
    if (format20) {
      if (dist > 0x7FFF)
        --l;
    } else {
      if (dist > SqxTables.MinLen3)
        --l;
      if (dist > SqxTables.MinLen4)
        --l;
    }

    if (l > 255) {
      if (len < 257 || len - 257 > 16383)
        return uint.MaxValue;
      long14 = len - 257;
      return SqxTables.LenCodes - 1;
    }

    for (var k = 0; k < SqxTables.LenCodes - 1; ++k)
      if (l >= SqxTables.LenOffsets[k] && l - SqxTables.LenOffsets[k] < 1u << SqxTables.LenExtraBits[k]) {
        extra = l - SqxTables.LenOffsets[k];
        return (uint)k;
      }

    return uint.MaxValue;
  }

  private static uint DistanceSymbol(uint dist, out uint extra) {
    var d = dist - 1;
    for (var c = 0; c < SqxTables.DistBase.Length; ++c)
      if (d >= SqxTables.DistBase[c] && d - SqxTables.DistBase[c] < 1u << SqxTables.DistExtraBits[c]) {
        extra = d - SqxTables.DistBase[c];
        return (uint)c;
      }

    extra = 0;
    return uint.MaxValue;
  }

  // --------------------------------------------------------------- tables

  /// <summary>Huffman code lengths of at most <paramref name="limit"/> bits; null when no symbol occurs.</summary>
  private static byte[]? LengthsFromFrequencies(ReadOnlySpan<uint> freq, int limit) {
    var n = freq.Length;
    var used = 0;
    foreach (var f in freq)
      if (f > 0)
        ++used;
    if (used == 0)
      return null;

    var tmp = new uint[n];
    var depth = new int[n];
    var maxDepth = 0;
    for (var shift = 0; shift < 40; ++shift) {
      ulong total = 0;
      for (var i = 0; i < n; ++i) {
        tmp[i] = freq[i] != 0 ? (shift < 32 ? freq[i] >> shift : 0) : 0;
        total += tmp[i];
      }

      if (shift > 0 || total == 0)
        for (var i = 0; i < n; ++i)
          if (freq[i] != 0 && tmp[i] == 0)
            tmp[i] = 1;

      if (used == 1)
        for (var i = 0; i < n; ++i)
          if (tmp[i] == 0) {
            tmp[i] = 1;
            break;
          }

      maxDepth = Depths(tmp, depth);
      if (maxDepth <= limit)
        break;
    }

    if (maxDepth > limit)
      return null;
    var lens = new byte[n];
    for (var i = 0; i < n; ++i)
      lens[i] = (byte)depth[i];
    return lens;
  }

  /// <summary>Plain Huffman depths, merging the two lightest nodes (ties by node index) until one remains.</summary>
  private static int Depths(uint[] freq, int[] depth) {
    var n = freq.Length;
    var active = new List<(ulong F, int Index)>(n);
    var parent = new int[2 * n + 1];
    Array.Fill(parent, -1);
    for (var i = 0; i < n; ++i)
      if (freq[i] > 0)
        active.Add((freq[i], i));
    var next = n;
    while (active.Count > 1) {
      var a = active[0];
      var b = active[1];
      parent[a.Index] = next;
      parent[b.Index] = next;
      var c = (F: a.F + b.F, Index: next++);
      active.RemoveRange(0, 2);
      var at = 0;
      while (at < active.Count && !(active[at].F > c.F || (active[at].F == c.F && active[at].Index > c.Index)))
        ++at;
      active.Insert(at, c);
    }

    var max = 0;
    for (var i = 0; i < n; ++i) {
      if (freq[i] == 0) {
        depth[i] = 0;
        continue;
      }

      var d = 0;
      for (var node = i; parent[node] >= 0 && d <= 64; node = parent[node])
        ++d;
      depth[i] = d;
      max = Math.Max(max, d);
    }

    return max;
  }

  private static void MetaFrequencies(byte[] lens, uint[] metaFreq) {
    for (var i = 0; i < lens.Length;) {
      var v = lens[i];
      var run = 1;
      while (i + run < lens.Length && lens[i + run] == v)
        ++run;
      if (v == 0 && run >= 11) {
        ++metaFreq[18];
        i += Math.Min(run, 138);
      } else if (v == 0 && run >= 3) {
        ++metaFreq[17];
        i += Math.Min(run, 10);
      } else if (i > 0 && v == lens[i - 1] && run >= 3) {
        ++metaFreq[16];
        i += Math.Min(run, 6);
      } else {
        ++metaFreq[v];
        ++i;
      }
    }
  }

  private static void WriteTree(SqxBitWriter w, byte[] lens, ushort[] metaCodes, byte[] metaLens) {
    for (var i = 0; i < lens.Length;) {
      var v = lens[i];
      var run = 1;
      while (i + run < lens.Length && lens[i + run] == v)
        ++run;
      if (v == 0 && run >= 11) {
        var r = Math.Min(run, 138);
        w.Write(metaCodes[18], metaLens[18]);
        w.Write((uint)(r - 11), 7);
        i += r;
      } else if (v == 0 && run >= 3) {
        var r = Math.Min(run, 10);
        w.Write(metaCodes[17], metaLens[17]);
        w.Write((uint)(r - 3), 3);
        i += r;
      } else if (i > 0 && v == lens[i - 1] && run >= 3) {
        var r = Math.Min(run, 6);
        w.Write(metaCodes[16], metaLens[16]);
        w.Write((uint)(r - 3), 2);
        i += r;
      } else {
        w.Write(metaCodes[v], metaLens[v]);
        ++i;
      }
    }
  }
}
