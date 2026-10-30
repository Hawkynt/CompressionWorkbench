using System.Buffers.Binary;
using System.Text;

namespace FileFormat.WebArchive;

/// <summary>An NSKeyedArchiver object reference (<c>0x8n</c> marker).</summary>
public readonly record struct PlistUid(ulong Value);

/// <summary>A set (<c>0xCn</c> marker), kept apart from arrays so a rewrite writes it back as a set.</summary>
public sealed record PlistSet(List<object?> Items);

/// <summary>
/// Apple binary property lists (<c>bplist00</c>). Values map to <c>null</c>, <see cref="bool"/>,
/// <see cref="long"/>, <see cref="Int128"/> (the 16-byte integers), <see cref="double"/>,
/// <see cref="DateTime"/> (UTC), <c>byte[]</c>, <see cref="string"/>, <see cref="PlistUid"/>,
/// <c>List&lt;object?&gt;</c>, <see cref="PlistSet"/> and <c>OrderedDictionary&lt;string, object?&gt;</c>
/// (key order is kept, so a rewrite preserves it).
/// </summary>
/// <remarks>
/// Layout per Apple's CoreFoundation <c>CFBinaryPList.c</c> format description: the 8-byte header,
/// the objects, an offset table, and a 32-byte trailer (6 unused bytes, sort version, offset width,
/// object-reference width, object count, top object, offset-table offset; all big-endian). The writer
/// numbers objects in the order CPython's <c>plistlib</c> does (depth-first, a dictionary's keys before
/// its values, equal scalars shared) and picks the same field widths, so for the same value tree it
/// produces the bytes <c>plistlib.dumps(fmt=FMT_BINARY, sort_keys=False)</c> produces — see
/// THIRD-PARTY-NOTICE.CPython-plistlib.txt.
/// </remarks>
public static class BinaryPlist {

  private static readonly DateTime Epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>Whether <paramref name="data"/> starts with the <c>bplist00</c> header.</summary>
  public static bool IsBinaryPlist(ReadOnlySpan<byte> data) => data.StartsWith("bplist00"u8);

  // ── reading ──────────────────────────────────────────────────────────

  public static object? Read(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    if (data.Length < 8 + 32 || !IsBinaryPlist(data))
      throw new InvalidDataException("Not a binary property list (no bplist00 header).");
    var trailer = data.AsSpan(data.Length - 32);
    int offsetSize = trailer[6], refSize = trailer[7];
    var count = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
    var top = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
    var tableOffset = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
    if (offsetSize is not (1 or 2 or 4 or 8) || refSize is not (1 or 2 or 4 or 8))
      throw new InvalidDataException("Binary plist trailer declares an invalid field width.");
    if (count == 0 || top >= count || tableOffset < 8 || tableOffset + count * (ulong)offsetSize > (ulong)data.Length - 32)
      throw new InvalidDataException("Binary plist trailer points outside the file.");
    var offsets = new long[count];
    for (var i = 0UL; i < count; ++i) {
      var o = ReadUnsigned(data.AsSpan((int)(tableOffset + i * (ulong)offsetSize), offsetSize));
      if (o < 8 || o >= tableOffset)
        throw new InvalidDataException($"Binary plist object {i} lies outside the object area.");
      offsets[i] = (long)o;
    }
    return new Reader(data, offsets, refSize, (long)tableOffset).Object((long)top, 0);
  }

  private sealed class Reader(byte[] data, long[] offsets, int refSize, long objectsEnd) {

    private readonly HashSet<long> _active = [];

    public object? Object(long index, int depth) {
      if ((ulong)index >= (ulong)offsets.Length)
        throw new InvalidDataException($"Binary plist object reference {index} is out of range.");
      if (depth > 512 || !this._active.Add(index))
        throw new InvalidDataException("Binary plist contains a reference cycle.");
      try {
        return this.Parse(offsets[index], depth);
      } finally {
        this._active.Remove(index);
      }
    }

