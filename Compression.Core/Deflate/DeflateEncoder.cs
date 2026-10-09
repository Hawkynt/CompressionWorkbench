using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Compression.Core.Deflate;

/// <summary>
/// Streaming DEFLATE encoder (RFC 1951) for the hash-chain levels 1–9.
/// </summary>
/// <remarks>
/// <para>The match search is the one RFC 1951 §4 recommends: strings are hashed into chains
/// over a 32 KB window held in a 64 KB buffer that slides by 32 KB, chains are cut off after a
/// level-dependent number of probes, and levels 4–9 defer each match by one byte to see
/// whether the next position starts a longer one ("lazy matching"). Levels 1–3 take matches
/// greedily and insert only the inner strings of short matches into the hash.</para>
/// <para>The chains are keyed on four bytes rather than three: a chain then holds only real
/// candidates for a match of four or more, which is most of what a three-byte chain walks past.
/// Matches of exactly three bytes come from a side table that remembers the latest position of
/// every three-byte hash, at short distances only, where they beat three literals. Runs of one
/// byte value are linked into their chain a vector at a time. (Four-byte hashing with a
/// three-byte side table is the arrangement of zlib-ng and libdeflate; no code was taken.)</para>
/// <para>The per-level search limits (good/lazy/nice length and chain depth) are the agreed
/// values of zlib's configuration table, so a level here does the same amount of work as the
/// same zlib level. Each block is emitted as stored, fixed- or dynamic-Huffman, whichever is
/// smallest, with length-limited code lengths from the shared deterministic builder. The
/// output depends only on the input and the level — never on the platform, nor on how the
/// input is split across <see cref="Write"/> calls.</para>
/// </remarks>
internal sealed partial class DeflateEncoder {
  private const int WSize = DeflateConstants.WindowSize;
  private const int WMask = WSize - 1;
  private const int MinMatch = 3;
  private const int MaxMatch = 258;
  private const int MinLookahead = MaxMatch + MinMatch + 1;
  private const int MaxDist = WSize - MinLookahead;
  private const int HashBits = 15;
  private const int Hash3Bits = 14;
  private const int TooFar = 4096;
  private const int GreedyMatch3Reach = 1024;
  private const uint HashMultiplier = 0x9E3779B1u;
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
  private readonly ushort[] _head3 = new ushort[1 << Hash3Bits];
  private int _strStart;
  private int _lookahead;
  private int _blockStart;
  private int _matchStart;
  private int _matchLength = MinMatch - 1;
  private bool _matchAvailable;

  // A symbol is a literal/length index — the literal, or 256 + length - MinMatch — and a
  // distance, 0 for a literal.
  private readonly ushort[] _symLitLen = new ushort[SymbolBufferSize];
  private readonly ushort[] _symDistance = new ushort[SymbolBufferSize];
  private int _symCount;
  private readonly long[] _litFreq = new long[DeflateConstants.LiteralLengthAlphabetSize];
  private readonly long[] _distFreq = new long[DeflateConstants.DistanceAlphabetSize];
  private readonly long[] _distTreeFreq = new long[DeflateConstants.DistanceAlphabetSize];

  private readonly ulong[] _litLenEntries = new ulong[256 + MaxMatch - MinMatch + 1];
  private readonly ulong[] _distEntries = new ulong[DeflateConstants.DistanceAlphabetSize + 1];

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
  //
  // Both loops keep the scan state in locals and write it back to the fields only around
  // FlushBlock and on exit, so the JIT can hold it in registers across the hot path.

  private void Compress(bool flush) {
    if (this._greedy)
      this.CompressGreedy(flush);
    else
      this.CompressLazy(flush);
  }

