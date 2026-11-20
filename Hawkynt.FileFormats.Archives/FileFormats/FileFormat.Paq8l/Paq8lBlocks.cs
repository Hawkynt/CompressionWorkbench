namespace FileFormat.Paq8l;

/// <summary>Block types of the coded stream (docs/PAQ8L-FORMAT.md §2).</summary>
internal enum Paq8lBlockType {
  Default = 0,
  Jpeg = 1,
  Exe = 2,
}

/// <summary>
/// Cuts a chunk into typed blocks and applies the EXE transform (§2.1, §2.2): the writer's side
/// of the block stream.
/// </summary>
internal static class Paq8lSegmenter {
  private const int ExeBlock = 0x10000;

  /// <summary>
  /// Encodes <paramref name="length"/> bytes of <paramref name="file"/> from <paramref name="start"/>
  /// as blocks; EXE blocks record their offset within the file.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<byte> file, int start, int length) {
    var output = new MemoryStream(length + 64);
    var type = Paq8lBlockType.Default;
    var begin = start;
    var left = length;
    while (left > 0) {
      var next = Detect(file, begin, left, type, out var end);
      var len = end - begin;
      if (len > 0) {
        output.WriteByte((byte)type);
        WriteBigEndian(output, len);
        var block = file.Slice(begin, len);
        if (type == Paq8lBlockType.Exe) EncodeExe(output, block, begin);
        else output.Write(block);
      }
      left -= len;
      type = next;
      begin = end;
    }
    return output.ToArray();
  }

  /// <summary>
  /// Scans up to <paramref name="n"/> bytes from <paramref name="start"/> with current type
  /// <paramref name="type"/>; returns the type that follows and where the current block ends.
  /// </summary>
  internal static Paq8lBlockType Detect(ReadOnlySpan<byte> data, int start, int n, Paq8lBlockType type, out int end) {
    uint b1 = 0, b0 = 0;
    var abs = new int[256];
    var rel = new int[256];
    int run = 0, first = 0, last = 0;
    int soi = 0, sof = 0, sos = 0;
    for (var i = 0; i < n; ++i) {
      var c = data[start + i];
      b1 = b1 << 8 | b0 >> 24;
      b0 = b0 << 8 | c;

      if (i >= 3 && (b0 & 0xFFFFFFF0) == 0xFFD8FFE0) soi = i;
      if (soi != 0 && i - soi < 0x10000 && (b1 & 0xFF) == 0xFF && (b0 & 0xFF0000FF) == 0xC0000008) sof = i;
      if (soi != 0 && sof != 0 && sof > soi && i - soi < 0x10000 && i - sof < 0x1000 && (b0 & 0xFFFF) == 0xFFDA) {
        sos = i;
        if (type != Paq8lBlockType.Jpeg) {
          end = start + soi - 3;
          return Paq8lBlockType.Jpeg;
        }
      }
      if (type == Paq8lBlockType.Jpeg && sos != 0 && i > sos && (b0 & 0xFF00) == 0xFF00 && (b0 & 0xFF) != 0 && (b0 & 0xF8) != 0xD0) {
        end = start + i + 1;
        return Paq8lBlockType.Default;
      }

      if ((b1 & 0xFE) == 0xE8 && ((b0 + 1) & 0xFE) == 0) {
        var r = (int)(b0 >> 24);
        var a = (r + i) & 0xFF;
        var dr = i - rel[r];
        var da = i - abs[a];
        if (da < dr && da < 0x1000 && abs[a] > 5) {
          last = i;
          ++run;
          if (first == 0 || first > abs[a]) first = abs[a];
        } else
          run = 0;
        if (type != Paq8lBlockType.Exe && run >= 4 && first > 5) {
          end = start + first - 5;
          return Paq8lBlockType.Exe;
        }
        abs[a] = i;
        rel[r] = i;
      }
      if (type == Paq8lBlockType.Exe && i - last > 0x1000) {
        end = start + last;
        return Paq8lBlockType.Default;
      }
    }
    end = start + n;
    return type;
  }

  private static void EncodeExe(Stream output, ReadOnlySpan<byte> block, int begin) {
    WriteBigEndian(output, block.Length);
    WriteBigEndian(output, begin);
    var sub = new byte[ExeBlock];
    for (var offset = 0; offset < block.Length; offset += ExeBlock) {
      var size = Math.Min(block.Length - offset, ExeBlock);
      block.Slice(offset, size).CopyTo(sub);
      for (var i = size - 1; i >= 4; --i) {
        if (sub[i - 4] is not (0xE8 or 0xE9) || sub[i] is not (0x00 or 0xFF)) continue;
        var a = (sub[i - 3] | sub[i - 2] << 8 | sub[i - 1] << 16 | sub[i] << 24) + offset + begin + i + 1;
        a <<= 7;
        a >>= 7;
        sub[i] = (byte)(a >> 24);
        sub[i - 1] = (byte)(a >> 16);
        sub[i - 2] = (byte)(a >> 8);
        sub[i - 3] = (byte)a;
      }
      output.Write(sub, 0, size);
    }
  }

  private static void WriteBigEndian(Stream output, int value) {
    output.WriteByte((byte)(value >> 24));
    output.WriteByte((byte)(value >> 16));
    output.WriteByte((byte)(value >> 8));
    output.WriteByte((byte)value);
  }
}

/// <summary>
/// Reads the block stream back into file bytes (§2, §2.2). Its state carries across chunks and
/// files, as the format requires; the writer keeps a second instance for its transform test.
/// </summary>
internal sealed class Paq8lBlockDecoder {
  private const int ExeBlock = 0x10000;
  private int _type, _left;
  private int _offset, _queued, _size, _begin;
  private readonly byte[] _window = new byte[5];

  /// <summary>Returns the next file byte, pulling coded bytes (−1 past their end) from <paramref name="source"/>.</summary>
  public int Next(Func<int> source) {
    while (this._left == 0) {
      this._type = source();
      var len = source() << 24;
      len |= source() << 16;
      len |= source() << 8;
      len |= source();
      this._left = len < 0 ? 1 : len;
    }
    --this._left;
    return this._type == (int)Paq8lBlockType.Exe ? this.NextExe(source) : source();
  }

  private int NextExe(Func<int> source) {
    while (this._offset == this._size && this._queued == 0) {
      this._offset = 0;
      this._size = source() << 24;
      this._size |= source() << 16;
      this._size |= source() << 8;
      this._size |= source();
      this._begin = source() << 24;
      this._begin |= source() << 16;
      this._begin |= source() << 8;
      this._begin |= source();
    }
    var c = this._window;
    while (this._offset < this._size && this._queued < 5) {
      c[4] = c[3];
      c[3] = c[2];
      c[2] = c[1];
      c[1] = c[0];
      c[0] = (byte)source();
      ++this._queued;
      ++this._offset;
    }
    if (this._queued == 5 && c[4] is 0xE8 or 0xE9 && c[0] is 0x00 or 0xFF
        && (((this._offset - 1) ^ (this._offset - 5)) & -ExeBlock) == 0) {
      var a = (c[3] | c[2] << 8 | c[1] << 16 | c[0] << 24) - this._offset - this._begin;
      a <<= 7;
      a >>= 7;
      c[3] = (byte)a;
      c[2] = (byte)(a >> 8);
      c[1] = (byte)(a >> 16);
      c[0] = (byte)(a >> 24);
    }
    // An exhausted window only happens on corrupt input; the format then reads outside its
    // window, which is reported as no byte at all.
    return this._queued > 0 ? c[--this._queued] : -1;
  }
}
