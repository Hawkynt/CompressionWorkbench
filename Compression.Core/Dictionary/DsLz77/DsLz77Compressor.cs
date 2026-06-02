using System.Buffers.Binary;
using Compression.Core.BitIO;
using Compression.Core.BuildingBlocks;

namespace Compression.Core.Dictionary.DsLz77;

/// <summary>
/// DoubleSpace/DriveSpace LZ77 compressor exposed as a tunable, effort-aware
/// algorithm primitive.
/// <para>
/// The on-the-wire bit-stream grammar is identical to
/// <see cref="DoubleSpaceCompressor"/> — a 4-byte little-endian original-size
/// header followed by LSB-first literal / match tokens — so a stream produced
/// by this class round-trips through the existing DoubleSpace decoder and
/// through <see cref="DsLz77Decompressor"/>.
/// </para>
/// <para>
/// What this class adds on top of the fixed-effort
/// <see cref="DoubleSpaceCompressor"/> implementation is a parse-effort knob
/// (<c>effort</c>):
/// </para>
/// <list type="bullet">
///   <item><b>0 — Greedy</b>: take the first sufficiently long match.
///     Bounded hash-chain depth (128). Matches the existing
///     <see cref="DoubleSpaceCompressor"/> behaviour. Fast.</item>
///   <item><b>1 — Lazy</b>: emit a literal when the next position has a
///     strictly longer match. Deeper hash chain (1024). Roughly 10× slower,
///     typically a few percent smaller for compressible inputs.</item>
///   <item><b>2+ — Iterated</b>: run multiple lazy passes with progressively
///     deeper chains and keep the best result. Roughly 100× slower at
///     effort 2; effort 3 only marginally improves further. This is a
///     pragmatic stand-in for full Zopfli-style optimal parsing.</item>
/// </list>
/// <para>
/// Sliding-window size defaults to the DoubleSpace 4 KiB cap and may be
/// overridden up to the format-defined maximum of 12 608 bytes — useful for
/// the DriveSpace (DVRS) 8 KiB variant.
/// </para>
/// </summary>
public sealed class DsLz77Compressor {

  /// <summary>Default sliding-window size (matches DoubleSpace 4 KiB).</summary>
  public const int DefaultMaxDistance = 4096;

  /// <summary>DriveSpace sliding-window size (8 KiB).</summary>
  public const int DriveSpaceMaxDistance = 8192;

  private const int MinMatchLength = 2;
  private const int MaxMatchLength = 323;

  /// <summary>
  /// Compresses <paramref name="input"/> at the requested
  /// <paramref name="effort"/> level (clamped to <c>[0, 3]</c>). The output
  /// is the standard DoubleSpace/DriveSpace BB stream (4-byte LE
  /// uncompressed-size header followed by the LSB-first token bit stream).
  /// </summary>
  public static byte[] Compress(ReadOnlySpan<byte> input, int effort = 0)
    => Compress(input, DefaultMaxDistance, effort);

  /// <summary>
  /// Compresses with an explicit sliding-window cap. <paramref name="maxDistance"/>
  /// must lie in <c>[1, 12608]</c>; pass <see cref="DriveSpaceMaxDistance"/>
  /// for DVRS.
  /// </summary>
  public static byte[] Compress(ReadOnlySpan<byte> input, int maxDistance, int effort) {
    ArgumentOutOfRangeException.ThrowIfLessThan(maxDistance, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDistance, 12608);

    var clampedEffort = Math.Clamp(effort, 0, 3);

    return clampedEffort switch {
      0 => DoubleSpaceCompressor.CompressWithWindow(input, maxDistance),
      1 => CompressLazy(input, maxDistance, chainDepth: 1024),
      _ => CompressIterated(input, maxDistance, clampedEffort),
    };
  }

  // =========================================================================
  //                         Effort 1 — lazy matching
  // =========================================================================

  /// <summary>
  /// Lazy-matching parse: at each candidate match position, look ahead one
  /// byte and emit a literal instead if the next position has a strictly
  /// longer match. Costs one extra match-finder call per position but
  /// typically shaves a few percent off the output vs greedy DS LZ77.
  /// </summary>
  private static byte[] CompressLazy(ReadOnlySpan<byte> data, int maxDistance, int chainDepth) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var writer = new BitWriter<LsbBitOrder>(ms);

    const int HashSize = 1 << 14;
    var hashHead = new int[HashSize];
    var hashNext = new int[data.Length];
    Array.Fill(hashHead, -1);
    Array.Fill(hashNext, -1);