  [MethodImpl(MethodImplOptions.AggressiveOptimization)]
  private void CompressGreedy(bool flush) {
    ref var window = ref MemoryMarshal.GetArrayDataReference(this._window);
    var tables = new HashTables(this);
    var symbols = new SymbolSink(this);
    var strStart = this._strStart;
    var lookahead = this._lookahead;
    var minimum = flush ? 1 : MinLookahead;
    var (chainLimit, niceLimit, maxInsert) = (this._chain, this._nice, this._lazy);

    while (lookahead >= minimum) {
      var matchLength = 0;
      var matchStart = 0;
      if (lookahead >= MinMatch) {
        var candidate = tables.Insert(ref window, strStart, out var candidate3);
        if (candidate != 0 && strStart - candidate <= MaxDist)
          matchLength = LongestMatch(ref window, ref tables.Prev, strStart, candidate, MinMatch - 1,
            chainLimit, Math.Min(niceLimit, lookahead), Math.Min(MaxMatch, lookahead), ref matchStart);

        if (matchLength < MinMatch && IsMatch3(ref window, strStart, candidate3, GreedyMatch3Reach)) {
          matchLength = MinMatch;
          matchStart = candidate3;
        }
      }

      if (matchLength >= MinMatch) {
        symbols.Match(strStart - matchStart, matchLength);
        lookahead -= matchLength;
        // Like zlib's fast levels, only short matches put their inner strings into the hash.
        if (matchLength <= maxInsert && lookahead >= MinMatch)
          tables.InsertRange(ref window, strStart + 1, strStart + matchLength);
        strStart += matchLength;
      } else {
        symbols.Literal(Unsafe.Add(ref window, strStart));
        --lookahead;
        ++strStart;
      }

      if (symbols.IsFull) {
        this._strStart = strStart;
        symbols.Flush(this);
      }
    }

    this._strStart = strStart;
    this._lookahead = lookahead;
    symbols.Store(this);
  }

  [MethodImpl(MethodImplOptions.AggressiveOptimization)]
  private void CompressLazy(bool flush) {
    ref var window = ref MemoryMarshal.GetArrayDataReference(this._window);
    var tables = new HashTables(this);
    var symbols = new SymbolSink(this);
    var strStart = this._strStart;
    var lookahead = this._lookahead;
    var matchStart = this._matchStart;
    var matchLength = this._matchLength;
    var matchAvailable = this._matchAvailable;
    var minimum = flush ? 1 : MinLookahead;
    var (good, lazy, niceLimit, chainLimit) = (this._good, this._lazy, this._nice, this._chain);

    while (lookahead >= minimum) {
      var candidate = 0;
      var candidate3 = 0;
      if (lookahead >= MinMatch)
        candidate = tables.Insert(ref window, strStart, out candidate3);

      var prevLength = matchLength;
      var prevMatch = matchStart;
      matchLength = MinMatch - 1;

      if (candidate != 0 && prevLength < lazy && strStart - candidate <= MaxDist) {
        matchLength = LongestMatch(ref window, ref tables.Prev, strStart, candidate, prevLength,
          prevLength >= good ? chainLimit >> 2 : chainLimit, Math.Min(niceLimit, lookahead), Math.Min(MaxMatch, lookahead), ref matchStart);

        // A minimal match far away costs more than the three literals it replaces.
        if (matchLength == MinMatch && strStart - matchStart > TooFar)
          matchLength = MinMatch - 1;
      }

      if (matchLength < MinMatch && prevLength < MinMatch && lookahead >= MinMatch && IsMatch3(ref window, strStart, candidate3, TooFar)) {
        matchLength = MinMatch;
        matchStart = candidate3;
      }

      if (prevLength >= MinMatch && matchLength <= prevLength) {
        // The match found one position back is at least as good: emit it, and put the strings
        // it covers into the hash.
        symbols.Match(strStart - 1 - prevMatch, prevLength);
        var end = strStart - 1 + prevLength;
        tables.InsertRange(ref window, strStart + 1, Math.Min(end, strStart + lookahead - MinMatch + 1));
        lookahead -= prevLength - 1;
        strStart = end;
        matchAvailable = false;
        matchLength = MinMatch - 1;
        if (symbols.IsFull) {
          this._strStart = strStart;
          symbols.Flush(this);
        }
      } else if (matchAvailable) {
        // The previous position did not start a better match than this one: it is a literal.
        symbols.Literal(Unsafe.Add(ref window, strStart - 1));
        if (symbols.IsFull) {
          this._strStart = strStart;
          symbols.Flush(this);
        }

        ++strStart;
        --lookahead;
      } else {
        matchAvailable = true;
        ++strStart;
        --lookahead;
      }
    }

    if (flush && matchAvailable) {
      symbols.Literal(Unsafe.Add(ref window, strStart - 1));
      matchAvailable = false;
    }

    this._strStart = strStart;
    this._lookahead = lookahead;
    this._matchStart = matchStart;
    this._matchLength = matchLength;
    this._matchAvailable = matchAvailable;
    symbols.Store(this);
  }

