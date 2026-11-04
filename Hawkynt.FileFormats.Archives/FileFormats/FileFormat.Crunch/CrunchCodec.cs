namespace FileFormat.Crunch;

/// <summary>
/// CP/M CRUNCH 1.x and 2.x files, implemented from <c>docs/CRUNCH-ON-DISK.md</c>: reads both
/// stream versions, writes 2.x.
/// </summary>
internal static class CrunchCodec {
  private const int TableSize = 4096;
  private const int HashSize = 5003;
  private const int Unused = 32767;
  private const int EndOfStream = 256;
  private const int Reset = 257;
  private const int FirstFreeCode = 260;
  private const byte RunEscape = 0x90;

  /// <summary>Decodes a CRUNCH file.</summary>
  /// <exception cref="InvalidDataException">The file is not CRUNCH, is truncated or fails its checksum.</exception>
  public static (string? Name, byte[] Data) Decode(ReadOnlySpan<byte> file) {
    if (file.Length < 2 || file[0] != CrunchConstants.Magic1 || file[1] != CrunchConstants.Magic2)
      throw new InvalidDataException("Invalid Crunch magic bytes.");
    var end = file[2..].IndexOf((byte)0);
    if (end < 0)
      throw new InvalidDataException("Crunch name is not terminated.");
    var name = end == 0 ? null : System.Text.Encoding.Latin1.GetString(file.Slice(2, end));
    var position = 2 + end + 1;
    if (position + 4 > file.Length)
      throw new InvalidDataException("Truncated Crunch header.");
    var version = file[position + 1] & 0xF0;
    var checkType = file[position + 2];
    position += 4;
    var old = version switch {
      0x10 => true,
      0x20 => false,
      _ => throw new InvalidDataException($"Crunch stream version 0x{file[position - 3]:X2} is not 1.x or 2.x."),
    };

    var decoder = new Decoder(old);
    var bits = new BitReader(file, position);
    var data = decoder.Run(ref bits);
    if (checkType == 0) {
      var at = bits.NextBytePosition;
      if (at + 2 > file.Length)
        throw new InvalidDataException("Crunch checksum is missing.");
      var stored = file[at] | file[at + 1] << 8;
      var sum = 0;
      foreach (var b in data)
        sum += b;
      if ((sum & 0xFFFF) != stored)
        throw new InvalidDataException("Crunch checksum mismatch.");
    }

    return (name, data);
  }

  /// <summary>Encodes <paramref name="data"/> as a CRUNCH 2.x file.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, string? name) {
    using var output = new MemoryStream(data.Length / 2 + 32);
    output.WriteByte(CrunchConstants.Magic1);
    output.WriteByte(CrunchConstants.Magic2);
    foreach (var c in name ?? "")
      output.WriteByte(c is > (char)0 and < (char)256 ? (byte)c : (byte)'_');
    output.WriteByte(0);
    output.Write([0x20, 0x20, 0x00, 0x00]);

    var writer = new BitWriter(output);
    var rle = RunLengthEncode(data);
    var dictionary = new Dictionary<int, int>();
    var nextCode = FirstFreeCode;
    // What the reader's table looks like after each code it reads.
    var readerLast = FirstFreeCode - 1;
    var readerWidth = 9;
    var readerSkipsAdd = true;

    void Emit(int code) {
      writer.Write(code, readerWidth);
      if (code is EndOfStream or Reset)
        return;
      if (readerSkipsAdd) {
        readerSkipsAdd = false;
        return;
      }

      ++readerLast;
      if (readerLast < TableSize - 2 && readerLast == (1 << readerWidth) - 2)
        ++readerWidth;
    }

    if (rle.Count > 0) {
      var w = (int)rle[0];
      for (var i = 1; i < rle.Count; ++i) {
        var c = rle[i];
        if (dictionary.TryGetValue(w << 8 | c, out var extended)) {
          w = extended;
          continue;
        }

        Emit(w);
        if (readerLast == TableSize - 1) {
          // The reader's table is full: the next assignment would reuse a code. Start over.
          Emit(Reset);
          dictionary.Clear();
          nextCode = FirstFreeCode;
          readerLast = FirstFreeCode - 1;
          readerWidth = 9;
          readerSkipsAdd = true;
        } else if (nextCode < TableSize)
          dictionary[w << 8 | c] = nextCode++;

        w = c;
      }

      Emit(w);
    }

