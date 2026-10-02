using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Compression.Core.Deflate;

/// <summary>
/// Streaming DEFLATE encoder (RFC 1951) for the hash-chain levels 1–9.
/// </summary>
/// <remarks>
/// <para>The match search is the one RFC 1951 §4 recommends: three-byte strings are hashed
/// into chains over a 32 KB window held in a 64 KB buffer that slides by 32 KB, chains are
/// cut off after a level-dependent number of probes, and levels 4–9 defer each match by one
/// byte to see whether the next position starts a longer one ("lazy matching"). Levels 1–3
/// take matches greedily and insert only the first strings of short matches into the hash.</para>
/// <para>The per-level search limits (good/lazy/nice length and chain depth) are the agreed
/// values of zlib's configuration table, so a level here does the same amount of work as the
/// same zlib level. Each block is emitted as stored, fixed- or dynamic-Huffman, whichever is
/// smallest, with length-limited code lengths from the shared deterministic builder. The
/// output depends only on the input and the level — never on the platform.</para>
/// </remarks>
internal sealed partial class DeflateEncoder {
  private const int WSize = DeflateConstants.WindowSize;
  private const int WMask = WSize - 1;
  private const int MinMatch = 3;
  private const int MaxMatch = 258;
  private const int MinLookahead = MaxMatch + MinMatch + 1;
  private const int MaxDist = WSize - MinLookahead;
  private const int HashBits = 15;
  private const int TooFar = 4096;
  private const int SymbolBufferSize = 1 << 14;
  private const int OutputBufferSize = 1 << 16;

  /// <summary>zlib's per-level search limits: good, lazy (or max insert), nice length and chain depth.</summary>
  private static readonly (int Good, int Lazy, int Nice, int Chain, bool Greedy)[] Configs = [
    (0, 0, 0, 0, true),
    (4, 4, 8, 4, true),
    (4, 5, 16, 8, true),
    (4, 6, 32, 32, true),
    (4, 4, 16, 16, false),
    (8, 16, 32, 32, false),
    (8, 16, 128, 128, false),
    (8, 32, 128, 256, false),
    (32, 128, 258, 1024, false),
    (32, 258, 258, 4096, false),
  ];

  private static readonly byte[] LengthCodes = BuildLengthCodes();
  private static readonly byte[] DistanceCodes = BuildDistanceCodes();
  private static readonly int[] StaticLitLengths = DeflateConstants.GetStaticLiteralLengths();
  private static readonly int[] StaticDistLengths = DeflateConstants.GetStaticDistanceLengths();
  private static readonly ushort[] StaticLitCodes = BuildCodes(StaticLitLengths);
  private static readonly ushort[] StaticDistCodes = BuildCodes(StaticDistLengths);

  private readonly Stream _output;
  private readonly int _good, _lazy, _nice, _chain;
  private readonly bool _greedy;

  private readonly byte[] _window = new byte[2 * WSize + MaxMatch + 8];
  private readonly ushort[] _head = new ushort[1 << HashBits];
  private readonly ushort[] _prev = new ushort[WSize];
  private int _strStart;
  private int _lookahead;
  private int _blockStart;
  private int _matchStart;
  private int _matchLength = MinMatch - 1;
  private bool _matchAvailable;

  private readonly byte[] _symLength = new byte[SymbolBufferSize];
  private readonly ushort[] _symDistance = new ushort[SymbolBufferSize];
  private int _symCount;
  private readonly long[] _litFreq = new long[DeflateConstants.LiteralLengthAlphabetSize];
  private readonly long[] _distFreq = new long[DeflateConstants.DistanceAlphabetSize];
  private readonly long[] _distTreeFreq = new long[DeflateConstants.DistanceAlphabetSize];

  private readonly byte[] _out = new byte[OutputBufferSize];
  private int _outPos;
  private ulong _bitBuf;
  private int _bitCount;