  /// <summary>
  /// Whether the three bytes at <paramref name="candidate"/> repeat at <paramref name="position"/>
  /// within <paramref name="maxDistance"/>.
  /// </summary>
  /// <remarks>
  /// The chains are keyed on four bytes, so a match of exactly three is found only through this
  /// side table, which remembers the latest position of every three-byte hash.
  /// </remarks>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static bool IsMatch3(ref byte window, int position, int candidate, int maxDistance)
    => candidate != 0
       && position - candidate <= maxDistance
       && ((Load32(ref window, candidate) ^ Load32(ref window, position)) & 0xFFFFFF) == 0;

  /// <summary>The hash heads and chain links, as references the hot loops keep in registers.</summary>
  private readonly ref struct HashTables {
    private readonly ref ushort _head;
    private readonly ref ushort _head3;
    public readonly ref ushort Prev;

    public HashTables(DeflateEncoder encoder) {
      this._head = ref MemoryMarshal.GetArrayDataReference(encoder._head);
      this._head3 = ref MemoryMarshal.GetArrayDataReference(encoder._head3);
      this.Prev = ref MemoryMarshal.GetArrayDataReference(encoder._prev);
    }

    /// <summary>
    /// Links the string at <paramref name="position"/> into its chain and returns the previous
    /// head (0: none), with the latest earlier position of its first three bytes in
    /// <paramref name="candidate3"/>.
    /// </summary>
    /// <remarks>
    /// Multiplicative hashes of the four and the three bytes at the position. The bytes past
    /// the data that the load may cover are window slack, and only steer the last strings,
    /// which no later string searches from.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Insert(ref byte window, int position, out int candidate3) {
      var value = Load32(ref window, position);
      ref var head3 = ref Unsafe.Add(ref this._head3, (int)((value << 8) * HashMultiplier >> (32 - Hash3Bits)));
      candidate3 = head3;
      head3 = (ushort)position;
      ref var head = ref Unsafe.Add(ref this._head, (int)(value * HashMultiplier >> (32 - HashBits)));
      int previous = head;
      Unsafe.Add(ref this.Prev, position & WMask) = (ushort)previous;
      head = (ushort)position;
      return previous;
    }

    /// <summary>Inserts every string from <paramref name="from"/> up to, not including, <paramref name="to"/>.</summary>
    /// <remarks>
    /// Inside a run of one byte value every string equals the one before it, so its chain link
    /// is simply the previous position and the heads only need their final value — written once
    /// the run ends instead of being read back and rewritten for every byte.
    /// </remarks>
    public void InsertRange(ref byte window, int from, int to) {
      if (from >= to)
        return;

      var value = Load32(ref window, from);
      ref var head3 = ref Unsafe.Add(ref this._head3, (int)((value << 8) * HashMultiplier >> (32 - Hash3Bits)));
      ref var head = ref Unsafe.Add(ref this._head, (int)(value * HashMultiplier >> (32 - HashBits)));
      Unsafe.Add(ref this.Prev, from & WMask) = head;
      for (var position = from + 1; position < to; ++position) {
        var next = Load32(ref window, position);
        if (next == value) {
          // Five equal bytes: a run, whose strings all equal this one until a byte breaks it.
          var room = to - position;
          var run = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref window, position + 3), room).IndexOfAnyExcept((byte)value);
          if (run < 0)
            run = room;

          this.LinkRun(position, run);
          position += run - 1;
          continue;
        }

