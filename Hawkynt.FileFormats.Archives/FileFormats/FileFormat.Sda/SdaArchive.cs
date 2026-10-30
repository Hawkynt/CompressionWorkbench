using System.Text;

namespace FileFormat.Sda;

/// <summary>One file of a C64/C128 ARC or SDA archive.</summary>
/// <param name="Name">Host name: the PETSCII name in ASCII plus the CBM type as extension.</param>
/// <param name="CbmName">The raw PETSCII name.</param>
/// <param name="CbmType">CBM file type: P, S, U or R.</param>
/// <param name="Mode">Storage mode 0-5.</param>
/// <param name="Version">ARC entry version, 1 or 2.</param>
/// <param name="Size">Original size in bytes.</param>
/// <param name="StoredSize">Bytes the entry occupies in the archive, header included.</param>
/// <param name="Offset">Offset of the entry header in the file.</param>
/// <param name="LastModified">The entry's date (version 2), or null.</param>
public sealed record SdaEntry(string Name, byte[] CbmName, char CbmType, int Mode, int Version, long Size, long StoredSize, long Offset, DateTime? LastModified) {
  /// <summary>Name of the storage mode.</summary>
  public string MethodName => this.Mode switch {
    0 => "Stored", 1 => "Packed", 2 => "Squeezed", 3 => "Crunched", 4 => "Squashed", 5 => "Crunched (one pass)", _ => $"Mode {this.Mode}",
  };
}

/// <summary>
/// Reads Chris Smeets' Commodore 64/128 ARC archives and their self-dissolving form (SDA): stored,
/// run-length packed, Huffman squeezed, LZW crunched and the combined modes, each entry checked
/// against its 16-bit checksum. The layout is described in docs/C64-ARC-ON-DISK.md.
/// </summary>
public static class SdaArchive {
  private const int BlockPayload = 254;
  private const int DefaultMarker = 0xFE;
  private const int MaxNameLength = 16;
  private const int MaxSize = 1 << 24;

  /// <summary>Offset of the first entry: 0 for a plain archive, after the extractor for an SDA; -1 when neither.</summary>
  public static long FindFirstEntry(ReadOnlySpan<byte> file) {
    if (file.Length < 2) return -1;
    if (file[0] == 2) return 0;
    if (file[0] != 1) return -1;
    if (file.Length < 9 || file[6] != 0x9E) return 0; // a version-1 archive without an extractor
    var line = file[4] | file[5] << 8;
    if (line < 6) return -1;
    long start = (line - 6) * BlockPayload;
    if (line == 15 && file[8] == '7') --start; // the C128 extractor of SDA 2.32 is one byte shorter
    return start;
  }

  /// <summary>Lists every entry.</summary>
  /// <exception cref="InvalidDataException">The data is not a C64 ARC or SDA archive.</exception>
  public static List<SdaEntry> List(ReadOnlySpan<byte> file) {
    var entries = new List<SdaEntry>();
    var start = FindFirstEntry(file);
    if (start < 0 || start >= file.Length) throw new InvalidDataException("Not a Commodore ARC or SDA archive.");
    var offset = start;
    while (offset < file.Length) {
      var header = ParseHeader(file, offset);
      if (header == null) break;
      var h = header.Value;
      long stored = h.Blocks * (long)BlockPayload;
      if (h.Mode == 5) {
        var decoded = Decode(file, offset, h);
        stored = decoded.StoredSize;
        h = h with { Size = decoded.Data.Length };
      }
      if (stored <= 0) break;
      entries.Add(new SdaEntry(HostName(h.Name, h.Type), h.Name, (char)h.Type, h.Mode, h.Version, h.Size, stored, offset, h.Date));
      offset += stored;
    }
    if (entries.Count == 0) throw new InvalidDataException("No C64 ARC entry found.");
    return entries;
  }

  /// <summary>Decodes one entry and checks its checksum.</summary>
  /// <exception cref="InvalidDataException">The entry is corrupt or fails its checksum.</exception>
  public static byte[] Extract(ReadOnlySpan<byte> file, SdaEntry entry) {
    var header = ParseHeader(file, entry.Offset) ?? throw new InvalidDataException("C64 ARC entry header is not valid.");
    return Decode(file, entry.Offset, header).Data;
  }

  private readonly record struct Header(int Version, int Mode, int Check, int Size, int Blocks, int Type, byte[] Name, DateTime? Date, int Marker, int DataOffset);