  /// <summary>Creates an encoder writing to <paramref name="output"/> at zlib level <paramref name="level"/> (1–9).</summary>
  public DeflateEncoder(Stream output, int level) {
    ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 9);
    this._output = output;
    (this._good, this._lazy, this._nice, this._chain, this._greedy) = Configs[level];
  }

  /// <summary>Feeds more input.</summary>
  public void Write(ReadOnlySpan<byte> data) {
    while (!data.IsEmpty) {
      // Slide only once the window is full. A full window has been compressed down to less
      // than MinLookahead, so the scan is past WSize + MaxDist and the slide loses nothing in
      // reach — and because it happens at a point fixed by the data alone, the output does not
      // depend on how the input was split across Write calls.
      var free = 2 * WSize - (this._strStart + this._lookahead);
      if (free == 0) {
        Debug.Assert(this._strStart >= WSize + MaxDist);
        this.Slide();
        free = WSize;
      }

      var n = Math.Min(free, data.Length);
      data[..n].CopyTo(this._window.AsSpan(this._strStart + this._lookahead));
      this._lookahead += n;
      data = data[n..];
      this.Compress(flush: false);
    }
  }

  /// <summary>Compresses whatever is left, writes the final block and flushes everything.</summary>
  public void Finish() {
    this.Compress(flush: true);
    this.FlushBlock(last: true);
    if (this._bitCount > 0) {
      var bytes = (this._bitCount + 7) >> 3;
      for (var i = 0; i < bytes; ++i)
        this._out[this._outPos++] = (byte)(this._bitBuf >> (i * 8));
      this._bitBuf = 0;
      this._bitCount = 0;
    }

    this.FlushOutput();
  }

  // ── match search ─────────────────────────────────────────────────────

  private void Compress(bool flush) {
    if (this._greedy)
      this.CompressGreedy(flush);
    else
      this.CompressLazy(flush);
  }

  private void CompressGreedy(bool flush) {
    var window = this._window;
    while (true) {
      if (this._lookahead < MinLookahead && (!flush || this._lookahead == 0))
        return;

      var hashHead = this._lookahead >= MinMatch ? this.Insert(this._strStart) : 0;
      var matchLength = 0;
      if (hashHead != 0 && this._strStart - hashHead <= MaxDist)
        matchLength = this.LongestMatch(hashHead, MinMatch - 1);

      if (matchLength >= MinMatch) {
        this.TallyMatch(this._strStart - this._matchStart, matchLength);
        this._lookahead -= matchLength;
        if (matchLength <= this._lazy && this._lookahead >= MinMatch) {
          for (var i = 1; i < matchLength; ++i)
            this.Insert(this._strStart + i);
        }

        this._strStart += matchLength;
      } else {
        this.TallyLiteral(window[this._strStart]);
        --this._lookahead;
        ++this._strStart;
      }

      if (this._symCount == SymbolBufferSize)
        this.FlushBlock(last: false);
    }
  }

  private void CompressLazy(bool flush) {
    var window = this._window;
    while (true) {
      if (this._lookahead < MinLookahead && (!flush || this._lookahead == 0))
        break;

      var hashHead = this._lookahead >= MinMatch ? this.Insert(this._strStart) : 0;
      var prevLength = this._matchLength;
      var prevMatch = this._matchStart;
      this._matchLength = MinMatch - 1;

      if (hashHead != 0 && prevLength < this._lazy && this._strStart - hashHead <= MaxDist) {
        this._matchLength = this.LongestMatch(hashHead, prevLength);

        // A minimal match far away costs more than the three literals it replaces.
        if (this._matchLength == MinMatch && this._strStart - this._matchStart > TooFar)
          this._matchLength = MinMatch - 1;
      }

      if (prevLength >= MinMatch && this._matchLength <= prevLength) {
        // The match found one position back is at least as good: emit it.
        var maxInsert = this._strStart + this._lookahead - MinMatch;
        this.TallyMatch(this._strStart - 1 - prevMatch, prevLength);
        this._lookahead -= prevLength - 1;
        for (var i = prevLength - 2; i > 0; --i)
          if (++this._strStart <= maxInsert)
            this.Insert(this._strStart);

        this._matchAvailable = false;
        this._matchLength = MinMatch - 1;
        ++this._strStart;
        if (this._symCount == SymbolBufferSize)
          this.FlushBlock(last: false);
      } else if (this._matchAvailable) {
        // The previous position did not start a better match than this one: it is a literal.
        this.TallyLiteral(window[this._strStart - 1]);
        if (this._symCount == SymbolBufferSize)
          this.FlushBlock(last: false);
        ++this._strStart;
        --this._lookahead;
      } else {
        this._matchAvailable = true;
        ++this._strStart;
        --this._lookahead;
      }
    }

    if (flush && this._matchAvailable) {
      this.TallyLiteral(window[this._strStart - 1]);
      this._matchAvailable = false;
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private int Insert(int position) {
    // Multiplicative hash of the three bytes at position. The fourth byte loaded alongside
    // is masked off; the window's tail slack keeps that load in bounds.
    var hash = (int)((Load32(this._window, position) & 0xFFFFFF) * 0x9E3779B1u >> (32 - HashBits));
    ref var head = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(this._head), hash);
    int previous = head;
    Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(this._prev), position & WMask) = (ushort)previous;
    head = (ushort)position;
    return previous;
  }

  /// <summary>
  /// Walks the hash chain from <paramref name="current"/> for the longest match at the
  /// current position that beats <paramref name="best"/>.
  /// </summary>
  /// <remarks>
  /// Bytes are compared eight at a time. Every read stays inside the window array: the scan
  /// never passes <c>_strStart + _lookahead + 7 &lt;= 2 * WSize + 7</c>, candidates lie before
  /// the scan, and the array carries <c>MaxMatch + 8</c> bytes of slack past the window.
  /// </remarks>
  private int LongestMatch(int current, int best) {
    ref var window = ref MemoryMarshal.GetArrayDataReference(this._window);
    ref var prev = ref MemoryMarshal.GetArrayDataReference(this._prev);
    var scan = this._strStart;
    var chain = this._chain;
    var nice = Math.Min(this._nice, this._lookahead);
    var maxLength = Math.Min(MaxMatch, this._lookahead);
    var limit = scan > MaxDist ? scan - MaxDist : 0;
    if (best >= this._good)
      chain >>= 2;

    var scanStart = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref window, scan));
    var scanEnd = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref window, scan + best - 1));
    do {
      // Quick rejects: the two bytes ending at the best length so far, then the first two.
      if (Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref window, current + best - 1)) != scanEnd
          || Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref window, current)) != scanStart)
        continue;

      var length = 2;
      while (true) {
        var diff = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref window, current + length))
                   ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref window, scan + length));
        if (diff != 0) {
          // The first differing byte is the lowest one in memory order.
          length += (BitConverter.IsLittleEndian ? BitOperations.TrailingZeroCount(diff) : BitOperations.LeadingZeroCount(diff)) >> 3;
          break;
        }

        length += 8;
        if (length >= maxLength)
          break;
      }

      if (length > maxLength)
        length = maxLength;

      if (length > best) {
        this._matchStart = current;
        best = length;
        if (length >= nice)
          break;
        scanEnd = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref window, scan + best - 1));
      }
    } while ((current = Unsafe.Add(ref prev, current & WMask)) > limit && --chain != 0);

    return Math.Min(best, this._lookahead);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Load32(byte[] array, int index) {
    var value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), index));
    return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
  }

  private void Slide() {
    Buffer.BlockCopy(this._window, WSize, this._window, 0, WSize);
    this._matchStart -= WSize;
    this._strStart -= WSize;
    this._blockStart -= WSize;

    SlideTable(this._head);
    SlideTable(this._prev);
  }

  /// <summary>
  /// Moves every position in <paramref name="table"/> down by the slide distance, clamping
  /// positions that fall out of the window to 0 (no candidate).
  /// </summary>
  /// <remarks>
  /// <c>max(v, WSize) - WSize</c> is exactly that clamp for unsigned values, so whole vectors
  /// go at once; both tables are a multiple of every vector width in length.
  /// </remarks>
  private static void SlideTable(ushort[] table) {
    var window = new Vector<ushort>(WSize);
    var span = MemoryMarshal.Cast<ushort, Vector<ushort>>(table.AsSpan());
    for (var i = 0; i < span.Length; ++i)
      span[i] = Vector.Max(span[i], window) - window;
  }

  // ── symbols ──────────────────────────────────────────────────────────

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void TallyLiteral(byte value) {
    this._symLength[this._symCount] = value;
    this._symDistance[this._symCount++] = 0;
    ++this._litFreq[value];
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void TallyMatch(int distance, int length) {
    this._symLength[this._symCount] = (byte)(length - MinMatch);
    this._symDistance[this._symCount++] = (ushort)distance;
    ++this._litFreq[257 + LengthCodes[length - MinMatch]];
    ++this._distFreq[DistanceCode(distance)];
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int DistanceCode(int distance) {
    var d = distance - 1;
    return d < 256 ? DistanceCodes[d] : DistanceCodes[256 + (d >> 7)];
  }

  // ── blocks ───────────────────────────────────────────────────────────

  private void FlushBlock(bool last) {
    var litFreq = this._litFreq;
    var distFreq = this._distFreq;
    litFreq[DeflateConstants.EndOfBlock] = 1;

    var distForTree = this._distTreeFreq;
    distFreq.CopyTo(distForTree, 0);
    ZopfliBlockCost.EnsureDistanceCode(distForTree);
    var litLengths = ZopfliBlockCost.BuildCodeLengths(litFreq, DeflateConstants.MaxBits);
    var distLengths = ZopfliBlockCost.BuildCodeLengths(distForTree, DeflateConstants.MaxBits);
    var header = DynamicHeader.Create(litLengths, distLengths);

    long extraBits = 0;
    for (var code = 0; code < 29; ++code)
      extraBits += litFreq[257 + code] * DeflateConstants.LengthExtraBits[code];
    for (var code = 0; code < 30; ++code)
      extraBits += distFreq[code] * DeflateConstants.DistanceExtraBits[code];

    var dynamicBits = 3 + header.Bits + extraBits + CodedBits(litFreq, litLengths) + CodedBits(distFreq, distLengths);
    var staticBits = 3 + extraBits + CodedBits(litFreq, StaticLitLengths) + CodedBits(distFreq, StaticDistLengths);

    var storedLength = this._strStart - this._blockStart;
    var canStore = this._blockStart >= 0 && storedLength <= 2 * WSize;
    var storedBits = canStore ? StoredBits(storedLength) : long.MaxValue;

    if (storedBits <= staticBits && storedBits <= dynamicBits)
      this.EmitStored(this._window.AsSpan(this._blockStart, storedLength), last);
    else if (staticBits <= dynamicBits) {
      this.PutBits(last ? 3u : 2u, 3);
      this.EmitSymbols(StaticLitLengths, StaticLitCodes, StaticDistLengths, StaticDistCodes);
    } else {
      this.PutBits(last ? 5u : 4u, 3);
      header.Write(this);
      this.EmitSymbols(litLengths, BuildCodes(litLengths), distLengths, BuildCodes(distLengths));
    }

    Array.Clear(litFreq);
    Array.Clear(distFreq);
    this._symCount = 0;
    this._blockStart = this._strStart;
  }

  private long StoredBits(int length) {
    // Header, pad to the byte boundary, LEN/NLEN, then the bytes; every further 64 KB chunk
    // starts aligned, so its three header bits always cost a full byte.
    var chunks = Math.Max(1, (length + 65534) / 65535);
    var firstPad = (8 - ((this._bitCount + 3) & 7)) & 7;
    return 3 + firstPad + 32 + (chunks - 1) * (8 + 32) + length * 8L;
  }

  private static long CodedBits(long[] frequencies, int[] lengths) {
    long bits = 0;
    var count = Math.Min(frequencies.Length, lengths.Length);
    for (var i = 0; i < count; ++i)
      bits += frequencies[i] * lengths[i];
    return bits;
  }

  private void EmitStored(ReadOnlySpan<byte> data, bool last) {
    do {
      var chunk = Math.Min(data.Length, 65535);
      var final = last && chunk == data.Length;
      this.PutBits(final ? 1u : 0u, 3);
      this.AlignToByte();
      this.PutBits((uint)chunk | ((uint)(ushort)~chunk << 16), 32);
      this.FlushBitsToBytes();
      while (!data.IsEmpty && chunk > 0) {
        if (this._outPos == this._out.Length)
          this.FlushOutput();
        var n = Math.Min(chunk, this._out.Length - this._outPos);
        data[..n].CopyTo(this._out.AsSpan(this._outPos));
        this._outPos += n;
        data = data[n..];
        chunk -= n;
      }
    } while (!data.IsEmpty);
  }

  private void EmitSymbols(int[] litLengths, ushort[] litCodes, int[] distLengths, ushort[] distCodes) {
    var lengthExtra = DeflateConstants.LengthExtraBits;
    var lengthBase = DeflateConstants.LengthBase;
    var distExtra = DeflateConstants.DistanceExtraBits;
    var distBase = DeflateConstants.DistanceBase;

    for (var i = 0; i < this._symCount; ++i) {
      int distance = this._symDistance[i];
      int value = this._symLength[i];
      if (distance == 0) {
        this.PutBits(litCodes[value], litLengths[value]);
        continue;
      }

      var lengthCode = LengthCodes[value];
      var symbol = 257 + lengthCode;
      var bits = (ulong)litCodes[symbol];
      var count = litLengths[symbol];
      var extra = lengthExtra[lengthCode];
      bits |= (ulong)(value + MinMatch - lengthBase[lengthCode]) << count;
      count += extra;

      var distCode = DistanceCode(distance);
      bits |= (ulong)distCodes[distCode] << count;
      count += distLengths[distCode];
      bits |= (ulong)(distance - distBase[distCode]) << count;
      count += distExtra[distCode];

      // At most 15 + 5 + 15 + 13 = 48 bits.
      this.PutBits(bits, count);
    }

    this.PutBits(litCodes[DeflateConstants.EndOfBlock], litLengths[DeflateConstants.EndOfBlock]);
  }

  // ── bit output ───────────────────────────────────────────────────────

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void PutBits(ulong value, int count) {
    if (this._bitCount + count > 64) {
      // Spill whole bytes first so the accumulator has room.
      this.FlushBitsToBytes();
    }

    this._bitBuf |= value << this._bitCount;
    this._bitCount += count;
    if (this._bitCount >= 32) {
      if (this._outPos > this._out.Length - 8)
        this.FlushOutput();
      BinaryPrimitives.WriteUInt32LittleEndian(this._out.AsSpan(this._outPos), (uint)this._bitBuf);
      this._outPos += 4;
      this._bitBuf >>= 32;
      this._bitCount -= 32;
    }
  }

  private void FlushBitsToBytes() {
    while (this._bitCount >= 8) {
      if (this._outPos == this._out.Length)
        this.FlushOutput();
      this._out[this._outPos++] = (byte)this._bitBuf;
      this._bitBuf >>= 8;
      this._bitCount -= 8;
    }
  }

  private void AlignToByte() {
    var pad = (8 - (this._bitCount & 7)) & 7;
    this._bitCount += pad;
    this.FlushBitsToBytes();
  }

  private void FlushOutput() {
    if (this._outPos == 0)
      return;
    this._output.Write(this._out, 0, this._outPos);
    this._outPos = 0;
  }

  // ── tables ───────────────────────────────────────────────────────────

  /// <summary>Canonical codes (RFC 1951 §3.2.2), bit-reversed for LSB-first output.</summary>
  private static ushort[] BuildCodes(int[] lengths) {
    Span<int> count = stackalloc int[16];
    foreach (var length in lengths)
      ++count[length];
    count[0] = 0;

    Span<int> next = stackalloc int[16];
    var code = 0;
    for (var length = 1; length <= 15; ++length) {
      code = (code + count[length - 1]) << 1;
      next[length] = code;
    }

    var codes = new ushort[lengths.Length];
    for (var symbol = 0; symbol < lengths.Length; ++symbol)
      if (lengths[symbol] > 0)
        codes[symbol] = (ushort)DeflateHuffmanTable.ReverseBits((uint)next[lengths[symbol]]++, lengths[symbol]);
    return codes;
  }

  private static byte[] BuildLengthCodes() {
    var table = new byte[MaxMatch - MinMatch + 1];
    for (var length = MinMatch; length <= MaxMatch; ++length)
      table[length - MinMatch] = (byte)(DeflateConstants.GetLengthCode(length) - 257);
    return table;
  }

  // Distances 1–256 directly, larger ones by (distance - 1) >> 7, which is exact because every
  // code above 15 spans a multiple of 128 distances.
  private static byte[] BuildDistanceCodes() {
    var table = new byte[512];
    for (var d = 0; d < 256; ++d)
      table[d] = (byte)DeflateConstants.GetDistanceCode(d + 1);
    for (var i = 2; i < 256; ++i)
      table[256 + i] = (byte)DeflateConstants.GetDistanceCode((i << 7) + 1);
    return table;
  }
}