    private object? Parse(long pos, int depth) {
      var marker = data[pos];
      int high = marker >> 4, low = marker & 0xF;
      ++pos;
      switch (high) {
        case 0x0:
          return low switch {
            0x0 => null,
            0x8 => false,
            0x9 => true,
            _ => throw new InvalidDataException($"Unsupported binary plist marker 0x{marker:X2}."),
          };
        case 0x1: {
          var size = 1 << low;
          var bytes = this.Slice(pos, size);
          return size switch {
            1 or 2 or 4 => (object)(long)ReadUnsigned(bytes),
            8 => (object)BinaryPrimitives.ReadInt64BigEndian(bytes),
            16 => (object)BinaryPrimitives.ReadInt128BigEndian(bytes),
            _ => throw new InvalidDataException($"Unsupported binary plist integer width {size}."),
          };
        }
        case 0x2:
          return low switch {
            2 => (double)BinaryPrimitives.ReadSingleBigEndian(this.Slice(pos, 4)),
            3 => BinaryPrimitives.ReadDoubleBigEndian(this.Slice(pos, 8)),
            _ => throw new InvalidDataException($"Unsupported binary plist real width {1 << low}."),
          };
        case 0x3 when low == 3:
          return Epoch.AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(this.Slice(pos, 8)));
        case 0x4: {
          var n = this.Count(low, ref pos);
          return this.Slice(pos, n).ToArray();
        }
        case 0x5: {
          var n = this.Count(low, ref pos);
          return Encoding.ASCII.GetString(this.Slice(pos, n));
        }
        case 0x6: {
          var n = this.Count(low, ref pos);
          return Encoding.BigEndianUnicode.GetString(this.Slice(pos, checked(n * 2)));
        }
        case 0x8:
          return new PlistUid(ReadUnsigned(this.Slice(pos, low + 1)));
        case 0xA:
        case 0xC: {
          var n = this.Count(low, ref pos);
          var refs = this.Slice(pos, checked(n * refSize));
          var items = new List<object?>(n);
          for (var i = 0; i < n; ++i)
            items.Add(this.Object((long)ReadUnsigned(refs.Slice(i * refSize, refSize)), depth + 1));
          return high == 0xA ? items : new PlistSet(items);
        }
        case 0xD: {
          var n = this.Count(low, ref pos);
          var refs = this.Slice(pos, checked(n * 2 * refSize));
          var dict = new OrderedDictionary<string, object?>(n);
          for (var i = 0; i < n; ++i) {
            if (this.Object((long)ReadUnsigned(refs.Slice(i * refSize, refSize)), depth + 1) is not string key)
              throw new InvalidDataException("Binary plist dictionary key is not a string.");
            var value = this.Object((long)ReadUnsigned(refs.Slice((n + i) * refSize, refSize)), depth + 1);
            if (!dict.TryAdd(key, value))
              throw new InvalidDataException($"Binary plist dictionary repeats the key '{key}'.");
          }
          return dict;
        }
        default:
          throw new InvalidDataException($"Unsupported binary plist marker 0x{marker:X2}.");
      }
    }

    private int Count(int low, ref long pos) {
      if (low != 0xF) return low;
      var marker = data[pos];
      if (marker >> 4 != 0x1 || (marker & 0xF) > 3)
        throw new InvalidDataException("Binary plist object length is not an integer.");
      var size = 1 << (marker & 0xF);
      var n = ReadUnsigned(this.Slice(pos + 1, size));
      pos += 1 + size;
      if (n > int.MaxValue) throw new InvalidDataException("Binary plist object is too long.");
      return (int)n;
    }