        head3 = (ushort)(position - 1);
        head = (ushort)(position - 1);
        value = next;
        head3 = ref Unsafe.Add(ref this._head3, (int)((value << 8) * HashMultiplier >> (32 - Hash3Bits)));
        head = ref Unsafe.Add(ref this._head, (int)(value * HashMultiplier >> (32 - HashBits)));
        Unsafe.Add(ref this.Prev, position & WMask) = head;
      }

      head3 = (ushort)(to - 1);
      head = (ushort)(to - 1);
    }

    /// <summary>Links each of the <paramref name="count"/> strings from <paramref name="start"/> to the one before it.</summary>
    private readonly void LinkRun(int start, int count) {
      // The links are consecutive positions, stored a vector at a time; a run is shorter than
      // the window, so it wraps around the end of the chain table at most once.
      var index = start & WMask;
      var first = Math.Min(count, WSize - index);
      FillSequence(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref this.Prev, index), first), (ushort)(start - 1));
      if (first < count)
        FillSequence(MemoryMarshal.CreateSpan(ref this.Prev, count - first), (ushort)(start - 1 + first));
    }

    private static void FillSequence(Span<ushort> destination, ushort value) {
      var i = 0;
      if (Vector128.IsHardwareAccelerated && destination.Length >= Vector128<ushort>.Count) {
        var vector = Vector128.Create(value) + Vector128.Create((ushort)0, 1, 2, 3, 4, 5, 6, 7);
        var step = Vector128.Create((ushort)Vector128<ushort>.Count);
        for (; i <= destination.Length - Vector128<ushort>.Count; i += Vector128<ushort>.Count) {
          vector.CopyTo(destination[i..]);
          vector += step;
        }
      }

      for (; i < destination.Length; ++i)
        destination[i] = (ushort)(value + i);
    }
  }

  /// <summary>
  /// Walks the hash chain from <paramref name="current"/> for the longest match at
  /// <paramref name="scan"/> that beats <paramref name="best"/>.
  /// </summary>
  /// <remarks>
  /// Bytes are compared eight at a time. Every read stays inside the window array: the scan
  /// never passes <c>_strStart + _lookahead + 7 &lt;= 2 * WSize + 7</c>, candidates lie before
  /// the scan, and the array carries <c>MaxMatch + 8</c> bytes of slack past the window.
  /// </remarks>
  [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
  private static int LongestMatch(ref byte window, ref ushort prev, int scan, int current, int best, int chain, int nice, int maxLength, ref int matchStart) {
    var limit = scan > MaxDist ? scan - MaxDist : 0;
    var lookahead = maxLength;
    ref var scanRef = ref Unsafe.Add(ref window, scan);
    var scanStart = Unsafe.ReadUnaligned<ushort>(ref scanRef);
    var scanEnd = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref scanRef, best - 1));
    // The window seen from the last two bytes of the best match so far, so a candidate's
    // ending bytes are one indexed load away.
    ref var windowAtEnd = ref Unsafe.Add(ref window, best - 1);
    do {
      // Quick rejects: the two bytes ending at the best length so far, then the first two.
      if (Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref windowAtEnd, (nuint)(uint)current)) != scanEnd
          || Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref window, (nuint)(uint)current)) != scanStart)
        continue;

      var length = MatchLength(ref Unsafe.Add(ref window, (nuint)(uint)current), ref scanRef, maxLength);
      if (length > best) {
        matchStart = current;
        best = length;
        if (length >= nice)
          break;
        scanEnd = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref scanRef, best - 1));
        windowAtEnd = ref Unsafe.Add(ref window, best - 1);
      }
    } while ((current = Unsafe.Add(ref prev, (nuint)(uint)(current & WMask))) > limit && --chain != 0);

    return Math.Min(best, lookahead);
  }

  /// <summary>Length of the common prefix of two strings whose first two bytes agree, capped at <paramref name="maxLength"/>.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int MatchLength(ref byte candidate, ref byte scan, int maxLength) {
    var length = 2;
    while (true) {
      var diff = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref candidate, length))
                 ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref scan, length));
      if (diff != 0) {
        // The first differing byte is the lowest one in memory order.
        length += (BitConverter.IsLittleEndian ? BitOperations.TrailingZeroCount(diff) : BitOperations.LeadingZeroCount(diff)) >> 3;
        return Math.Min(length, maxLength);
      }

      length += 8;
      if (length >= maxLength)
        return maxLength;
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Load32(ref byte window, int index) {
    var value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref window, index));
    return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
  }

  private void Slide() {
    Buffer.BlockCopy(this._window, WSize, this._window, 0, WSize);
    this._matchStart -= WSize;
    this._strStart -= WSize;
    this._blockStart -= WSize;

    SlideTable(this._head);
    SlideTable(this._prev);
    SlideTable(this._head3);
  }

  /// <summary>
  /// Moves every position in <paramref name="table"/> down by the slide distance, clamping
  /// positions that fall out of the window to 0 (no candidate).
  /// </summary>
  /// <remarks>
  /// <c>max(v, WSize) - WSize</c> is exactly that clamp for unsigned values, so whole vectors
  /// go at once; every table is a multiple of every vector width in length.
  /// </remarks>
  private static void SlideTable(ushort[] table) {
    var window = new Vector<ushort>(WSize);
    var span = MemoryMarshal.Cast<ushort, Vector<ushort>>(table.AsSpan());
    for (var i = 0; i < span.Length; ++i)
      span[i] = Vector.Max(span[i], window) - window;
  }

  // ── symbols ──────────────────────────────────────────────────────────

  /// <summary>The block's symbol buffer and frequency counts, as references the hot loops keep in registers.</summary>
  private ref struct SymbolSink {
    private readonly ref ushort _litLen;
    private readonly ref ushort _distance;
    private readonly ref long _litFreq;
    private readonly ref long _distFreq;
    private int _count;

    public SymbolSink(DeflateEncoder encoder) {
      this._litLen = ref MemoryMarshal.GetArrayDataReference(encoder._symLitLen);
      this._distance = ref MemoryMarshal.GetArrayDataReference(encoder._symDistance);
      this._litFreq = ref MemoryMarshal.GetArrayDataReference(encoder._litFreq);
      this._distFreq = ref MemoryMarshal.GetArrayDataReference(encoder._distFreq);
      this._count = encoder._symCount;
    }

    public readonly bool IsFull => this._count == SymbolBufferSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Literal(byte value) {
      Unsafe.Add(ref this._litLen, this._count) = value;
      Unsafe.Add(ref this._distance, this._count++) = 0;
      ++Unsafe.Add(ref this._litFreq, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Match(int distance, int length) {
      Unsafe.Add(ref this._litLen, this._count) = (ushort)(256 + length - MinMatch);
      Unsafe.Add(ref this._distance, this._count++) = (ushort)distance;
      ++Unsafe.Add(ref this._litFreq, 257 + LengthCodes[length - MinMatch]);
      ++Unsafe.Add(ref this._distFreq, DistanceCodes[DistanceSlot(distance)]);
    }

    /// <summary>Emits the buffered symbols as a block ending at the encoder's <c>_strStart</c>, and starts over.</summary>
    public void Flush(DeflateEncoder encoder) {
      encoder._symCount = this._count;
      encoder.FlushBlock(last: false);
      this._count = 0;
    }

    public readonly void Store(DeflateEncoder encoder) => encoder._symCount = this._count;
  }

  /// <summary>Index of <paramref name="distance"/> in <see cref="DistanceCodes"/>; 0 for no distance.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int DistanceSlot(int distance) => distance <= 256 ? distance : 256 + ((distance - 1) >> 7);

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

  /// <summary>Writes the buffered symbols and the end-of-block code with the given codes.</summary>
  /// <remarks>
  /// <para>Every code is first folded into a table entry: a literal's code, or a length's code
  /// together with its extra bits, in one; a distance code with its length, extra-bit count and
  /// base in another. A literal looks up the distance entry of distance 0, which writes
  /// nothing, so literals and matches take the same branch-free path.</para>
  /// <para>The bit accumulator is spilled after every symbol by an unconditional eight-byte
  /// store, of which only the whole bytes count: at most 7 bits stay behind, a symbol adds at
  /// most 15 + 5 + 15 + 13 = 48, so the accumulator never overflows, and keeping eight bytes
  /// of room in the output buffer is the only check per symbol.</para>
  /// </remarks>
  [MethodImpl(MethodImplOptions.AggressiveOptimization)]
  private void EmitSymbols(int[] litLengths, ushort[] litCodes, int[] distLengths, ushort[] distCodes) {
    var litLen = this._litLenEntries;
    for (var symbol = 0; symbol < 256; ++symbol)
      litLen[symbol] = litCodes[symbol] | (ulong)litLengths[symbol] << 32;

    var lengthBase = DeflateConstants.LengthBase;
    var lengthExtra = DeflateConstants.LengthExtraBits;
    for (var value = 0; value <= MaxMatch - MinMatch; ++value) {
      var code = LengthCodes[value];
      var count = litLengths[257 + code];
      litLen[256 + value] = litCodes[257 + code] | (ulong)(value + MinMatch - lengthBase[code]) << count | (ulong)(count + lengthExtra[code]) << 32;
    }

    var distances = this._distEntries;
    var distBase = DeflateConstants.DistanceBase;
    var distExtra = DeflateConstants.DistanceExtraBits;
    for (var code = 0; code < DeflateConstants.DistanceAlphabetSize; ++code)
      distances[code] = distCodes[code] | (ulong)distLengths[code] << 32 | (ulong)distExtra[code] << 40 | (ulong)distBase[code] << 48;

    ref var litLenRef = ref MemoryMarshal.GetArrayDataReference(litLen);
    ref var distRef = ref MemoryMarshal.GetArrayDataReference(distances);
    ref var slots = ref MemoryMarshal.GetArrayDataReference(DistanceCodes);
    ref var symLitLen = ref MemoryMarshal.GetArrayDataReference(this._symLitLen);
    ref var symDistance = ref MemoryMarshal.GetArrayDataReference(this._symDistance);
    var output = this._out;
    var room = output.Length - 8;

    // The header may have left up to 31 bits; bring that down to the at most 7 the loop expects.
    if (this._outPos > room)
      this.FlushOutput();
    var bitBuf = this._bitBuf;
    var bitCount = this._bitCount;
    var outPos = this._outPos;
    Spill(output, ref outPos, ref bitBuf, ref bitCount);

    var symbols = this._symCount;
    for (var i = 0; i < symbols; ++i) {
      if (outPos > room) {
        this._outPos = outPos;
        this.FlushOutput();
        outPos = 0;
      }

      var entry = Unsafe.Add(ref litLenRef, Unsafe.Add(ref symLitLen, i));
      int distance = Unsafe.Add(ref symDistance, i);
      var distEntry = Unsafe.Add(ref distRef, Unsafe.Add(ref slots, DistanceSlot(distance)));
      var bits = (ulong)(uint)entry;
      var count = (int)(byte)(entry >> 32);
      bits |= (distEntry & 0xFFFF) << count;
      count += (byte)(distEntry >> 32);
      bits |= (ulong)(distance - (int)(distEntry >> 48)) << count;
      count += (byte)(distEntry >> 40);

      bitBuf |= bits << bitCount;
      bitCount += count;
      Spill(output, ref outPos, ref bitBuf, ref bitCount);
    }

    if (outPos > room) {
      this._outPos = outPos;
      this.FlushOutput();
      outPos = 0;
    }

    bitBuf |= (ulong)litCodes[DeflateConstants.EndOfBlock] << bitCount;
    bitCount += litLengths[DeflateConstants.EndOfBlock];
    Spill(output, ref outPos, ref bitBuf, ref bitCount);

    this._bitBuf = bitBuf;
    this._bitCount = bitCount;
    this._outPos = outPos;
  }

  /// <summary>Stores the accumulator's whole bytes; needs eight bytes of room at <paramref name="outPos"/>.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void Spill(byte[] output, ref int outPos, ref ulong bitBuf, ref int bitCount) {
    Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(output), outPos),
      BitConverter.IsLittleEndian ? bitBuf : BinaryPrimitives.ReverseEndianness(bitBuf));
    var bytes = bitCount >> 3;
    outPos += bytes;
    bitBuf >>= bytes << 3;
    bitCount &= 7;
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
  // code above 15 spans a multiple of 128 distances. Slot 0 — a literal's distance — maps to a
  // code past the alphabet, whose emit entry writes nothing.
  private static byte[] BuildDistanceCodes() {
    var table = new byte[512];
    table[0] = DeflateConstants.DistanceAlphabetSize;
    for (var d = 1; d <= 256; ++d)
      table[d] = (byte)DeflateConstants.GetDistanceCode(d);
    for (var i = 2; i < 256; ++i)
      table[256 + i] = (byte)DeflateConstants.GetDistanceCode((i << 7) + 1);
    return table;
  }
}