    var pos = 0;
    while (pos < data.Length) {
      InsertHash(data, pos, hashHead, hashNext);
      var (curLen, curOff) = FindBestMatch(data, pos, maxDistance, hashHead, hashNext, chainDepth);

      if (curLen < MinMatchLength) {
        // No usable match — emit a literal.
        writer.WriteBit(0);
        writer.WriteBits(data[pos], 8);
        ++pos;
        continue;
      }

      // Lazy look-ahead: would (pos + 1) give a longer match?
      var nextPos = pos + 1;
      var preferLiteral = false;
      if (nextPos < data.Length) {
        InsertHash(data, nextPos, hashHead, hashNext);
        var (nextLen, _) = FindBestMatch(data, nextPos, maxDistance, hashHead, hashNext, chainDepth);
        if (nextLen > curLen)
          preferLiteral = true;
      }

      if (preferLiteral) {
        writer.WriteBit(0);
        writer.WriteBits(data[pos], 8);
        ++pos;
        continue;
      }

      EmitMatch(writer, curLen, curOff);
      // Update hash chain for interior bytes of the match (we already
      // inserted pos and pos+1; insert the rest).
      for (var j = 2; j < curLen; ++j) {
        var p = pos + j;
        if (p + 1 < data.Length)
          InsertHash(data, p, hashHead, hashNext);
      }
      pos += curLen;
    }

    writer.FlushBits();
    return ms.ToArray();
  }

  // =========================================================================
  //                  Effort 2+ — iterated multi-pass parse
  // =========================================================================

  /// <summary>
  /// Runs several parses with progressively deeper match-chain depths and
  /// keeps the smallest output. Effort 2 explores 2 extra passes; effort 3
  /// explores 4. Always falls back to at least the baseline greedy result so
  /// the iterated path never produces worse output than effort 0.
  /// </summary>
  private static byte[] CompressIterated(ReadOnlySpan<byte> data, int maxDistance, int effort) {
    // Pre-materialise spans we'll reuse — the iterations cannot share state
    // because each one needs its own fresh hash chains.
    var asArray = data.ToArray();

    var best = DoubleSpaceCompressor.CompressWithWindow(asArray, maxDistance);

    var depthSchedule = effort >= 3
      ? new[] { 1024, 2048, 4096, 8192 }
      : new[] { 1024, 4096 };

    foreach (var depth in depthSchedule) {
      var candidate = CompressLazy(asArray, maxDistance, chainDepth: depth);
      if (candidate.Length < best.Length)
        best = candidate;
    }

    return best;
  }

  // =========================================================================
  //                              Helpers
  // =========================================================================

  private static int Hash2(ReadOnlySpan<byte> data, int pos)
    => ((data[pos] << 6) ^ data[pos + 1]) & 0x3FFF;

  private static void InsertHash(ReadOnlySpan<byte> data, int pos, int[] hashHead, int[] hashNext) {
    if (pos + 1 >= data.Length) return;
    var h = Hash2(data, pos);
    hashNext[pos] = hashHead[h];
    hashHead[h] = pos;
  }

  private static (int Length, int Offset) FindBestMatch(
      ReadOnlySpan<byte> data, int pos, int maxDistance,
      int[] hashHead, int[] hashNext, int maxChainLen) {

    if (pos + MinMatchLength > data.Length)
      return (0, 0);

    var bestLen = 0;
    var bestOff = 0;
    var minPos = Math.Max(0, pos - maxDistance);
    var idx = hashNext[pos];
    var chainLen = 0;
    var maxLen = Math.Min(data.Length - pos, MaxMatchLength);

    while (idx >= minPos && idx < pos && chainLen < maxChainLen) {
      if (data[idx] == data[pos] && data[idx + 1] == data[pos + 1]) {
        var len = 2;
        while (len < maxLen && data[idx + len] == data[pos + len])
          ++len;
        if (len > bestLen) {
          bestLen = len;
          bestOff = pos - idx;
          if (bestLen >= maxLen) break;
        }
      }
      idx = hashNext[idx];
      ++chainLen;
    }
    return (bestLen, bestOff);
  }

  private static void EmitMatch(BitWriter<LsbBitOrder> writer, int length, int distance) {
    writer.WriteBit(1);
    EncodeLength(writer, length);
    EncodeDistance(writer, distance);
  }

  private static void EncodeLength(BitWriter<LsbBitOrder> writer, int length) {
    if (length == 2) { writer.WriteBits(0, 2); return; }
    if (length == 3) { writer.WriteBits(1, 2); return; }
    if (length == 4) { writer.WriteBits(2, 2); return; }

    writer.WriteBits(3, 2);
    var extended = length - 5;
    if (extended < 63) {
      writer.WriteBits((uint)extended, 6);
      return;
    }

    writer.WriteBits(63, 6);
    writer.WriteBits((uint)(length - 68), 8);
  }

  private static void EncodeDistance(BitWriter<LsbBitOrder> writer, int distance) {
    var classes = DoubleSpaceCompressor.DistanceClasses;
    for (var cls = 0; cls < classes.Length; ++cls) {
      var (bits, baseVal, max) = classes[cls];
      if (distance <= max) {
        writer.WriteBits((uint)cls, 2);
        writer.WriteBits((uint)(distance - baseVal), bits);
        return;
      }
    }
    throw new InvalidDataException($"DsLz77: distance {distance} exceeds maximum class range.");
  }
}
