using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Compression.Core.Deflate;

/// <summary>
/// Decompresses data in the DEFLATE format (RFC 1951).
/// </summary>
/// <remarks>
/// <para>The decoder works on a 64-bit bit accumulator refilled from a read-ahead buffer, and
/// decodes Huffman symbols through two-level lookup tables (a root table indexed by the next
/// few bits, with sub-tables for the rarer longer codes), writing straight into an output
/// buffer that doubles as the 32 KB history window.</para>
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
  private const int LitRootBits = 10;
  private const int DistRootBits = 8;
  private const int CodeLengthRootBits = 7;

  private static readonly int[] StaticLitTable = BuildTable(DeflateConstants.GetStaticLiteralLengths(), LitRootBits);
  private static readonly int[] StaticDistTable = BuildTable(DeflateConstants.GetStaticDistanceLengths(), DistRootBits);

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
  private int[] _litTable = StaticLitTable;
  private int[] _distTable = StaticDistTable;

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
      if (this._buf.Length - this._wpos <= MaxMatch + 8)
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
    if (size - this._wpos <= MaxMatch + 8)
      throw new InvalidDataException("DEFLATE output exceeds the largest supported buffer.");

    Array.Resize(ref this._buf, size);
  }

  // Called only once everything decoded so far has been handed out (_rpos == _wpos).
  private void MakeRoom() {
    if (this._buf.Length == 0) {
      this._buf = new byte[StreamingBufferSize];
      return;
    }

    if (this._buf.Length - this._wpos > MaxMatch + 8)
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

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private int DecodeSymbol(int[] table, int rootBits) {
    var entry = table[(int)this._bitBuf & ((1 << rootBits) - 1)];
    if ((entry & 0x80) != 0)
      entry = table[(entry >> 8) + ((int)(this._bitBuf >> rootBits) & ((1 << (entry & 0x0F)) - 1))];

    var length = entry & 0xFF;
    if (length == 0)
      ThrowInvalidCode();
    if (length > this._bitCnt)
      ThrowTruncated();

    this._bitBuf >>= length;
    this._bitCnt -= length;
    return entry >> 8;
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
    // The hot loop keeps the bit state in locals and writes it back before anything that
    // reads it from the fields.
    var buf = this._buf;
    // Copies run in whole eight-byte steps, so up to seven bytes past a match may be written;
    // stopping MaxMatch + 8 short of the end keeps that inside the buffer.
    var stopAt = buf.Length - MaxMatch - 8;
    var lit = this._litTable;
    var dist = this._distTable;
    ref var lengthBase = ref MemoryMarshal.GetReference(DeflateConstants.LengthBase);
    ref var lengthExtra = ref MemoryMarshal.GetReference(DeflateConstants.LengthExtraBits);
    ref var distBase = ref MemoryMarshal.GetReference(DeflateConstants.DistanceBase);
    ref var distExtra = ref MemoryMarshal.GetReference(DeflateConstants.DistanceExtraBits);
    ref var output = ref MemoryMarshal.GetArrayDataReference(buf);
    var wpos = this._wpos;
    var bitBuf = this._bitBuf;
    var bitCnt = this._bitCnt;
    var inPos = this._inPos;

    while (wpos < stopAt) {
      // 15 (length code) + 5 (extra) + 15 (distance code) + 13 (extra) = 48 bits per token.
      if (bitCnt < 48) {
        if (this._inEnd - inPos >= 8) {
          // The bits loaded above the count are the very bytes a later refill ORs in again.
          bitBuf |= BinaryPrimitives.ReadUInt64LittleEndian(this._in.AsSpan(inPos)) << bitCnt;
          inPos += (63 - bitCnt) >> 3;
          bitCnt |= 56;
        } else {
          (this._bitBuf, this._bitCnt, this._inPos) = (bitBuf, bitCnt, inPos);
          this.RefillSlow();
          (bitBuf, bitCnt, inPos) = (this._bitBuf, this._bitCnt, this._inPos);
        }
      }

      var entry = lit[(int)bitBuf & ((1 << LitRootBits) - 1)];
      if ((entry & 0x80) != 0)
        entry = lit[(entry >> 8) + ((int)(bitBuf >> LitRootBits) & ((1 << (entry & 0x0F)) - 1))];

      var codeLength = entry & 0xFF;
      if (codeLength == 0)
        ThrowInvalidCode();
      if (codeLength > bitCnt)
        ThrowTruncated();

      bitBuf >>= codeLength;
      bitCnt -= codeLength;
      var symbol = entry >> 8;

      if (symbol < 256) {
        Unsafe.Add(ref output, wpos++) = (byte)symbol;
        continue;
      }

      if (symbol == DeflateConstants.EndOfBlock) {
        (this._bitBuf, this._bitCnt, this._inPos, this._wpos) = (bitBuf, bitCnt, inPos, wpos);
        this.EndBlock();
        return;
      }

      symbol -= 257;
      if ((uint)symbol >= 29)
        ThrowInvalidCode();

      var length = Unsafe.Add(ref lengthBase, symbol);
      var extra = Unsafe.Add(ref lengthExtra, symbol);
      if (extra > bitCnt)
        ThrowTruncated();
      length += (int)bitBuf & ((1 << extra) - 1);
      bitBuf >>= extra;
      bitCnt -= extra;

      entry = dist[(int)bitBuf & ((1 << DistRootBits) - 1)];
      if ((entry & 0x80) != 0)
        entry = dist[(entry >> 8) + ((int)(bitBuf >> DistRootBits) & ((1 << (entry & 0x0F)) - 1))];

      codeLength = entry & 0xFF;
      if (codeLength == 0)
        ThrowInvalidCode();
      if (codeLength > bitCnt)
        ThrowTruncated();

      bitBuf >>= codeLength;
      bitCnt -= codeLength;
      var distSymbol = entry >> 8;
      if ((uint)distSymbol >= 30)
        ThrowInvalidCode();

      var distance = Unsafe.Add(ref distBase, distSymbol);
      extra = Unsafe.Add(ref distExtra, distSymbol);
      if (extra > bitCnt)
        ThrowTruncated();
      distance += (int)bitBuf & ((1 << extra) - 1);
      bitBuf >>= extra;
      bitCnt -= extra;

      if (distance > wpos)
        ThrowDistanceTooFar(distance, wpos);

      // Bounds: wpos < stopAt, so wpos + length + 7 < buf.Length; the source lies before wpos.
      ref var dst = ref Unsafe.Add(ref output, wpos);
      ref var src = ref Unsafe.Add(ref output, wpos - distance);
      if (distance >= 8)
        // Each eight-byte step reads only bytes at least eight behind it, all already final.
        for (var i = 0; i < length; i += 8)
          Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, i), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, i)));
      else if (distance == 1)
        Unsafe.InitBlockUnaligned(ref dst, src, (uint)length);
      else
        for (var i = 0; i < length; ++i)
          Unsafe.Add(ref dst, i) = Unsafe.Add(ref src, i);

      wpos += length;
    }

    (this._bitBuf, this._bitCnt, this._inPos, this._wpos) = (bitBuf, bitCnt, inPos, wpos);
  }

  private void ReadDynamicTables() {
    var hlit = (int)this.GetBits(5) + 257;
    var hdist = (int)this.GetBits(5) + 1;
    var hclen = (int)this.GetBits(4) + 4;
    if (hlit > 286 || hdist > 30)
      throw new InvalidDataException($"Invalid DEFLATE dynamic header: HLIT={hlit}, HDIST={hdist}.");

    var codeLengthLengths = new int[DeflateConstants.CodeLengthAlphabetSize];
    var order = DeflateConstants.CodeLengthOrder;
    for (var i = 0; i < hclen; ++i)
      codeLengthLengths[order[i]] = (int)this.GetBits(3);

    var codeLengthTable = BuildTable(codeLengthLengths, CodeLengthRootBits);

    var lengths = new int[hlit + hdist];
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

      lengths.AsSpan(index, repeat).Fill(value);
      index += repeat;
    }

    if (lengths[DeflateConstants.EndOfBlock] == 0)
      throw new InvalidDataException("DEFLATE block has no end-of-block code.");

    this._litTable = BuildTable(lengths.AsSpan(0, hlit), LitRootBits);
    this._distTable = BuildTable(lengths.AsSpan(hlit, hdist), DistRootBits);
  }

  /// <summary>
  /// Builds a two-level decoding table for canonical Huffman code lengths (RFC 1951 §3.2.2).
  /// </summary>
  /// <remarks>
  /// A direct entry is <c>symbol &lt;&lt; 8 | length</c>; a link to a sub-table is
  /// <c>offset &lt;&lt; 8 | 0x80 | subBits</c>, and the sub-table is indexed by the bits after
  /// the root ones. Zero marks a bit pattern no code describes — an incomplete code is
  /// accepted, an over-subscribed one is not.
  /// </remarks>
  private static int[] BuildTable(ReadOnlySpan<int> lengths, int rootBits) {
    Span<int> count = stackalloc int[16];
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
    var code = 0;
    for (var length = 1; length <= 15; ++length) {
      code = (code + count[length - 1]) << 1;
      next[length] = code;
    }

    var rootSize = 1 << rootBits;
    var rootMask = rootSize - 1;
    var reversed = new int[lengths.Length];
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

    var table = new int[size];
    for (var symbol = 0; symbol < lengths.Length; ++symbol) {
      var length = lengths[symbol];
      if (length == 0)
        continue;

      var rev = reversed[symbol];
      var entry = (symbol << 8) | length;
      if (length <= rootBits) {
        for (var i = rev; i < rootSize; i += 1 << length)
          table[i] = entry;
        continue;
      }

      var root = rev & rootMask;
      var bits = subBits[root];
      table[root] = (subOffset[root] << 8) | 0x80 | bits;
      for (var i = rev >> rootBits; i < 1 << bits; i += 1 << (length - rootBits))
        table[subOffset[root] + i] = entry;
    }

    return table;
  }

  [DoesNotReturn]
  private static void ThrowDistanceTooFar(int distance, int history)
    => throw new InvalidDataException($"Invalid DEFLATE distance {distance}: only {history} bytes of history.");

  [DoesNotReturn]
  private static void ThrowTruncated() => throw new EndOfStreamException("DEFLATE stream is truncated.");

  [DoesNotReturn]
  private static void ThrowInvalidCode() => throw new InvalidDataException("Invalid DEFLATE Huffman code.");
}
