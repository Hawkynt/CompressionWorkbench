using System.Buffers.Binary;
using System.Text;

namespace FileFormat.DxArchive;

/// <summary>One file of a DxLib archive.</summary>
/// <param name="Path">'/'-separated path.</param>
/// <param name="Size">Expanded size.</param>
/// <param name="StoredSize">Bytes the file occupies in the archive.</param>
/// <param name="Modified">Last-write time, when recorded.</param>
public sealed record DxArchiveEntry(string Path, long Size, long StoredSize, DateTime? Modified) {
  internal long DataOffset { get; init; }
  internal long LzSize { get; init; } = -1;
  internal long HuffmanSize { get; init; } = -1;
  internal byte[]? Key { get; init; }
}

/// <summary>
/// DxLib archives (<c>.dxa</c>, and WOLF RPG Editor <c>.wolf</c> data archives), versions 6 and
/// 8, after docs/DXA-ON-DISK.md: XOR-keyed index and file data, LZ- and Huffman-coded files.
/// </summary>
/// <remarks>
/// Version 6 is converted from GARbro's DxLib reader (morkt, MIT; see THIRD-PARTY-NOTICE.GARbro.txt);
/// version 8 is implemented from the document, written from the DxLib archiver source.
/// </remarks>
public sealed class DxArchiveReader {
  /// <summary>The bytes the archiver uses when no password is given (version 8).</summary>
  public static ReadOnlySpan<byte> DefaultPassword => "DXBDXARC"u8;

  private const ulong None = ulong.MaxValue;
  private readonly byte[] _data;

  private DxArchiveReader(byte[] data, int version, List<DxArchiveEntry> entries) {
    this._data = data;
    this.Version = version;
    this.Entries = entries;
  }

  /// <summary>The archive version (6 or 8).</summary>
  public int Version { get; }

  /// <summary>The files, depth first in index order.</summary>
  public IReadOnlyList<DxArchiveEntry> Entries { get; }

  /// <summary>Whether the bytes start like a DxLib archive of a version this reader handles.</summary>
  public static bool LooksLikeDxa(ReadOnlySpan<byte> head)
    => head.Length >= 4 && head[0] == 'D' && head[1] == 'X' && head[2] == 8 && head[3] == 0;

  /// <summary>Opens an archive; <paramref name="password"/> is the archive password (null: the archiver's default, or a key recovered from a version 6 header).</summary>
  /// <exception cref="InvalidDataException">Not a DxLib archive, or the password does not fit.</exception>
  /// <exception cref="NotSupportedException">A version other than 6 and 8.</exception>
  public static DxArchiveReader Open(byte[] data, string? password = null) {
    ArgumentNullException.ThrowIfNull(data);
    if (data.Length < 0x30)
      throw new InvalidDataException("DXA: too short for a header.");
    if (data[0] == 'D' && data[1] == 'X') {
      var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
      if (version == 8)
        return OpenV8(data, password);
      throw new NotSupportedException($"DXA: version {version} archives are not supported (versions 6 and 8 are).");
    }
    return OpenV6(data, password);
  }

  #region Version 8

  /// <summary>The 7-byte key of a byte string (see "Keys").</summary>
  internal static byte[] KeyV8(ReadOnlySpan<byte> source) {
    byte[] s = source.Length < 4 ? [.. source, .. DefaultPassword] : source.ToArray();
    var even = new byte[(s.Length + 1) / 2];
    var odd = new byte[s.Length / 2];
    for (var i = 0; i < s.Length; ++i)
      if ((i & 1) == 0)
        even[i / 2] = s[i];
      else
        odd[i / 2] = s[i];
    var c0 = Compression.Core.Checksums.Crc32.Compute(even);
    var c1 = Compression.Core.Checksums.Crc32.Compute(odd);
    return [(byte)c0, (byte)(c0 >> 8), (byte)(c0 >> 16), (byte)(c0 >> 24), (byte)c1, (byte)(c1 >> 8), (byte)(c1 >> 16)];
  }

