using System.Buffers.Binary;

namespace FileFormat.FreeArc;

/// <summary>
/// Decoders for FreeArc's preprocessing stages <c>rep</c>, <c>delta</c> and <c>exe</c>, written from
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

  /// <summary><c>exe</c>: the x86 CALL/JMP converter, started at address 0 over the whole stream.</summary>
  public static byte[] DecodeExe(ReadOnlySpan<byte> input) => Compression.Core.Transforms.BcjFilter.DecodeX86(input);

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