  private static Header? ParseHeader(ReadOnlySpan<byte> file, long offset) {
    if (offset + 11 > file.Length) return null;
    var p = (int)offset;
    int version = file[p], mode = file[p + 1];
    if (version is not (1 or 2) || mode > 5 || (version == 1 && mode > 2)) return null;
    var check = file[p + 2] | file[p + 3] << 8;
    var size = file[p + 4] | file[p + 5] << 8 | file[p + 6] << 16;
    var blocks = file[p + 7] | file[p + 8] << 8;
    int type = file[p + 9], nameLength = file[p + 10];
    if (nameLength > MaxNameLength || "PSUR".IndexOf((char)type) < 0) return null;
    p += 11;
    if (p + nameLength > file.Length) return null;
    var name = file.Slice(p, nameLength).ToArray();
    p += nameLength;
    DateTime? date = null;
    if (version == 2) {
      if (p + 3 > file.Length) return null;
      date = DosDate(file[p + 1] | file[p + 2] << 8);
      p += 3;
    }
    var marker = DefaultMarker;
    if (mode == 1) {
      if (p >= file.Length) return null;
      marker = file[p++];
    }
    return new Header(version, mode, check, size, blocks, type, name, date, marker, p);
  }

  private static DateTime? DosDate(int value) {
    int day = value & 31, month = (value >> 5) & 15, year = 1980 + (value >> 9);
    return day is >= 1 and <= 31 && month is >= 1 and <= 12 && day <= DateTime.DaysInMonth(year, month) ? new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified) : null;
  }

  private static string HostName(byte[] petscii, int type) {
    var sb = new StringBuilder(petscii.Length + 4);
    foreach (var b in petscii) {
      sb.Append(b switch {
        >= 0x41 and <= 0x5A => (char)(b + 0x20),
        >= 0x61 and <= 0x7A => (char)(b - 0x20),
        >= 0xC1 and <= 0xDA => (char)(b - 0x80),
        (byte)'/' => '.',
        (byte)'\\' or (byte)':' or (byte)'*' or (byte)'?' or (byte)'"' or (byte)'<' or (byte)'>' or (byte)'|' => '_',
        >= 0x20 and < 0x7F => (char)b,
        _ => '_',
      });
    }
    sb.Append(type switch { 'P' => ".prg", 'S' => ".seq", 'U' => ".usr", _ => ".rel" });
    return sb.ToString();
  }

  // ── decoding ─────────────────────────────────────────────────────────────────

  /// <summary>Least-significant-bit-first reader that fetches a byte only when its first bit is needed.</summary>
  private sealed class Bits(byte[] data, int position) {
    private int _current;
    private int _left;

    public int Position { get; private set; } = position;

    public int Next() {
      if (this._left == 0) {
        if (this.Position >= data.Length) throw new InvalidDataException("C64 ARC entry ends early.");
        this._current = data[this.Position++];
        this._left = 8;
      }
      var bit = this._current & 1;
      this._current >>= 1;
      --this._left;
      return bit;
    }

    /// <summary>A field whose i-th bit read becomes bit i.</summary>
    public int LowFirst(int count) {
      var v = 0;
      for (var i = 0; i < count; ++i) v |= this.Next() << i;
      return v;
    }

    /// <summary>A field whose first bit read is its most significant.</summary>
    public int HighFirst(int count) {
      var v = 0;
      for (var i = 0; i < count; ++i) v = (v << 1) | this.Next();
      return v;
    }

    public int Byte() {
      if (this.Position >= data.Length) throw new InvalidDataException("C64 ARC entry ends early.");
      return data[this.Position++];
    }
  }

  private sealed record Decoded(byte[] Data, long StoredSize);

  private static Decoded Decode(ReadOnlySpan<byte> file, long offset, Header h) {
    var bytes = file.ToArray();
    var bits = new Bits(bytes, h.DataOffset);
    var expected = h.Size;
    var check = h.Check;
    IEnumerator<int> source = h.Mode switch {
      0 or 1 => Raw(bits).GetEnumerator(),
      2 or 4 => Huffman(bits).GetEnumerator(),
      _ => Lzw(bits, h.Mode == 5, (c, s) => { check = c; expected = s; }).GetEnumerator(),
    };
    var runLength = h.Mode is 1 or 3 or 4 or 5;
    var limit = h.Mode == 5 ? MaxSize : h.Size;
    var output = new MemoryStream(Math.Min(limit, 1 << 20));
    while (output.Length < limit && source.MoveNext()) {
      var b = source.Current;
      if (runLength && b == h.Marker) {
        if (!source.MoveNext()) break;
        var count = source.Current;
        if (!source.MoveNext()) break;
        var value = source.Current;
        if (count == 0) count = h.Version == 1 ? 255 : 256;
        for (var i = 0; i < count; ++i) output.WriteByte((byte)value);
      } else output.WriteByte((byte)b);
    }

    var data = output.ToArray();
    if (data.Length > expected) data = data.AsSpan(0, expected).ToArray();
    if (data.Length != expected) throw new InvalidDataException($"C64 ARC entry decodes to {data.Length} of {expected} bytes.");
    if ((Checksum(data, h.Version) ^ check) != 0) throw new InvalidDataException("C64 ARC entry fails its checksum.");

    long stored = h.Blocks * (long)BlockPayload;
    if (h.Mode == 5) {
      long consumed = bits.Position - offset;
      stored = (consumed + BlockPayload - 1) / BlockPayload * BlockPayload;
    }
    return new Decoded(data, stored);
  }

  /// <summary>The 16-bit checksum: a plain byte sum (version 1) or the sum of each byte XOR a running 8-bit counter from 1 (version 2).</summary>
  internal static int Checksum(ReadOnlySpan<byte> data, int version) {
    var sum = 0;
    byte counter = 0;
    foreach (var b in data) sum += version == 1 ? b : b ^ ++counter;
    return sum & 0xFFFF;
  }

  private static IEnumerable<int> Raw(Bits bits) {
    for (;;) yield return bits.Byte();
  }

  private static IEnumerable<int> Huffman(Bits bits) {
    var codes = new Dictionary<(int Length, int Code), int>();
    for (var symbol = 0; symbol < 256; ++symbol) {
      var length = bits.LowFirst(5);
      if (length > 24) throw new InvalidDataException("C64 ARC Huffman code is too long.");
      if (length == 0) continue;
      codes.TryAdd((length, bits.LowFirst(length)), symbol);
    }
    if (codes.Count == 0) throw new InvalidDataException("C64 ARC Huffman table is empty.");
    for (;;) {
      int value = 0, length = 0, symbol;
      do {
        value |= bits.Next() << length;
        if (++length > 24) throw new InvalidDataException("C64 ARC Huffman code is not in the table.");
      } while (!codes.TryGetValue((length, value), out symbol));
      yield return symbol;
    }
  }

  private static IEnumerable<int> Lzw(Bits bits, bool onePass, Action<int, int> trailer) {
    const int EndCode = 256, FirstFree = 258, TableSize = 4096;
    var prefix = new int[TableSize];
    var suffix = new byte[TableSize];
    var stack = new byte[TableSize];
    var next = FirstFree;
    var width = 9;
    var period = 256;
    var countdown = 254;

    int ReadCode() {
      var code = bits.HighFirst(width);
      if (code == EndCode && onePass) {
        var check = bits.HighFirst(16);
        var size = bits.HighFirst(24);
        bits.HighFirst(16);
        trailer(check, size);
      }
      if (width < 12 && --countdown == 0) {
        period <<= 1;
        ++width;
        countdown = period;
      }
      return code;
    }

    var previous = ReadCode();
    if (previous == EndCode) yield break;
    if (previous > 255) throw new InvalidDataException("C64 ARC LZW stream starts with an undefined code.");
    var first = (byte)previous;
    yield return first;

    for (;;) {
      var code = ReadCode();
      if (code == EndCode) yield break;
      var depth = 0;
      int current;
      if (code >= next) {
        if (code > next) throw new InvalidDataException("C64 ARC LZW code is undefined.");
        stack[depth++] = first;
        current = previous;
      } else current = code;
      while (current > 255) {
        if (depth >= stack.Length) throw new InvalidDataException("C64 ARC LZW string is too long.");
        stack[depth++] = suffix[current];
        current = prefix[current];
      }
      first = (byte)current;
      yield return first;
      while (depth > 0) yield return stack[--depth];
      if (next < TableSize) {
        prefix[next] = previous;
        suffix[next] = first;
        ++next;
      }
      previous = code;
    }
  }
}