  private static void Xor(Span<byte> data, long position, byte[]? key) {
    if (key == null)
      return;
    var j = (int)(position % key.Length);
    for (var i = 0; i < data.Length; ++i) {
      data[i] ^= key[j];
      if (++j == key.Length)
        j = 0;
    }
  }

  private static DxArchiveReader OpenV8(byte[] data, string? password) {
    var h = data.AsSpan();
    long indexSize = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
    var dataStart = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[8..]);
    var indexOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[16..]);
    var fileTable = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[24..]);
    var dirTable = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[32..]);
    var codePage = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[40..]);
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(h[44..]);
    var huffmanKb = h[48];
    if (indexOffset < 0x40 || indexOffset > data.Length || dataStart > data.Length || indexSize > int.MaxValue)
      throw new InvalidDataException("DXA: the header points outside the archive.");

    var noKey = (flags & 1) != 0;
    var passwordBytes = password == null ? DefaultPassword.ToArray() : Encoding.Latin1.GetBytes(password);
    if (passwordBytes.Length > 63)
      passwordBytes = passwordBytes[..63];
    var key = noKey ? null : KeyV8(passwordBytes);

    byte[] index;
    var raw = data.AsSpan((int)indexOffset).ToArray();
    Xor(raw, 0, key);
    if ((flags & 2) != 0)
      index = raw.Length >= indexSize ? raw[..(int)indexSize] : throw new InvalidDataException("DXA: truncated index.");
    else {
      byte[] lz;
      try {
        lz = DxCodecs.HuffmanDecode(raw);
      } catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) {
        throw new InvalidDataException("DXA: the index does not decode; the password is probably wrong.", e);
      }
      index = DxCodecs.LzDecode(lz);
    }
    if (index.Length < indexSize || fileTable >= index.Length || dirTable >= index.Length)
      throw new InvalidDataException("DXA: the index does not decode; the password is probably wrong.");

    var unit = codePage switch { 1200 or 1201 => 2, 32766 or 32767 => 4, _ => 1 };
    var encoding = EncodingFor(codePage);
    var entries = new List<DxArchiveEntry>();

    byte[] Upper(long nameOffset) {
      var at = (int)nameOffset + 4;
      var end = at;
      while (end + unit <= index.Length && index.AsSpan(end, unit).ContainsAnyExcept((byte)0))
        end += unit;
      return index.AsSpan(at, end - at).ToArray();
    }

    string Original(long nameOffset) {
      var packs = BinaryPrimitives.ReadUInt16LittleEndian(index.AsSpan((int)nameOffset));
      var at = (int)nameOffset + 4 + packs * 4;
      var end = at;
      while (end + unit <= index.Length && index.AsSpan(end, unit).ContainsAnyExcept((byte)0))
        end += unit;
      return encoding.GetString(index, at, end - at);
    }

    void Walk(long dirOffset, string prefix, List<byte[]> dirNames, int depth) {
      if (depth > 256 || dirOffset < 0 || dirTable + dirOffset + 32 > index.Length)
        throw new InvalidDataException("DXA: a directory entry lies outside the index.");
      var d = index.AsSpan((int)(dirTable + dirOffset), 32);
      var count = (long)BinaryPrimitives.ReadUInt64LittleEndian(d[16..]);
      var first = (long)BinaryPrimitives.ReadUInt64LittleEndian(d[24..]);
      if (count < 0 || count > index.Length / 72 || fileTable + first + count * 72 > index.Length)
        throw new InvalidDataException("DXA: a directory's entries lie outside the index.");
      for (var i = 0L; i < count; ++i) {
        var f = index.AsSpan((int)(fileTable + first + i * 72), 72);
        var nameOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(f);
        var attributes = BinaryPrimitives.ReadUInt64LittleEndian(f[8..]);
        var written = (long)BinaryPrimitives.ReadUInt64LittleEndian(f[32..]);
        var dataAddress = (long)BinaryPrimitives.ReadUInt64LittleEndian(f[40..]);
        var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(f[48..]);
        var lz = BinaryPrimitives.ReadUInt64LittleEndian(f[56..]);
        var huffman = BinaryPrimitives.ReadUInt64LittleEndian(f[64..]);
        if (nameOffset < 0 || nameOffset + 4 > index.Length)
          throw new InvalidDataException("DXA: a name lies outside the index.");
        var name = Original(nameOffset);
        if ((attributes & 0x10) != 0) {
          Walk(dataAddress, prefix + name + "/", [Upper(nameOffset), .. dirNames], depth + 1);
          continue;
        }
        byte[]? fileKey = null;
        if (!noKey) {
          // Password, then the file's upper-case name, then its directories from the innermost out.
          var s = new List<byte>(passwordBytes);
          s.AddRange(Upper(nameOffset));
          foreach (var dir in dirNames)
            s.AddRange(dir);
          fileKey = KeyV8(s.ToArray());
        }
        var stored = huffman != None ? (lz != None && huffmanKb != 0xFF && (long)lz > huffmanKb * 2048L ? (long)huffman + (long)lz - huffmanKb * 2048L
            : lz == None && huffmanKb != 0xFF && size > huffmanKb * 2048L ? (long)huffman + size - huffmanKb * 2048L : (long)huffman)
          : lz != None ? (long)lz : size;
        entries.Add(new DxArchiveEntry(prefix + name, size, stored, FileTime(written)) {
          DataOffset = dataStart + dataAddress,
          LzSize = lz == None ? -1 : (long)lz,
          HuffmanSize = huffman == None ? -1 : (long)huffman,
          Key = fileKey,
        });
      }
    }

    Walk(0, "", [], 0);
    return new DxArchiveReader(data, 8, entries) { HuffmanKb = huffmanKb };
  }

  private int HuffmanKb { get; init; }

  private byte[] ExtractV8(DxArchiveEntry e) {
    var size = e.Size;
    if (e.HuffmanSize < 0 && e.LzSize < 0) {
      var stored = Slice(e.DataOffset, size);
      Xor(stored, size, e.Key);
      return stored;
    }
    if (e.HuffmanSize < 0) {
      var lz = Slice(e.DataOffset, e.LzSize);
      Xor(lz, size, e.Key);
      return Checked(DxCodecs.LzDecode(lz), size);
    }
    var coded = Slice(e.DataOffset, e.HuffmanSize);
    Xor(coded, size, e.Key);
    var decoded = DxCodecs.HuffmanDecode(coded);
    var whole = e.LzSize >= 0 ? e.LzSize : size;
    var span = this.HuffmanKb * 1024L;
    if (this.HuffmanKb != 0xFF && whole > span * 2) {
      // Only the first and last span KiB were Huffman-coded; the middle follows in the file.
      if (decoded.Length < span * 2)
        throw new InvalidDataException($"DXA: '{e.Path}' has too little Huffman output.");
      var full = new byte[whole];
      decoded.AsSpan(0, (int)span).CopyTo(full);
      decoded.AsSpan((int)span, (int)span).CopyTo(full.AsSpan((int)(whole - span)));
      var middle = Slice(e.DataOffset + e.HuffmanSize, whole - span * 2);
      Xor(middle, size + e.HuffmanSize, e.Key);
      middle.CopyTo(full, span);
      decoded = full;
    }
    if (decoded.Length < whole)
      throw new InvalidDataException($"DXA: '{e.Path}' decodes short.");
    return e.LzSize >= 0 ? Checked(DxCodecs.LzDecode(decoded[..(int)whole]), size) : decoded[..(int)size];
  }

  #endregion

  #region Version 6

  /// <summary>The 12-byte key of a version 6 password (see "Version 6").</summary>
  internal static byte[] KeyV6(string? password) {
    var key = new byte[12];
    if (string.IsNullOrEmpty(password))
      key.AsSpan().Fill(0xAA);
    else {
      var bytes = ShiftJis.GetBytes(password);
      var n = Math.Min(bytes.Length, 12);
      bytes.AsSpan(0, n).CopyTo(key);
      for (var i = n; i < 12; ++i)
        key[i] = key[i - n];
    }
    key[0] ^= 0xFF;
    key[1] = byte.RotateRight(key[1], 4);
    key[2] ^= 0x8A;
    key[3] = (byte)~byte.RotateRight(key[3], 4);
    key[4] ^= 0xFF;
    key[5] ^= 0xAC;
    key[6] ^= 0xFF;
    key[7] = (byte)~byte.RotateRight(key[7], 3);
    key[8] = byte.RotateLeft(key[8], 3);
    key[9] ^= 0x7F;
    key[10] = (byte)(byte.RotateRight(key[10], 4) ^ 0xD6);
    key[11] ^= 0xCC;
    return key;
  }

  /// <summary>Recovers the key from the header's known plaintext.</summary>
  private static byte[]? GuessKeyV6(byte[] data) {
    var header = data.AsSpan(0, 0x30).ToArray();
    header[0] ^= (byte)'D';
    header[1] ^= (byte)'X';
    header[2] ^= 6;
    var key0 = BinaryPrimitives.ReadUInt32LittleEndian(header);
    header[8] ^= 0x30;
    if ((BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) ^ key0) != 0)
      return null;
    var key2 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
    var key1 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x1C));
    var indexOffset = (long)(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x10)) ^ key1 ^ ((ulong)key2 << 32));
    if (indexOffset <= 0x30 || indexOffset >= data.Length)
      return null;
    var key = new byte[12];
    BinaryPrimitives.WriteUInt32LittleEndian(key, key0);
    BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(4), key1);
    BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(8), key2);
    return key;
  }

  private static DxArchiveReader OpenV6(byte[] data, string? password) {
    List<byte[]> keys = [KeyV6(password)];
    if (password == null && GuessKeyV6(data) is { } guessed)
      keys.Add(guessed);
    foreach (var key in keys) {
      var header = data.AsSpan(0, 0x30).ToArray();
      Xor(header, 0, key);
      if (header[0] != 'D' || header[1] != 'X' || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) != 6)
        continue;
      try {
        return ReadV6(data, header, key);
      } catch (InvalidDataException) when (key != keys[^1]) {
        // try the next key
      }
    }
    throw new InvalidDataException("Not a DxLib archive of version 6 or 8 (or the password does not fit).");
  }

  private static DxArchiveReader ReadV6(byte[] data, byte[] header, byte[] key) {
    long indexSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
    var dataStart = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
    var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x10));
    var fileTable = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x18));
    var dirTable = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x20));
    var codePage = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x28));
    if (indexOffset < 0x30 || indexOffset + indexSize > data.Length || fileTable >= indexSize || dirTable >= indexSize || dataStart < 0 || dataStart > data.Length)
      throw new InvalidDataException("DXA: the version 6 header points outside the archive.");
    var index = data.AsSpan((int)indexOffset, (int)indexSize).ToArray();
    Xor(index, 0, key);
    var encoding = EncodingFor(codePage);
    var entries = new List<DxArchiveEntry>();

    string Name(long nameOffset) {
      if (nameOffset < 0 || nameOffset + 4 > index.Length)
        throw new InvalidDataException("DXA: a name lies outside the index.");
      var at = (int)nameOffset + 4 + BinaryPrimitives.ReadUInt16LittleEndian(index.AsSpan((int)nameOffset)) * 4;
      var end = at;
      while (end < index.Length && index[end] != 0)
        ++end;
      return encoding.GetString(index, at, end - at);
    }

    void Walk(long dirOffset, string prefix, int depth) {
      if (depth > 256 || dirOffset < 0 || dirTable + dirOffset + 32 > index.Length)
        throw new InvalidDataException("DXA: a directory entry lies outside the index.");
      var d = index.AsSpan((int)(dirTable + dirOffset), 32);
      var count = BinaryPrimitives.ReadInt64LittleEndian(d[16..]);
      var first = BinaryPrimitives.ReadInt64LittleEndian(d[24..]);
      if (count < 0 || fileTable + first + count * 64 > index.Length)
        throw new InvalidDataException("DXA: a directory's entries lie outside the index.");
      for (var i = 0L; i < count; ++i) {
        var f = index.AsSpan((int)(fileTable + first + i * 64), 64);
        var name = Name(BinaryPrimitives.ReadInt64LittleEndian(f));
        var attributes = BinaryPrimitives.ReadUInt64LittleEndian(f[8..]);
        var written = BinaryPrimitives.ReadInt64LittleEndian(f[32..]);
        var offset = BinaryPrimitives.ReadInt64LittleEndian(f[40..]);
        if ((attributes & 0x10) != 0) {
          if (offset == 0 || offset == dirOffset)
            throw new InvalidDataException("DXA: a directory refers to itself.");
          Walk(offset, prefix + name + "/", depth + 1);
          continue;
        }
        var size = BinaryPrimitives.ReadInt64LittleEndian(f[48..]);
        var packed = BinaryPrimitives.ReadInt64LittleEndian(f[56..]);
        entries.Add(new DxArchiveEntry(prefix + name, size, packed == -1 ? size : packed, FileTime(written)) {
          DataOffset = dataStart + offset,
          LzSize = packed,
          Key = key,
        });
      }
    }

    Walk(0, "", 0);
    return new DxArchiveReader(data, 6, entries);
  }

  private byte[] ExtractV6(DxArchiveEntry e) {
    if (e.LzSize < 0) {
      var stored = Slice(e.DataOffset, e.Size);
      Xor(stored, e.Size, e.Key);
      return stored;
    }
    var lz = Slice(e.DataOffset, e.LzSize);
    Xor(lz, e.Size, e.Key);
    return Checked(DxCodecs.LzDecode(lz), e.Size);
  }

  #endregion

  /// <summary>The expanded bytes of <paramref name="entry"/>.</summary>
  /// <exception cref="InvalidDataException">The data is damaged.</exception>
  public byte[] Extract(DxArchiveEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0)
      return [];
    return this.Version == 8 ? this.ExtractV8(entry) : this.ExtractV6(entry);
  }

  private byte[] Slice(long offset, long length) {
    if (offset < 0 || length < 0 || offset + length > this._data.Length)
      throw new InvalidDataException("DXA: file data lies outside the archive.");
    return this._data.AsSpan((int)offset, (int)length).ToArray();
  }

  private static byte[] Checked(byte[] data, long size) => data.Length == size ? data
    : throw new InvalidDataException($"DXA: a file expands to {data.Length} bytes instead of {size}.");

  private static DateTime? FileTime(long value) => value > 0 && value < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(value) : null;

  private static readonly Encoding ShiftJis = Pages(932);

  private static Encoding Pages(int codePage) {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    return Encoding.GetEncoding(codePage);
  }

  private static Encoding EncodingFor(int codePage) => codePage switch {
    65001 => Encoding.UTF8,
    1200 => Encoding.Unicode,
    1201 => Encoding.BigEndianUnicode,
    32765 => Encoding.ASCII,
    32766 => Encoding.UTF32,
    0 => ShiftJis,
    _ => TryPage(codePage),
  };

  private static Encoding TryPage(int codePage) {
    try {
      return Pages(codePage);
    } catch (Exception e) when (e is ArgumentException or NotSupportedException) {
      return ShiftJis;
    }
  }
}
