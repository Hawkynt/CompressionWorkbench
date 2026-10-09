using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Compression.Core.Deflate;

/// <summary>
/// Decompresses data in the DEFLATE format (RFC 1951).
/// </summary>
/// <remarks>
/// <para>The decoder works on a 64-bit bit accumulator refilled from a read-ahead buffer, and
/// decodes Huffman symbols through two-level lookup tables (a root table indexed by the next
/// few bits, with sub-tables for the rarer longer codes), writing straight into an output
/// buffer that doubles as the 32 KB history window. A table entry carries the base length or
/// distance and its extra-bit count along with the code, so a symbol costs one lookup.</para>
/// <para>Two loops share the work. Wherever the read-ahead and the output buffer both have
/// room for the largest step, a fast loop decodes without bounds or truncation checks — up to
/// three literals per refill, matches copied sixteen bytes at a time; at the edges a careful
/// loop decodes one symbol at a time with every check, and hands back as soon as the fast
/// loop can resume. Malformed input is rejected by both alike.</para>
/// <para>Input is read ahead in chunks. When the stream ends and the underlying stream is
/// seekable, the read-ahead is handed back by seeking, so the stream is left positioned on
/// the first byte after the DEFLATE data; otherwise <see cref="UnconsumedBytes"/> reports how
/// much was read ahead and <see cref="ReadRemainder"/> returns those bytes first — container
/// formats use either to reach their trailer.</para>
/// </remarks>
public sealed class DeflateDecompressor {
  private const int WindowSize = DeflateConstants.WindowSize;
  private const int MaxMatch = 258;
  private const int InputChunk = 16384;
  private const int StreamingBufferSize = WindowSize * 4;
  private const int LitRootBits = 11;
  private const int LitRootMask = (1 << LitRootBits) - 1;
  private const int DistRootBits = 8;
  private const int DistRootMask = (1 << DistRootBits) - 1;
  private const int CodeLengthRootBits = 7;

  // Room a match copy needs past the write position: the match, plus the overshoot of its
  // last sixteen-byte step.
  private const int CopySlack = MaxMatch + 16;
  // The fast loop writes up to three literals or one match per step.
  private const int FastOutputSlack = CopySlack + 16;
  // The fast loop reads eight bytes per refill and refills once per step.
  private const int FastInputSlack = 16;

  // Decode-table entry flags; see BuildTable.
  private const uint Literal = 0x8000;
  private const uint Exceptional = 0x4000;
  private const uint SubTable = 0x2000;
  private const uint EndOfBlock = 0x1000;

  private static readonly uint[] StaticLitTable = BuildTable(DeflateConstants.GetStaticLiteralLengths(), LitRootBits, TableKind.LiteralLength, null);
  private static readonly uint[] StaticDistTable = BuildTable(DeflateConstants.GetStaticDistanceLengths(), DistRootBits, TableKind.Distance, null);

  private readonly Stream? _input;
  private byte[] _in;
  private int _inPos;
  private int _inEnd;
  private bool _inEof;

  private ulong _bitBuf;
  private int _bitCnt;

  private byte[] _buf;
  private int _rpos;
  private int _wpos;

  private State _state;
  private bool _final;
  private int _storedLeft;
  private uint[] _litTable = StaticLitTable;
  private uint[] _distTable = StaticDistTable;
  private uint[]? _dynamicLitTable;
  private uint[]? _dynamicDistTable;

  private enum State { Header, Stored, Huffman, Done }

  /// <summary>
  /// Initializes a new <see cref="DeflateDecompressor"/> for streaming decompression.
  /// </summary>
  /// <param name="input">The stream containing DEFLATE compressed data.</param>
  public DeflateDecompressor(Stream input) {
    this._input = input ?? throw new ArgumentNullException(nameof(input));
    this._in = new byte[InputChunk];
    this._buf = [];
  }

  private DeflateDecompressor(byte[] compressed, int outputCapacity) {
    this._in = compressed;
    this._inEnd = compressed.Length;
    this._inEof = true;
    this._buf = new byte[outputCapacity];
  }

  /// <summary>
  /// Gets the number of whole bytes read from the underlying stream but not consumed by the
  /// DEFLATE data. Once the stream has ended on a seekable input this is zero, because the
  /// read-ahead has already been handed back by seeking.
  /// </summary>
  public int UnconsumedBytes => (this._bitCnt >> 3) + (this._inEnd - this._inPos);

