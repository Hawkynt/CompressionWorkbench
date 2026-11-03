using System.Buffers.Binary;

namespace FileFormat.FreeArc;

/// <summary>
/// Decoders for FreeArc's preprocessing stages <c>rep</c>, <c>delta</c>, <c>exe</c>, <c>lzp</c> and <c>dict</c>, written from
/// docs/FREEARC-ON-DISK.md (section "Filter streams"). Each takes the whole stage output and
/// returns its input; every length and offset is checked, so damaged data is refused rather than
/// returned.
/// </summary>
internal static class FreeArcFilters {
  private const int MaxDeltaElement = 30;

  /// <summary>
  /// <c>rep</c>: long-distance repeats. A 32-bit window size, then frames of
  /// <c>length, count, lengths[count], distances[count], literalRuns[count + 1], literals</c> ending
  /// with a zero length. Literal run <c>i</c> precedes match <c>i</c>; the last run ends the frame.
  /// </summary>
  public static byte[] DecodeRep(ReadOnlySpan<byte> input, long expectedSize) {
    var p = new Reader(input, "rep");
    var window = p.U32();
    if (window == 0)
      throw new InvalidDataException("FreeArc rep: window size is zero.");
    var buffer = new Growable(expectedSize);
    while (true) {
      var frameLength = p.U32();
      if (frameLength == 0)
        break;
      var frame = new Reader(p.Take(frameLength), "rep");
      var count = frame.U32();
      if (count > frameLength / 12)
        throw new InvalidDataException("FreeArc rep: frame claims more matches than it can hold.");
      var lengths = frame.Take(count * 4);
      var distances = frame.Take(count * 4);
      var runs = frame.Take((count + 1) * 4);
      for (var i = 0u; ; ++i) {
        var run = BinaryPrimitives.ReadUInt32LittleEndian(runs[(int)(i * 4)..]);
        buffer.Append(frame.Take(run));
        if (i == count)
          break;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengths[(int)(i * 4)..]);
        var distance = BinaryPrimitives.ReadUInt32LittleEndian(distances[(int)(i * 4)..]);
        if (distance == 0 || distance > window || distance > buffer.Length)
          throw new InvalidDataException($"FreeArc rep: match distance {distance} reaches outside the window.");
        buffer.CopyMatch(distance, length);
      }
      if (!frame.AtEnd)
        throw new InvalidDataException("FreeArc rep: frame has bytes after its literals.");
    }
    if (!p.AtEnd)
      throw new InvalidDataException("FreeArc rep: data after the end marker.");
    return buffer.ToArray();
  }

  /// <summary>
  /// <c>delta</c>: blocks of <c>dataSize, tableBytes, skips[], types[], rows[], data</c>, each table
  /// array holding <c>tableBytes / 4</c> 32-bit values. Each described table is un-reordered and
  /// un-differenced in place before the block's data is emitted.
  /// </summary>
  public static byte[] DecodeDelta(ReadOnlySpan<byte> input) {
    var p = new Reader(input, "delta");
    Span<bool> immutable = stackalloc bool[MaxDeltaElement + 1];
    using var output = new MemoryStream(input.Length);
    while (!p.AtEnd) {
      var dataSize = p.U32();
      var tableBytes = p.U32();
      if (tableBytes % 4 != 0)
        throw new InvalidDataException("FreeArc delta: table descriptor size is not a multiple of 4.");
      var skips = p.Take(tableBytes);
      var types = p.Take(tableBytes);
      var rows = p.Take(tableBytes);
      var data = p.Take(dataSize).ToArray();
      var at = 0L;
      for (var t = 0; t < tableBytes; t += 4) {
        at += BinaryPrimitives.ReadUInt32LittleEndian(skips[t..]);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(types[t..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(rows[t..]);
        // The type is 1 << N with bit i set for each column i that is stored unchanged.
        var n = 0;
        for (; type > 1; ++n, type >>= 1) {
          if (n >= MaxDeltaElement)
            throw new InvalidDataException("FreeArc delta: table element is wider than 30 bytes.");
          immutable[n] = (type & 1) != 0;
        }
        if (n == 0 || type != 1)
          throw new InvalidDataException("FreeArc delta: invalid table type.");
        var size = (long)n * count;
        if (at < 0 || at + size > data.Length)
          throw new InvalidDataException("FreeArc delta: table runs past its block.");
        var table = data.AsSpan((int)at, (int)size);
        Unreorder(table, n, (int)count, immutable[..n]);
        Undiff(table, n, (int)count, immutable[..n]);
        at += size;
      }
      output.Write(data);
    }
    return output.ToArray();
  }

  /// <summary>Puts the stored-unchanged columns (gathered first) back between the others.</summary>
  private static void Unreorder(Span<byte> table, int n, int rows, ReadOnlySpan<bool> immutable) {
    var fixedColumns = 0;
    foreach (var f in immutable)
      if (f)
        ++fixedColumns;
    if (fixedColumns == 0 || fixedColumns == n)
      return;
    var copy = table.ToArray();
    int q = 0, q1 = fixedColumns * rows, p = 0;
    for (var i = 0; i < rows; ++i)
      for (var k = 0; k < n; ++k)
        table[p++] = immutable[k] ? copy[q++] : copy[q1++];
  }

  /// <summary>Adds each row to the one after it, column by column with carries over adjacent changed columns.</summary>
  private static void Undiff(Span<byte> table, int n, int rows, ReadOnlySpan<bool> immutable) {
    for (var r = n; r < n * rows; r += n) {
      var carry = 0;
      for (var i = 0; i < n; ++i) {
        if (immutable[i]) {
          carry = 0;
          continue;
        }
        var sum = table[r + i] + table[r + i - n] + carry;
        table[r + i] = (byte)sum;
        carry = sum >> 8;
      }
    }
  }

  /// <summary>
  /// <c>4x4</c>: a 32-bit version (0), then chunks of <c>decodedSize, storedSize, bytes</c> until
  /// the stage input ends. Each chunk is an independent stream of <paramref name="inner"/> that
  /// decodes to exactly <c>decodedSize</c> bytes; <c>decodedSize</c> = 0xFFFFFFFF marks a chunk
  /// stored as is.
  /// </summary>
  public static byte[] DecodeFourByFour(ReadOnlySpan<byte> input, string inner, Func<string, byte[], long, byte[]> decodeInner) {
    var p = new Reader(input, "4x4");
    var version = p.U32();
    if (version != 0)
      throw new InvalidDataException($"FreeArc 4x4: unknown format version {version}.");
    using var output = new MemoryStream(input.Length * 2);
    while (!p.AtEnd) {
      var decoded = p.U32();
      var stored = p.U32();
      var chunk = p.Take(stored);
      if (decoded == uint.MaxValue) {
        output.Write(chunk);
        continue;
      }
      if (decoded > int.MaxValue)
        throw new InvalidDataException("FreeArc 4x4: chunk is larger than supported.");
      var data = decodeInner(inner, chunk.ToArray(), decoded);
      if (data.Length != decoded)
        throw new InvalidDataException($"FreeArc 4x4: chunk decoded to {data.Length} bytes, expected {decoded}.");
      output.Write(data);
    }
    return output.ToArray();
  }

  /// <summary><c>exe</c>: the x86 CALL/JMP converter, started at address 0 over the whole stream.</summary>
  public static byte[] DecodeExe(ReadOnlySpan<byte> input) => Compression.Core.Transforms.BcjFilter.DecodeX86(input);

  /// <summary>
  /// <c>lzp</c>: chunks of a signed 32-bit length, negative for stored bytes, positive for an LZP
  /// block whose tokens run forward from byte 12 while its match lengths run backward from its end.
  /// A block decodes to at most <paramref name="blockSize"/> bytes.
  /// </summary>
  public static byte[] DecodeLzp(ReadOnlySpan<byte> input, int blockSize, int minLength, int hashBits, long barrier, int smallestLength) {
    var p = new Reader(input, "lzp");
    var output = new Growable(input.Length * 2L);
    var block = new LzpBlockDecoder(blockSize, hashBits, minLength, barrier, smallestLength);
    while (!p.AtEnd) {
      var length = (int)p.U32();
      if (length < 0)
        output.Append(p.Take((uint)-(long)length));
      else
        output.Append(block.Decode(p.Take((uint)length)));
    }
    return output.ToArray();
  }

  /// <summary>
  /// <c>dict</c>: the LZP chunk framing around dictionary blocks. A block holds its dictionary —
  /// one-byte word lengths, shared-prefix counts of the two-byte words, the one-byte words, a
  /// separator, the two-byte words, an escape byte — and then the coded text. A block decodes to at
  /// most <paramref name="blockSize"/> bytes.
  /// </summary>
  public static byte[] DecodeDict(ReadOnlySpan<byte> input, int blockSize) {
    var p = new Reader(input, "dict");
    var output = new Growable(input.Length * 2L);
    while (!p.AtEnd) {
      var length = (int)p.U32();
      if (length < 0)
        output.Append(p.Take((uint)-(long)length));
      else
        DecodeDictBlock(p.Take((uint)length), blockSize, output);
    }
    return output.ToArray();
  }

  private const int DictTwoByteCode = 1;

  private static void DecodeDictBlock(ReadOnlySpan<byte> block, int blockSize, Growable output) {
    var p = new Reader(block, "dict");
    var singleLength = p.Take(256).ToArray();

    // Every word lives in one buffer as (start, length); two-byte words are indexed [first * 256 + second].
    var text = new List<byte>();
    var singleStart = new int[256];
    var pairStart = new int[256 * 256];
    var pairLength = new int[256 * 256];

    var shared = new Dictionary<int, byte[]>();
    for (var c = 0; c < 256; ++c)
      if (singleLength[c] == DictTwoByteCode)
        shared[c] = p.Take(256).ToArray();

    for (var c = 0; c < 256; ++c) {
      if (singleLength[c] == DictTwoByteCode)
        continue;
      singleStart[c] = text.Count;
      text.AddRange(p.Take(singleLength[c]));
    }

    var separator = p.Byte();
    var previous = -1;
    for (var c = 0; c < 256; ++c) {
      if (!shared.TryGetValue(c, out var counts))
        continue;
      for (var d = 0; d < 256; ++d) {
        var start = text.Count;
        var count = counts[d];
        if (count > 0 && previous < 0)
          throw new InvalidDataException("FreeArc dict: the first two-byte word shares a prefix with no word.");
        for (var k = 0; k < count; ++k)
          text.Add(text[previous + k]); // may run on into this word's own bytes
        for (byte b; (b = p.Byte()) != separator;)
          text.Add(b);
        pairStart[c * 256 + d] = start;
        pairLength[c * 256 + d] = text.Count - start;
        previous = start;
      }
    }

    var escape = p.Byte();
    singleLength[escape] = DictTwoByteCode;
    for (var d = 0; d < 256; ++d) {
      pairStart[escape * 256 + d] = text.Count;
      pairLength[escape * 256 + d] = 1;
      text.Add((byte)d);
    }

    var words = text.ToArray();
    var produced = 0L;
    void Put(ReadOnlySpan<byte> bytes) {
      produced += bytes.Length;
      if (produced > blockSize)
        throw new InvalidDataException("FreeArc dict: block decodes past its block size.");
      output.Append(bytes);
    }

    while (!p.AtEnd) {
      var c = p.Byte();
      var length = singleLength[c];
      if (length == 0)
        Put([c]);
      else if (length == DictTwoByteCode) {
        var index = c * 256 + p.Byte();
        Put(words.AsSpan(pairStart[index], pairLength[index]));
      } else
        Put(words.AsSpan(singleStart[c], length));
    }
  }

  /// <summary>One LZP block at a time; the table and the output buffer are reused between blocks.</summary>
  private sealed class LzpBlockDecoder(int blockSize, int hashBits, int minLength, long barrier, int smallestLength) {
    private const byte Flag = 0xB5, Escape = 255;
    private const int RunUnit = 254, HeadBytes = 12, InitialPosition = 5, MaxStride = 7;

    private readonly int[] _table = new int[1 << hashBits];
    private readonly uint _mask = (1u << hashBits) - 1;
    private byte[] _output = [];
    private int _pos;

    public ReadOnlySpan<byte> Decode(ReadOnlySpan<byte> coded) {
      if (coded.Length <= HeadBytes || blockSize < HeadBytes)
        throw new InvalidDataException("FreeArc lzp: block is shorter than its 12-byte head.");
      Array.Fill(this._table, InitialPosition);
      this._pos = 0;
      this.Reserve(Math.Min(blockSize, Math.Max(coded.Length * 4L, 1 << 16)));
      this.Append(coded[..HeadBytes]);
      int front = HeadBytes, back = coded.Length;
      int step = 1, stride = 1;
      do {
        var context = this.Context(this._pos);
        var slot = this.Slot(this._pos);
        var token = coded[front++];
        var source = this._table[slot];
        if (--step == 0) {
          this._table[slot] = this._pos;
          step = stride;
        }
        if (token != Flag || this.Context(source) != context) {
          this.Put(token);
          continue;
        }
        if (back <= front)
          throw new InvalidDataException("FreeArc lzp: match lengths run into the tokens.");
        var t = coded[--back];
        if (t == Escape) {
          this.Put(Flag);
          continue;
        }
        this._table[slot] = this._pos;
        var distance = this._pos - source;
        if (distance > (stride + 1L) * this._table.Length && stride < MaxStride)
          ++stride;
        long count = (distance > barrier ? smallestLength : minLength) - 1;
        for (; t == 0; t = coded[--back]) {
          count += RunUnit;
          if (back <= front)
            throw new InvalidDataException("FreeArc lzp: match lengths run into the tokens.");
        }
        count += t;
        this.Reserve(count);
        for (var countdown = 2 * stride + 2; count > 0; --count) {
          if (--countdown == 0) {
            countdown = 2 * stride + 1;
            this._table[this.Slot(this._pos)] = this._pos;
          }
          this._output[this._pos++] = this._output[source++];
        }
      } while (front < back);
      return this._output.AsSpan(0, this._pos);
    }

    /// <summary>The four output bytes before <paramref name="at"/>, little-endian.</summary>
    private uint Context(int at) => BinaryPrimitives.ReadUInt32LittleEndian(this._output.AsSpan(at - 4));

    private int Slot(int at) {
      var c = this.Context(at);
      return (int)((c + 5 * uint.RotateRight(c, 17) + 3 * this.Context(at - 1)) & this._mask);
    }

    private void Append(ReadOnlySpan<byte> bytes) {
      this.Reserve(bytes.Length);
      bytes.CopyTo(this._output.AsSpan(this._pos));
      this._pos += bytes.Length;
    }

    private void Put(byte value) {
      this.Reserve(1);
      this._output[this._pos++] = value;
    }

    private void Reserve(long more) {
      var need = this._pos + more;
      if (need > blockSize)
        throw new InvalidDataException("FreeArc lzp: block decodes past its block size.");
      if (need > this._output.Length)
        Array.Resize(ref this._output, (int)Math.Min(blockSize, Math.Max(need, this._output.Length * 2L)));
    }
  }

  private ref struct Reader(ReadOnlySpan<byte> data, string stage) {
    private ReadOnlySpan<byte> _rest = data;

    public readonly bool AtEnd => this._rest.IsEmpty;

    public uint U32() {
      if (this._rest.Length < 4)
        throw new InvalidDataException($"FreeArc {stage}: stream is truncated.");
      var v = BinaryPrimitives.ReadUInt32LittleEndian(this._rest);
      this._rest = this._rest[4..];
      return v;
    }

    public byte Byte() {
      if (this._rest.IsEmpty)
        throw new InvalidDataException($"FreeArc {stage}: stream is truncated.");
      var v = this._rest[0];
      this._rest = this._rest[1..];
      return v;
    }

    public ReadOnlySpan<byte> Take(uint length) {
      if (length > (uint)this._rest.Length)
        throw new InvalidDataException($"FreeArc {stage}: stream is truncated.");
      var s = this._rest[..(int)length];
      this._rest = this._rest[(int)length..];
      return s;
    }
  }

  /// <summary>An output buffer that can copy from its own earlier bytes, overlapping like LZ77.</summary>
  private sealed class Growable(long expected) {
    private byte[] _data = new byte[expected is > 0 and < 1L << 30 ? expected : 1 << 16];

    public long Length { get; private set; }

    private void Ensure(long more) {
      var need = this.Length + more;
      if (need > Array.MaxLength)
        throw new InvalidDataException("FreeArc: decoded stage exceeds the supported size.");
      if (need <= this._data.Length)
        return;
      Array.Resize(ref this._data, (int)Math.Min(Array.MaxLength, Math.Max(need, this._data.Length * 2L)));
    }

    public void Append(ReadOnlySpan<byte> bytes) {
      this.Ensure(bytes.Length);
      bytes.CopyTo(this._data.AsSpan((int)this.Length));
      this.Length += bytes.Length;
    }

    public void CopyMatch(uint distance, uint length) {
      this.Ensure(length);
      var to = (int)this.Length;
      var from = to - (int)distance;
      for (var k = 0; k < length; ++k)
        this._data[to + k] = this._data[from + k];
      this.Length += length;
    }

    public byte[] ToArray() => this._data.AsSpan(0, (int)this.Length).ToArray();
  }
}