    private ReadOnlySpan<byte> Slice(long pos, int length) {
      if (length < 0 || pos + length > objectsEnd)
        throw new InvalidDataException("Binary plist object runs past the object area.");
      return data.AsSpan((int)pos, length);
    }
  }

  private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes) {
    var v = 0UL;
    foreach (var b in bytes) v = v << 8 | b;
    return v;
  }

  // ── writing ──────────────────────────────────────────────────────────

  public static byte[] Write(object? root) {
    var objects = new List<object?>();
    var scalarIds = new Dictionary<(Type, object), int>();
    var containerIds = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
    var nullId = -1;
    Flatten(root);

    var refSize = SizeFor((ulong)objects.Count);
    using var ms = new MemoryStream();
    ms.Write("bplist00"u8);
    var offsets = new long[objects.Count];
    for (var i = 0; i < objects.Count; ++i) {
      offsets[i] = ms.Position;
      WriteObject(ms, objects[i]);
    }
    var tableOffset = ms.Position;
    var offsetSize = SizeFor((ulong)tableOffset);
    foreach (var o in offsets) WriteUnsigned(ms, (ulong)o, offsetSize);
    Span<byte> trailer = stackalloc byte[32];
    trailer.Clear();
    trailer[6] = (byte)offsetSize;
    trailer[7] = (byte)refSize;
    BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)objects.Count);
    BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0);
    BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)tableOffset);
    ms.Write(trailer);
    return ms.ToArray();

    int Id(object? value) => value switch {
      null => nullId,
      List<object?> or PlistSet or OrderedDictionary<string, object?> => containerIds[value],
      _ => scalarIds[ScalarKey(value)],
    };

    void Flatten(object? value) {
      switch (value) {
        case null:
          if (nullId >= 0) return;
          nullId = objects.Count;
          objects.Add(null);
          return;
        case List<object?> or PlistSet or OrderedDictionary<string, object?>:
          if (!containerIds.TryAdd(value, objects.Count)) return;
          break;
        default:
          if (!scalarIds.TryAdd(ScalarKey(value), objects.Count)) return;
          break;
      }
      objects.Add(value);
      switch (value) {
        case OrderedDictionary<string, object?> dict:
          foreach (var k in dict.Keys) Flatten(k);
          foreach (var v in dict.Values) Flatten(v);
          break;
        case List<object?> list:
          foreach (var v in list) Flatten(v);
          break;
        case PlistSet set:
          foreach (var v in set.Items) Flatten(v);
          break;
      }
    }

    void WriteObject(Stream s, object? value) {
      switch (value) {
        case null: s.WriteByte(0x00); break;
        case false: s.WriteByte(0x08); break;
        case true: s.WriteByte(0x09); break;
        case long l: WriteInteger(s, l); break;
        case int i: WriteInteger(s, i); break;
        case Int128 big:
          if (big >= long.MinValue && big <= long.MaxValue) { WriteInteger(s, (long)big); break; }
          if (big < 0 || big > ulong.MaxValue) throw new ArgumentOutOfRangeException(nameof(root), "Binary plist integers above 2^64 cannot be written.");
          s.WriteByte(0x14);
          Span<byte> b16 = stackalloc byte[16];
          BinaryPrimitives.WriteInt128BigEndian(b16, big);
          s.Write(b16);
          break;
        case double d: s.WriteByte(0x23); WriteDouble(s, d); break;
        case DateTime t: s.WriteByte(0x33); WriteDouble(s, (t.ToUniversalTime() - Epoch).TotalSeconds); break;
        case byte[] bytes: WriteSize(s, 0x40, bytes.Length); s.Write(bytes); break;
        case string str:
          if (str.All(c => c < 0x80)) {
            WriteSize(s, 0x50, str.Length);
            s.Write(Encoding.ASCII.GetBytes(str));
          } else {
            var utf16 = Encoding.BigEndianUnicode.GetBytes(str);
            WriteSize(s, 0x60, utf16.Length / 2);
            s.Write(utf16);
          }
          break;
        case PlistUid uid: {
          var n = uid.Value < 1UL << 8 ? 1 : uid.Value < 1UL << 16 ? 2 : uid.Value < 1UL << 32 ? 4 : 8;
          s.WriteByte((byte)(0x80 | (n - 1)));
          WriteUnsigned(s, uid.Value, n);
          break;
        }
        case List<object?> list:
          WriteSize(s, 0xA0, list.Count);
          foreach (var v in list) WriteUnsigned(s, (ulong)Id(v), refSize);
          break;
        case PlistSet set:
          WriteSize(s, 0xC0, set.Items.Count);
          foreach (var v in set.Items) WriteUnsigned(s, (ulong)Id(v), refSize);
          break;
        case OrderedDictionary<string, object?> dict:
          WriteSize(s, 0xD0, dict.Count);
          foreach (var k in dict.Keys) WriteUnsigned(s, (ulong)Id(k), refSize);
          foreach (var v in dict.Values) WriteUnsigned(s, (ulong)Id(v), refSize);
          break;
        default:
          throw new ArgumentException($"Binary plists cannot hold a {value.GetType().Name}.", nameof(root));
      }
    }
  }

  /// <summary>Equal scalars share one object; booleans and integers stay apart (True is not 1).</summary>
  private static (Type, object) ScalarKey(object value) => value switch {
    int i => (typeof(long), (long)i),
    byte[] b => (typeof(byte[]), Convert.ToBase64String(b)),
    DateTime t => (typeof(DateTime), t.ToUniversalTime()),
    _ => (value.GetType(), value),
  };

  private static int SizeFor(ulong count) => count < 1UL << 8 ? 1 : count < 1UL << 16 ? 2 : count < 1UL << 32 ? 4 : 8;

  private static void WriteInteger(Stream s, long v) {
    if (v < 0) { s.WriteByte(0x13); WriteUnsigned(s, (ulong)v, 8); }
    else if (v < 1L << 8) { s.WriteByte(0x10); s.WriteByte((byte)v); }
    else if (v < 1L << 16) { s.WriteByte(0x11); WriteUnsigned(s, (ulong)v, 2); }
    else if (v < 1L << 32) { s.WriteByte(0x12); WriteUnsigned(s, (ulong)v, 4); }
    else { s.WriteByte(0x13); WriteUnsigned(s, (ulong)v, 8); }
  }

  private static void WriteSize(Stream s, int token, int size) {
    if (size < 15) { s.WriteByte((byte)(token | size)); return; }
    s.WriteByte((byte)(token | 0xF));
    WriteInteger(s, size);
  }

  private static void WriteDouble(Stream s, double d) {
    Span<byte> b = stackalloc byte[8];
    BinaryPrimitives.WriteDoubleBigEndian(b, d);
    s.Write(b);
  }

  private static void WriteUnsigned(Stream s, ulong v, int width) {
    for (var i = width - 1; i >= 0; --i) s.WriteByte((byte)(v >> (8 * i)));
  }
}
