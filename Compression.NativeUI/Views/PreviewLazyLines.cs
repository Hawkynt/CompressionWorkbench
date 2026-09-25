using System.Text;

namespace Compression.NativeUI.Views;

/// <summary>One byte of a hex dump row, with its display text precomputed.</summary>
internal readonly record struct HexByte(byte Value, string HexText, char AsciiChar);

/// <summary>A single hex dump row.</summary>
internal sealed class HexLine {
  public required string OffsetText { get; init; }
  public required HexByte[] Bytes { get; init; }
  public required int ByteCount { get; init; }
  public required int BytesPerRow { get; init; }
}

/// <summary>
/// Hex rows materialised on access only. The backing buffer is held by reference and no per-row
/// state is allocated up front, so opening a gigabyte-sized binary in hex view costs O(viewport)
/// memory rather than O(file).
/// </summary>
internal sealed class LazyHexLines {
  private readonly byte[] _data;
  private readonly int _bytesPerRow;
  private readonly int _offsetWidth;

  public LazyHexLines(byte[] data, int bytesPerRow) {
    this._data = data;
    this._bytesPerRow = bytesPerRow > 0 ? bytesPerRow : 16;
    this._offsetWidth = data.Length > 0xFFFFFF ? 8 : data.Length > 0xFFFF ? 6 : 4;
    this.Count = (int)((data.LongLength + this._bytesPerRow - 1) / this._bytesPerRow);
  }

  public int Count { get; }

  public HexLine this[int index] {
    get {
      ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)this.Count, nameof(index));

      var offset = (long)index * this._bytesPerRow;
      var count = (int)Math.Min(this._bytesPerRow, this._data.LongLength - offset);
      var bytes = new HexByte[count];
      for (var i = 0; i < count; ++i) {
        var b = this._data[offset + i];
        bytes[i] = new(b, b.ToString("X2"), b is >= 0x20 and < 0x7F ? (char)b : '.');
      }

      return new() {
        OffsetText = offset.ToString($"X{this._offsetWidth}"),
        Bytes = bytes,
        ByteCount = count,
        BytesPerRow = this._bytesPerRow,
      };
    }
  }
}

/// <summary>
/// Lazy line view over an arbitrary byte buffer for the text-mode preview. Newline offsets are
/// pre-scanned once (a single O(n) pass, fast even for hundreds of megabytes), with soft breaks
/// inserted every <see cref="ChunkBytes"/> bytes so a blob with no newlines does not materialise as
/// one enormous line. Each row is decoded on access via the supplied <see cref="Encoding"/>.
/// </summary>
internal sealed class LazyTextLines {
  private const int ChunkBytes = 4096;

  private readonly byte[] _data;
  private readonly Encoding _encoding;

  // _starts[i] is the byte offset of line i; the final element is the buffer length, acting as the
  // sentinel that gives the last line its length.
  private readonly long[] _starts;

  public LazyTextLines(byte[] data, Encoding encoding) {
    this._data = data;
    this._encoding = encoding;
    this._starts = ScanLineStarts(data);
    this.Count = this._starts.Length - 1;
  }

  public int Count { get; }

  public string this[int index] {
    get {
      ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)this.Count, nameof(index));

      var start = this._starts[index];
      var len = (int)(this._starts[index + 1] - start);

      // Strip the trailing line break for display.
      if (len > 0 && this._data[start + len - 1] == (byte)'\n') --len;
      if (len > 0 && this._data[start + len - 1] == (byte)'\r') --len;

      return len > 0 ? this._encoding.GetString(this._data, (int)start, len) : "";
    }
  }

  private static long[] ScanLineStarts(byte[] data) {
    var starts = new List<long> { 0 };
    long lastBreak = 0;

    for (long i = 0; i < data.LongLength; ++i) {
      if (data[i] == (byte)'\n') {
        starts.Add(i + 1);
        lastBreak = i + 1;
      } else if (i - lastBreak >= ChunkBytes) {
        starts.Add(i);
        lastBreak = i;
      }
    }

    starts.Add(data.LongLength);
    return [.. starts];
  }
}