    Emit(EndOfStream);
    writer.Flush();
    var sum = 0;
    foreach (var b in data)
      sum += b;
    output.WriteByte((byte)sum);
    output.WriteByte((byte)(sum >> 8));
    return output.ToArray();
  }

  /// <summary>RLE that the reader undoes: <c>c 90 n</c> repeats <c>c</c>; a literal 0x90 is <c>90 00</c>.</summary>
  private static List<byte> RunLengthEncode(ReadOnlySpan<byte> data) {
    var result = new List<byte>(data.Length + 16);
    for (var i = 0; i < data.Length;) {
      var c = data[i];
      if (c == RunEscape) {
        // 90 00 does not become the byte to repeat, so 0x90 runs are written one by one.
        result.Add(RunEscape);
        result.Add(0);
        ++i;
        continue;
      }

      var run = 1;
      while (i + run < data.Length && data[i + run] == c && run < 255)
        ++run;
      result.Add(c);
      if (run >= 3) {
        result.Add(RunEscape);
        result.Add((byte)run);
        i += run;
      } else
        ++i;
    }

    return result;
  }

  private sealed class Decoder(bool old) {
    private readonly int[] _prefix = new int[TableSize];
    private readonly int[] _suffix = new int[TableSize];
    private readonly int[] _firstOf = new int[TableSize];
    private readonly int[] _links = new int[TableSize];
    private readonly bool[] _seen = new bool[TableSize];
    private readonly int[] _hash = new int[HashSize];
    private readonly int[] _scratch = new int[TableSize];
    private readonly List<byte> _output = [];
    private int _last;
    private int _lastLiteral;
    private bool _repeating;

    public byte[] Run(ref BitReader bits) {
      var width = old ? 12 : 9;
      var initialWidth = width;
      this.Init();
      var previous = old ? 0xFFFF : 0;
      var first = true;
      var k = 0;
      while (true) {
        var code = this.Read(ref bits, width);
        var skipAdd = false;
        if (first) {
          k = code;
          first = false;
          skipAdd = true;
        }

        if (old) {
          if (code == 0)
            break;
        } else {
          if (code == EndOfStream)
            break;
          if (code == Reset) {
            width = initialWidth;
            this.Init();
            code = this.Read(ref bits, width);
            if (code == EndOfStream)
              break;
            skipAdd = true;
          }
        }

        if (old ? this._suffix[code] != Unused : code <= this._last) {
          this.OutputString(code);
          k = this.FirstByte(code);
        } else {
          this.OutputString(previous);
          this.OutputByte(k);
        }

        this._seen[code] = true;
        if (this._last == TableSize - 1) {
          if (!old)
            this.Replace(previous, k);
        } else if (!skipAdd) {
          this.Add(previous, k);
          if (this._last < TableSize - 2 && this._last == (1 << width) - 2)
            ++width;
        }

        previous = code;
      }

      return [.. this._output];
    }

    private int Read(ref BitReader bits, int width) {
      while (true) {
        var code = bits.Read(width);
        // 2.x pads with filler codes that carry nothing.
        if (old || code is not (258 or 259))
          return code;
      }
    }

    private void Init() {
      this._hash.AsSpan().Fill(-1);
      this._prefix.AsSpan().Fill(Unused);
      this._suffix.AsSpan().Fill(Unused);
      this._firstOf.AsSpan().Fill(Unused);
      this._links.AsSpan().Fill(Unused);
      this._seen.AsSpan().Clear();
      this._last = -1;
      if (old) {
        this._prefix[0] = this._suffix[0] = this._firstOf[0] = 0xFFFF;
        ++this._last;
        for (var c = 0; c < 256; ++c)
          this.Add(0xFFFF, c);
      } else
        for (var c = 0; c < FirstFreeCode; ++c) {
          this.Add(c < 256 ? 0x6FFF : 0x7FFF, c < 256 ? c : 0);
          this._seen[c] = true;
        }
    }

    private static int Hash(int prefix, int c) => (((prefix >> 4) ^ c) | ((prefix & 15) << 8)) + 1;

    private void Add(int prefix, int c) {
      ++this._last;
      if ((this._last & TableSize) != 0) {
        this._last = TableSize - 1;
        return;
      }

      var index = this._last;
      if (old) {
        index = this.OldIndex(prefix, c);
        if (index < 0)
          return;
      } else {
        if (c == Unused)
          c = 0;
        var h = Hash(prefix, c);
        var f = h;
        while (this._hash[f] != -1) {
          f += h - HashSize;
          if (f < 0)
            f += HashSize;
        }

        this._hash[f] = index;
      }

      this._suffix[index] = c;
      if (prefix >= TableSize)
        return;
      this._prefix[index] = prefix;
      this._firstOf[index] = this._prefix[prefix] == Unused ? prefix : this._firstOf[prefix];
    }

    /// <summary>1.x: where the string (prefix, c) lives in the table.</summary>
    private int OldIndex(int prefix, int c) {
      int index;
      if (prefix == 0xFFFF && c == 0)
        index = 0x800;
      else {
        var a = ((prefix + c) | 0x800) & 0x1FFF;
        var b = a >> 1;
        index = ((b * (b + (a & 1))) >> 4) & 0xFFF;
      }

      while (this._suffix[index] != Unused && this._links[index] != Unused)
        index = this._links[index];
      if (this._suffix[index] == Unused)
        return index;

      var chainEnd = index;
      index = (index + 101) & 0xFFF;
      if (this._suffix[index] != Unused) {
        var probed = 0;
        for (; probed < TableSize; ++probed, index = (index + 1) & 0xFFF)
          if (this._suffix[index] == Unused)
            break;
        if (probed == TableSize)
          return -1;
      }

      this._links[chainEnd] = index;
      return index;
    }

    /// <summary>2.x with a full table: reuse a never-read code from the string's hash chain.</summary>
    private void Replace(int prefix, int c) {
      if (c == Unused)
        c = 0;
      var h = Hash(prefix, c);
      var f = h;
      while (this._hash[f] != -1) {
        var code = this._hash[f];
        if (!this._seen[code]) {
          this._prefix[code] = prefix;
          this._suffix[code] = c;
          this._firstOf[code] = this._prefix[prefix] == Unused ? prefix : this._firstOf[prefix];
          break;
        }

        f += h - HashSize;
        if (f < 0)
          f += HashSize;
      }
    }

    private int FirstByte(int code) {
      if (this._prefix[code] != Unused)
        code = this._firstOf[code];
      return code == Unused ? 0 : this._suffix[code];
    }

    private void OutputString(int code) {
      var depth = 0;
      while (this._prefix[code] != Unused && depth < TableSize) {
        this._scratch[depth++] = this._suffix[code];
        code = this._prefix[code];
        if (code >= TableSize)
          throw new InvalidDataException("Crunch string refers outside the table.");
      }

      this.OutputByte(this._suffix[code]);
      while (depth > 0)
        this.OutputByte(this._scratch[--depth]);
    }

    /// <summary>Undoes the RLE layer: 90 n repeats the last literal, 90 00 is a literal 0x90.</summary>
    private void OutputByte(int value) {
      if (value is < 0 or > 255)
        throw new InvalidDataException("Crunch string holds an unassigned code.");
      if (this._repeating) {
        if (value == 0)
          this._output.Add(RunEscape);
        else
          for (var i = 1; i < value; ++i)
            this._output.Add((byte)this._lastLiteral);
        this._repeating = false;
      } else if (value == RunEscape)
        this._repeating = true;
      else {
        this._output.Add((byte)value);
        this._lastLiteral = value;
      }
    }
  }

  private ref struct BitReader(ReadOnlySpan<byte> data, int start) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position = start;
    private int _buffer;
    private int _count;

    /// <summary>The first byte the code stream has not touched.</summary>
    public readonly int NextBytePosition => this._position;

    public int Read(int width) {
      var code = 0;
      for (var filled = 0; filled < width;) {
        if (this._count == 0) {
          if (this._position >= this._data.Length)
            throw new InvalidDataException("Crunch stream ends before its end code.");
          this._buffer = this._data[this._position++];
          this._count = 8;
        }

        var take = Math.Min(this._count, width - filled);
        code = code << take | (this._buffer >> (this._count - take)) & ((1 << take) - 1);
        this._count -= take;
        filled += take;
      }

      return code;
    }
  }

  private sealed class BitWriter(Stream output) {
    private int _buffer;
    private int _count;

    public void Write(int code, int width) {
      for (var bit = width - 1; bit >= 0; --bit) {
        this._buffer = this._buffer << 1 | (code >> bit & 1);
        if (++this._count == 8) {
          output.WriteByte((byte)this._buffer);
          this._buffer = this._count = 0;
        }
      }
    }

    public void Flush() {
      if (this._count == 0)
        return;
      output.WriteByte((byte)(this._buffer << (8 - this._count)));
      this._buffer = this._count = 0;
    }
  }
}