  /// <summary>Gets a value indicating whether the final block has been decoded.</summary>
  public bool IsFinished => this._state == State.Done;

  /// <summary>
  /// Decompresses all data from the stream.
  /// </summary>
  /// <returns>The decompressed data.</returns>
  public byte[] DecompressAll() {
    while (this._state != State.Done) {
      if (this._buf.Length - this._wpos <= CopySlack)
        this.Grow();

      this.Step();
    }

    return this._buf.AsSpan(this._rpos, this._wpos - this._rpos).ToArray();
  }

  /// <summary>
  /// Decompresses DEFLATE data in one shot.
  /// </summary>
  /// <param name="compressedData">The DEFLATE compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressedData) {
    var capacity = (int)Math.Clamp(compressedData.Length * 4L, 4096, Array.MaxLength);
    return new DeflateDecompressor(compressedData.ToArray(), capacity).DecompressAll();
  }

  /// <summary>
  /// Decompresses data from the input stream into the provided buffer.
  /// Returns the number of bytes written. Returns 0 when decompression is complete.
  /// </summary>
  /// <param name="output">Buffer to write decompressed data into.</param>
  /// <param name="offset">Offset in the output buffer.</param>
  /// <param name="count">Maximum number of bytes to write.</param>
  /// <returns>Number of bytes written, or 0 if decompression is complete.</returns>
  public int Decompress(byte[] output, int offset, int count) => this.Decompress(output.AsSpan(offset, count));

  /// <summary>
  /// Decompresses data into <paramref name="output"/>, filling it unless the DEFLATE stream ends first.
  /// </summary>
  /// <param name="output">Buffer to write decompressed data into.</param>
  /// <returns>Number of bytes written, or 0 if decompression is complete.</returns>
  public int Decompress(Span<byte> output) {
    var total = 0;
    while (!output.IsEmpty) {
      var available = this._wpos - this._rpos;
      if (available > 0) {
        var n = Math.Min(available, output.Length);
        this._buf.AsSpan(this._rpos, n).CopyTo(output);
        this._rpos += n;
        total += n;
        output = output[n..];
        continue;
      }

      if (this._state == State.Done)
        break;

      this.MakeRoom();
      this.Step();
    }

    return total;
  }

  /// <summary>
  /// Reads bytes that follow the end of the DEFLATE data — a container's trailer, or the next
  /// member — taking the decompressor's read-ahead first and then the underlying stream.
  /// </summary>
  /// <param name="destination">Where to put the bytes.</param>
  /// <returns>The number of bytes read; less than requested only at the end of the input.</returns>
  /// <exception cref="InvalidOperationException">The DEFLATE data has not ended yet.</exception>
  public int ReadRemainder(Span<byte> destination) {
    if (this._state != State.Done)
      throw new InvalidOperationException("The DEFLATE stream has not ended yet.");

    var total = 0;
    var buffered = Math.Min(this._inEnd - this._inPos, destination.Length);
    if (buffered > 0) {
      this._in.AsSpan(this._inPos, buffered).CopyTo(destination);
      this._inPos += buffered;
      total = buffered;
    }

    if (this._input is null)
      return total;

    while (total < destination.Length) {
      var n = this._input.Read(destination[total..]);
      if (n <= 0)
        break;
      total += n;
    }

    return total;
  }

  private void Grow() {
    var size = (int)Math.Min(Math.Max(this._buf.Length * 2L, StreamingBufferSize), Array.MaxLength);
    if (size - this._wpos <= CopySlack)
      throw new InvalidDataException("DEFLATE output exceeds the largest supported buffer.");

    Array.Resize(ref this._buf, size);
  }

  // Called only once everything decoded so far has been handed out (_rpos == _wpos).
  private void MakeRoom() {
    if (this._buf.Length == 0) {
      this._buf = new byte[StreamingBufferSize];
      return;
    }

    if (this._buf.Length - this._wpos > CopySlack)
      return;

    var keep = Math.Min(this._wpos, WindowSize);
    var shift = this._wpos - keep;
    Buffer.BlockCopy(this._buf, shift, this._buf, 0, keep);
    this._wpos = keep;
    this._rpos -= shift;
  }

  private void Step() {
    switch (this._state) {
      case State.Header: this.ReadBlockHeader(); break;
      case State.Stored: this.CopyStored(); break;
      case State.Huffman: this.DecodeHuffman(); break;
    }
  }

  // ── bit input ────────────────────────────────────────────────────────

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void Refill() {
    if (this._inEnd - this._inPos >= 8) {
      // Load eight bytes at once; the bits above the accumulator's count are the very bytes
      // that a later refill would OR in again, so loading them early is harmless.
      this._bitBuf |= BinaryPrimitives.ReadUInt64LittleEndian(this._in.AsSpan(this._inPos)) << this._bitCnt;
      var n = (63 - this._bitCnt) >> 3;
      this._inPos += n;
      this._bitCnt += n << 3;
    } else
      this.RefillSlow();
  }

  private void RefillSlow() {
    while (this._bitCnt <= 56) {
      if (this._inPos == this._inEnd && !this.FillInput())
        return;

      this._bitBuf |= (ulong)this._in[this._inPos++] << this._bitCnt;
      this._bitCnt += 8;
    }
  }

  private bool FillInput() {
    if (this._inEof || this._input is null)
      return false;

    var n = this._input.Read(this._in, 0, this._in.Length);
    if (n <= 0) {
      this._inEof = true;
      return false;
    }

    this._inPos = 0;
    this._inEnd = n;
    return true;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private uint GetBits(int count) {
    if (this._bitCnt < count) {
      this.RefillSlow();
      if (this._bitCnt < count)
        ThrowTruncated();
    }

    var value = (uint)this._bitBuf & ((1u << count) - 1);
    this._bitBuf >>= count;
    this._bitCnt -= count;
    return value;
  }

  /// <summary>Decodes one symbol of a code-length table, whose codes never need a sub-table.</summary>
  private int DecodeSymbol(uint[] table, int rootBits) {
    var entry = table[(int)this._bitBuf & ((1 << rootBits) - 1)];
    var length = (int)(entry & 0xFF);
    if (length == 0)
      ThrowInvalidCode();
    if (length > this._bitCnt)
      ThrowTruncated();

    this._bitBuf >>= length;
    this._bitCnt -= length;
    return (int)(entry >> 16);
  }

  // ── blocks ───────────────────────────────────────────────────────────

  private void ReadBlockHeader() {
    if (this._bitCnt < 3)
      this.RefillSlow();

    // A stream that simply ends where a block header should be is taken as finished.
    if (this._bitCnt < 3) {
      this.Finish();
      return;
    }

    this._final = this.GetBits(1) == 1;
    switch (this.GetBits(2)) {
      case DeflateConstants.BlockTypeUncompressed: {
        var drop = this._bitCnt & 7;
        this._bitBuf >>= drop;
        this._bitCnt -= drop;

        var len = this.GetBits(16);
        var nlen = this.GetBits(16);
        if ((len ^ nlen) != 0xFFFF)
          throw new InvalidDataException("Invalid uncompressed block: LEN/NLEN mismatch.");

        this._storedLeft = (int)len;
        this._state = State.Stored;
        break;
      }
      case DeflateConstants.BlockTypeStaticHuffman:
        this._litTable = StaticLitTable;
        this._distTable = StaticDistTable;
        this._state = State.Huffman;
        break;
      case DeflateConstants.BlockTypeDynamicHuffman:
        this.ReadDynamicTables();
        this._state = State.Huffman;
        break;
      default:
        throw new InvalidDataException("Invalid DEFLATE block type: 3");
    }
  }

  private void EndBlock() {
    if (this._final)
      this.Finish();
    else
      this._state = State.Header;
  }

  private void Finish() {
    this._state = State.Done;

    // Hand back whole bytes still sitting in the accumulator, then the read-ahead.
    var drop = this._bitCnt & 7;
    this._bitBuf >>= drop;
    this._bitCnt -= drop;

    var held = this._bitCnt >> 3;
    if (held > 0) {
      if (this._inPos >= held)
        this._inPos -= held;
      else {
        var rest = this._inEnd - this._inPos;
        var merged = new byte[held + rest];
        for (var i = 0; i < held; ++i)
          merged[i] = (byte)(this._bitBuf >> (i * 8));
        this._in.AsSpan(this._inPos, rest).CopyTo(merged.AsSpan(held));
        this._in = merged;
        this._inPos = 0;
        this._inEnd = merged.Length;
      }
    }

    this._bitBuf = 0;
    this._bitCnt = 0;

    var unread = this._inEnd - this._inPos;
    if (unread <= 0 || this._input is not { CanSeek: true })
      return;

    try {
      this._input.Seek(-unread, SeekOrigin.Current);
      this._inPos = this._inEnd;
    } catch (NotSupportedException) {
      // Leave the read-ahead buffered; ReadRemainder still serves it.
    }
  }

  private void CopyStored() {
    while (this._storedLeft > 0) {
      var room = this._buf.Length - this._wpos;
      if (room == 0)
        return;

      // After byte alignment the accumulator only holds whole bytes; drain those first.
      if (this._bitCnt > 0) {
        this._buf[this._wpos++] = (byte)this._bitBuf;
        this._bitBuf >>= 8;
        this._bitCnt -= 8;
        --this._storedLeft;
        continue;
      }

      // Bytes are about to be taken from the read-ahead directly, so the copies of them the
      // accumulator may hold above its count must go.
      this._bitBuf = 0;
      if (this._inPos == this._inEnd && !this.FillInput())
        ThrowTruncated();

      var n = Math.Min(Math.Min(this._storedLeft, room), this._inEnd - this._inPos);
      this._in.AsSpan(this._inPos, n).CopyTo(this._buf.AsSpan(this._wpos));
      this._inPos += n;
      this._wpos += n;
      this._storedLeft -= n;
    }

    this.EndBlock();
  }

  private void DecodeHuffman() {
    // Alternate between the two loops: the fast one wherever it is safe, the careful one at
    // the edges. Each call of either makes progress or hands back for more room.
    while (true) {
      if (this._wpos < this._buf.Length - FastOutputSlack && this._inEnd - this._inPos >= 2 * FastInputSlack && this.DecodeFast())
        return;

      if (!this.DecodeCareful())
        return;
    }
  }

  /// <summary>
  /// Decodes symbols while the output buffer and the read-ahead both have room for the
  /// largest step, without any per-symbol bounds or truncation check.
  /// </summary>
  /// <remarks>
  /// <para>Inside the safe zone every refill can load eight whole bytes, so the accumulator
  /// holds at least 56 bits after each one: enough for three literals (3 × 15 bits), or for a
  /// length and its distance (15 + 5 + 15 + 13 = 48 bits). The next table entry is looked up
  /// before a match is copied, so the lookup overlaps the copy.</para>
  /// <para>Returns <see langword="true"/> when the block ended; otherwise the careful loop
  /// takes over at the edge of the zone.</para>
  /// </remarks>
  [MethodImpl(MethodImplOptions.AggressiveOptimization)]
  private bool DecodeFast() {
    var buf = this._buf;
    var stopAt = buf.Length - FastOutputSlack;
    var inEnd = this._inEnd - FastInputSlack;
    var lit = this._litTable;
    var dist = this._distTable;
    ref var litRef = ref MemoryMarshal.GetArrayDataReference(lit);
    ref var distRef = ref MemoryMarshal.GetArrayDataReference(dist);
    ref var input = ref MemoryMarshal.GetArrayDataReference(this._in);
    ref var output = ref MemoryMarshal.GetArrayDataReference(buf);
    var wpos = this._wpos;
    var bitBuf = this._bitBuf;
    var bitCnt = this._bitCnt;
    var inPos = this._inPos;

    Refill(ref input, ref inPos, ref bitBuf, ref bitCnt);
    var entry = Unsafe.Add(ref litRef, (int)bitBuf & LitRootMask);
    while (wpos < stopAt && inPos <= inEnd) {
      if ((entry & Literal) != 0) {
        bitBuf >>= (int)entry;
        bitCnt -= (int)(entry & 0xFF);
        Unsafe.Add(ref output, wpos++) = (byte)(entry >> 16);
        entry = Unsafe.Add(ref litRef, (int)bitBuf & LitRootMask);
        if ((entry & Literal) != 0) {
          bitBuf >>= (int)entry;
          bitCnt -= (int)(entry & 0xFF);
          Unsafe.Add(ref output, wpos++) = (byte)(entry >> 16);
          entry = Unsafe.Add(ref litRef, (int)bitBuf & LitRootMask);
          if ((entry & Literal) != 0) {
            bitBuf >>= (int)entry;
            bitCnt -= (int)(entry & 0xFF);
            Unsafe.Add(ref output, wpos++) = (byte)(entry >> 16);
            entry = Unsafe.Add(ref litRef, (int)bitBuf & LitRootMask);
          }
        }

        // The lookup above only needed root bits that are already in place; the refill
        // makes room for whatever the entry turns out to be.
        Refill(ref input, ref inPos, ref bitBuf, ref bitCnt);
        continue;
      }

      if ((entry & Exceptional) != 0) {
        if ((entry & SubTable) != 0) {
          entry = Unsafe.Add(ref litRef, (int)(entry >> 16) + ((int)(bitBuf >> LitRootBits) & ((1 << (int)((entry >> 8) & 0xF)) - 1)));
          if ((entry & Literal) != 0) {
            bitBuf >>= (int)entry;
            bitCnt -= (int)(entry & 0xFF);
            Unsafe.Add(ref output, wpos++) = (byte)(entry >> 16);
            Refill(ref input, ref inPos, ref bitBuf, ref bitCnt);
            entry = Unsafe.Add(ref litRef, (int)bitBuf & LitRootMask);
            continue;
          }
        }

        if ((entry & Exceptional) != 0) {
          if ((entry & EndOfBlock) == 0)
            ThrowInvalidCode();

          bitBuf >>= (int)entry;
          bitCnt -= (int)(entry & 0xFF);
          (this._bitBuf, this._bitCnt, this._inPos, this._wpos) = (bitBuf, bitCnt, inPos, wpos);
          this.EndBlock();
          return true;
        }
      }

      // A length, then its distance; at least 56 bits are in the accumulator.
      var saved = bitBuf;
      bitBuf >>= (int)entry;
      bitCnt -= (int)(entry & 0xFF);
      var length = (int)(entry >> 16) + ExtraBits(saved, entry);

      entry = Unsafe.Add(ref distRef, (int)bitBuf & DistRootMask);
      if ((entry & Exceptional) != 0) {
        if ((entry & SubTable) == 0)
          ThrowInvalidCode();
        entry = Unsafe.Add(ref distRef, (int)(entry >> 16) + ((int)(bitBuf >> DistRootBits) & ((1 << (int)((entry >> 8) & 0xF)) - 1)));
        if ((entry & Exceptional) != 0)
          ThrowInvalidCode();
      }

      saved = bitBuf;
      bitBuf >>= (int)entry;
      bitCnt -= (int)(entry & 0xFF);
      var distance = (int)(entry >> 16) + ExtraBits(saved, entry);
      if (distance > wpos)
        ThrowDistanceTooFar(distance, wpos);

      Refill(ref input, ref inPos, ref bitBuf, ref bitCnt);
      entry = Unsafe.Add(ref litRef, (int)bitBuf & LitRootMask);
      CopyMatch(ref Unsafe.Add(ref output, wpos), distance, length);
      wpos += length;
    }

    (this._bitBuf, this._bitCnt, this._inPos, this._wpos) = (bitBuf, bitCnt, inPos, wpos);
    return false;
  }

  /// <summary>
  /// Decodes one symbol at a time with every check in place: near the end of the input,
  /// where a refill may come up short, and near the end of the output buffer.
  /// </summary>
  /// <returns><see langword="true"/> when the fast loop can take over again.</returns>
  [MethodImpl(MethodImplOptions.AggressiveOptimization)]
  private bool DecodeCareful() {
    var buf = this._buf;
    // Copies run in whole steps of up to sixteen bytes, so up to fifteen bytes past a match
    // may be written; stopping MaxMatch + 16 short of the end keeps that inside the buffer.
    var stopAt = buf.Length - CopySlack;
    var fastStop = buf.Length - FastOutputSlack;
    var lit = this._litTable;
    var dist = this._distTable;
    ref var output = ref MemoryMarshal.GetArrayDataReference(buf);
    var wpos = this._wpos;
    var bitBuf = this._bitBuf;
    var bitCnt = this._bitCnt;
    var inPos = this._inPos;

    while (wpos < stopAt) {
      // 15 (length code) + 5 (extra) + 15 (distance code) + 13 (extra) = 48 bits per token.
      if (bitCnt < 48) {
        if (this._inEnd - inPos >= 8)
          Refill(ref MemoryMarshal.GetArrayDataReference(this._in), ref inPos, ref bitBuf, ref bitCnt);
        else {
          (this._bitBuf, this._bitCnt, this._inPos) = (bitBuf, bitCnt, inPos);
          this.RefillSlow();
          (bitBuf, bitCnt, inPos) = (this._bitBuf, this._bitCnt, this._inPos);
        }
      }

      var entry = lit[(int)bitBuf & LitRootMask];
      if ((entry & SubTable) != 0)
        entry = lit[(int)(entry >> 16) + ((int)(bitBuf >> LitRootBits) & ((1 << (int)((entry >> 8) & 0xF)) - 1))];

      if ((entry & (Exceptional | EndOfBlock)) == Exceptional)
        ThrowInvalidCode();
      if ((int)(entry & 0xFF) > bitCnt)
        ThrowTruncated();

      var saved = bitBuf;
      bitBuf >>= (int)entry;
      bitCnt -= (int)(entry & 0xFF);

      if ((entry & Literal) != 0) {
        Unsafe.Add(ref output, wpos++) = (byte)(entry >> 16);
        if (wpos < fastStop && this._inEnd - inPos >= 2 * FastInputSlack) {
          (this._bitBuf, this._bitCnt, this._inPos, this._wpos) = (bitBuf, bitCnt, inPos, wpos);
          return true;
        }

        continue;
      }

      if ((entry & EndOfBlock) != 0) {
        (this._bitBuf, this._bitCnt, this._inPos, this._wpos) = (bitBuf, bitCnt, inPos, wpos);
        this.EndBlock();
        return false;
      }

      var length = (int)(entry >> 16) + ExtraBits(saved, entry);

      entry = dist[(int)bitBuf & DistRootMask];
      if ((entry & SubTable) != 0)
        entry = dist[(int)(entry >> 16) + ((int)(bitBuf >> DistRootBits) & ((1 << (int)((entry >> 8) & 0xF)) - 1))];
      if ((entry & Exceptional) != 0)
        ThrowInvalidCode();
      if ((int)(entry & 0xFF) > bitCnt)
        ThrowTruncated();

      saved = bitBuf;
      bitBuf >>= (int)entry;
      bitCnt -= (int)(entry & 0xFF);
      var distance = (int)(entry >> 16) + ExtraBits(saved, entry);
      if (distance > wpos)
        ThrowDistanceTooFar(distance, wpos);

      CopyMatch(ref Unsafe.Add(ref output, wpos), distance, length);
      wpos += length;
      if (wpos < fastStop && this._inEnd - inPos >= 2 * FastInputSlack) {
        (this._bitBuf, this._bitCnt, this._inPos, this._wpos) = (bitBuf, bitCnt, inPos, wpos);
        return true;
      }
    }

    (this._bitBuf, this._bitCnt, this._inPos, this._wpos) = (bitBuf, bitCnt, inPos, wpos);
    return false;
  }

  /// <summary>
  /// Tops the accumulator up to at least 56 bits from eight bytes at <paramref name="inPos"/>,
  /// which the caller guarantees are there.
  /// </summary>
  /// <remarks>
  /// All eight bytes are ORed in but only the whole ones that fit are counted; the bits loaded
  /// above the count are the very bits a later refill ORs in again, so loading them early is
  /// harmless.
  /// </remarks>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void Refill(ref byte input, ref int inPos, ref ulong bitBuf, ref int bitCnt) {
    var word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, inPos));
    bitBuf |= (BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word)) << bitCnt;
    inPos += (63 - bitCnt) >> 3;
    bitCnt |= 56;
  }

  /// <summary>The extra bits that follow the code of <paramref name="entry"/> in <paramref name="saved"/>, the accumulator before the code was consumed.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int ExtraBits(ulong saved, uint entry)
    => (int)((saved & ~(ulong.MaxValue << (int)(entry & 0xFF))) >> (int)((entry >> 8) & 0xF));

  /// <summary>Copies <paramref name="length"/> bytes from <paramref name="distance"/> back to <paramref name="destination"/>.</summary>
  /// <remarks>
  /// Copies run in whole 16- or 8-byte steps and may write up to fifteen bytes past the match,
  /// which the callers' slack keeps inside the buffer. Each step reads only bytes at least a
  /// step behind it, all already final; shorter distances repeat a pattern and go byte by byte.
  /// </remarks>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void CopyMatch(ref byte destination, int distance, int length) {
    ref var source = ref Unsafe.Subtract(ref destination, distance);
    if (distance >= 16) {
      var i = 0;
      do {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i), Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref source, i)));
        i += 16;
      } while (i < length);
    } else if (distance >= 8) {
      var i = 0;
      do {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, i)));
        i += 8;
      } while (i < length);
    } else if (distance == 1)
      Unsafe.InitBlockUnaligned(ref destination, source, (uint)length);
    else
      for (var i = 0; i < length; ++i)
        Unsafe.Add(ref destination, i) = Unsafe.Add(ref source, i);
  }

  private void ReadDynamicTables() {
    var hlit = (int)this.GetBits(5) + 257;
    var hdist = (int)this.GetBits(5) + 1;
    var hclen = (int)this.GetBits(4) + 4;
    if (hlit > 286 || hdist > 30)
      throw new InvalidDataException($"Invalid DEFLATE dynamic header: HLIT={hlit}, HDIST={hdist}.");

    Span<int> codeLengthLengths = stackalloc int[DeflateConstants.CodeLengthAlphabetSize];
    codeLengthLengths.Clear();
    var order = DeflateConstants.CodeLengthOrder;
    for (var i = 0; i < hclen; ++i)
      codeLengthLengths[order[i]] = (int)this.GetBits(3);

    var codeLengthTable = BuildTable(codeLengthLengths, CodeLengthRootBits, TableKind.Plain, null);

    Span<int> lengths = stackalloc int[hlit + hdist];
    var index = 0;
    while (index < lengths.Length) {
      if (this._bitCnt < 16)
        this.RefillSlow();

      var symbol = this.DecodeSymbol(codeLengthTable, CodeLengthRootBits);
      int repeat, value;
      switch (symbol) {
        case <= 15:
          lengths[index++] = symbol;
          continue;
        case 16:
          if (index == 0)
            throw new InvalidDataException("Code length 16 at start of table.");
          value = lengths[index - 1];
          repeat = 3 + (int)this.GetBits(2);
          break;
        case 17:
          value = 0;
          repeat = 3 + (int)this.GetBits(3);
          break;
        default:
          value = 0;
          repeat = 11 + (int)this.GetBits(7);
          break;
      }

      if (index + repeat > lengths.Length)
        throw new InvalidDataException("DEFLATE code lengths overrun the table.");

      lengths.Slice(index, repeat).Fill(value);
      index += repeat;
    }

    if (lengths[DeflateConstants.EndOfBlock] == 0)
      throw new InvalidDataException("DEFLATE block has no end-of-block code.");

    this._litTable = this._dynamicLitTable = BuildTable(lengths[..hlit], LitRootBits, TableKind.LiteralLength, this._dynamicLitTable);
    this._distTable = this._dynamicDistTable = BuildTable(lengths.Slice(hlit, hdist), DistRootBits, TableKind.Distance, this._dynamicDistTable);
  }

  private enum TableKind { Plain, LiteralLength, Distance }

  /// <summary>
  /// Builds a two-level decoding table for canonical Huffman code lengths (RFC 1951 §3.2.2),
  /// reusing <paramref name="reuse"/> when it is large enough.
  /// </summary>
  /// <remarks>
  /// <para>An entry is <c>value &lt;&lt; 16 | flags | codeLength &lt;&lt; 8 | totalBits</c>,
  /// where totalBits counts the code and the extra bits that follow it, so consuming a symbol
  /// is one shift by the entry itself. The value is the literal, the base length or distance,
  /// or — in a code-length table — the symbol. A literal/length or distance table folds the
  /// RFC's base and extra-bit tables in, which spares the decoder two lookups per symbol.</para>
  /// <para>A link to a sub-table is <c>offset &lt;&lt; 16 | Exceptional | SubTable | subBits &lt;&lt; 8</c>,
  /// the sub-table being indexed by the bits after the root ones. A bit pattern no code
  /// describes, and the symbols the format reserves (286, 287, distance codes 30 and 31),
  /// decode to a bare <c>Exceptional</c>: an incomplete code is accepted, an over-subscribed
  /// one is not.</para>
  /// <para>The flag layout follows libdeflate's decode-table design (MIT licence): fold the
  /// base and the extra-bit count into the entry and let the low byte drive the shift.</para>
  /// </remarks>
  private static uint[] BuildTable(ReadOnlySpan<int> lengths, int rootBits, TableKind kind, uint[]? reuse) {
    Span<int> count = stackalloc int[16];
    count.Clear();
    foreach (var length in lengths) {
      if ((uint)length > 15)
        throw new InvalidDataException($"Invalid DEFLATE code length {length}.");
      ++count[length];
    }

    count[0] = 0;
    var left = 1;
    for (var length = 1; length <= 15; ++length) {
      left = (left << 1) - count[length];
      if (left < 0)
        throw new InvalidDataException("Over-subscribed DEFLATE Huffman code.");
    }

    Span<int> next = stackalloc int[16];
    next.Clear();
    var code = 0;
    for (var length = 1; length <= 15; ++length) {
      code = (code + count[length - 1]) << 1;
      next[length] = code;
    }

    var rootSize = 1 << rootBits;
    var rootMask = rootSize - 1;
    Span<int> reversed = stackalloc int[lengths.Length];
    Span<int> subBits = stackalloc int[rootSize];
    subBits.Clear();
    for (var symbol = 0; symbol < lengths.Length; ++symbol) {
      var length = lengths[symbol];
      if (length == 0)
        continue;

      var rev = (int)DeflateHuffmanTable.ReverseBits((uint)next[length]++, length);
      reversed[symbol] = rev;
      if (length > rootBits)
        subBits[rev & rootMask] = Math.Max(subBits[rev & rootMask], length - rootBits);
    }

    Span<int> subOffset = stackalloc int[rootSize];
    var size = rootSize;
    for (var i = 0; i < rootSize; ++i)
      if (subBits[i] > 0) {
        subOffset[i] = size;
        size += 1 << subBits[i];
      }

    var table = reuse != null && reuse.Length >= size ? reuse : new uint[size];
    table.AsSpan(0, size).Fill(Exceptional);
    for (var i = 0; i < rootSize; ++i)
      if (subBits[i] > 0)
        table[i] = (uint)subOffset[i] << 16 | Exceptional | SubTable | (uint)subBits[i] << 8;

    for (var symbol = 0; symbol < lengths.Length; ++symbol) {
      var length = lengths[symbol];
      if (length == 0)
        continue;

      var rev = reversed[symbol];
      var entry = Entry(kind, symbol, length);
      if (length <= rootBits) {
        for (var i = rev; i < rootSize; i += 1 << length)
          table[i] = entry;
        continue;
      }

      var root = rev & rootMask;
      var bits = subBits[root];
      for (var i = rev >> rootBits; i < 1 << bits; i += 1 << (length - rootBits))
        table[subOffset[root] + i] = entry;
    }

    return table;
  }

  private static uint Entry(TableKind kind, int symbol, int length) {
    var code = (uint)length << 8;
    switch (kind) {
      case TableKind.Plain:
        return (uint)symbol << 16 | code | (uint)length;
      case TableKind.LiteralLength when symbol < 256:
        return (uint)symbol << 16 | Literal | code | (uint)length;
      case TableKind.LiteralLength when symbol == DeflateConstants.EndOfBlock:
        return Exceptional | EndOfBlock | code | (uint)length;
      case TableKind.LiteralLength when symbol - 257 < 29:
        return (uint)DeflateConstants.LengthBase[symbol - 257] << 16 | code | (uint)(length + DeflateConstants.LengthExtraBits[symbol - 257]);
      case TableKind.Distance when symbol < 30:
        return (uint)DeflateConstants.DistanceBase[symbol] << 16 | code | (uint)(length + DeflateConstants.DistanceExtraBits[symbol]);
      default:
        // Reserved symbols: decoding one is an error.
        return Exceptional;
    }
  }

  [DoesNotReturn]
  private static void ThrowDistanceTooFar(int distance, int history)
    => throw new InvalidDataException($"Invalid DEFLATE distance {distance}: only {history} bytes of history.");

  [DoesNotReturn]
  private static void ThrowTruncated() => throw new EndOfStreamException("DEFLATE stream is truncated.");

  [DoesNotReturn]
  private static void ThrowInvalidCode() => throw new InvalidDataException("Invalid DEFLATE Huffman code.");
}
