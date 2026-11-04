using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Sqlar;

/// <summary>
/// A minimal reader of the SQLite 3 file format (https://www.sqlite.org/fileformat2.html): the
/// database header, the schema table, and table b-trees walked in rowid order with overflow chains
/// followed. Records decode to <see cref="object"/> values: <see langword="null"/>, <see cref="long"/>,
/// <see cref="double"/>, <see cref="string"/> (UTF-8 databases) or <see cref="byte"/>[].
/// </summary>
public sealed class SqliteDatabase {

  /// <summary>The 16-byte header string every SQLite 3 database starts with.</summary>
  public static ReadOnlySpan<byte> Magic => "SQLite format 3\0"u8;

  private readonly byte[] _data;

  /// <summary>A row of the schema table.</summary>
  public sealed record SchemaEntry(string Type, string Name, string TableName, long RootPage, string? Sql);

  /// <summary>A table row: its rowid and column values.</summary>
  public sealed record Row(long RowId, object?[] Values);

  public SqliteDatabase(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    this._data = data;
    if (data.Length < 100 || !data.AsSpan(0, 16).SequenceEqual(Magic))
      throw new InvalidDataException("Not an SQLite 3 database (missing header string).");
    var raw = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(16));
    this.PageSize = raw == 1 ? 65536 : raw;
    if (this.PageSize < 512 || (this.PageSize & (this.PageSize - 1)) != 0)
      throw new InvalidDataException($"SQLite page size {this.PageSize} is invalid.");
    this.ReservedBytes = data[20];
    this.UsableSize = this.PageSize - this.ReservedBytes;
    if (this.UsableSize < 480)
      throw new InvalidDataException("SQLite usable page size below 480 bytes.");
    var declared = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(28));
    var changeCounter = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(24));
    var validFor = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(92));
    // The in-header page count is valid only when version-valid-for matches the change counter.
    this.PageCount = declared != 0 && validFor == changeCounter ? declared : (uint)(data.Length / this.PageSize);
    if ((long)this.PageCount * this.PageSize > data.Length)
      throw new InvalidDataException("SQLite database is shorter than its page count.");
    this.TextEncoding = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(56));
    if (this.TextEncoding is not (0 or 1))
      throw new NotSupportedException("SQLite: only UTF-8 databases are read.");
    this.FreelistPages = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(36));
    this.Schema = [.. this.ReadTable(1).Select(r => new SchemaEntry(
      r.Values.ElementAtOrDefault(0) as string ?? "", r.Values.ElementAtOrDefault(1) as string ?? "",
      r.Values.ElementAtOrDefault(2) as string ?? "", r.Values.ElementAtOrDefault(3) is long p ? p : 0,
      r.Values.ElementAtOrDefault(4) as string))];
  }

  public int PageSize { get; }
  public int ReservedBytes { get; }
  public int UsableSize { get; }
  public uint PageCount { get; }
  public uint TextEncoding { get; }
  public uint FreelistPages { get; }
  public IReadOnlyList<SchemaEntry> Schema { get; }

  /// <summary>Every row of the table b-tree rooted at <paramref name="rootPage"/>, in rowid order.</summary>
  public List<Row> ReadTable(long rootPage) {
    var rows = new List<Row>();
    var visited = new HashSet<uint>();
    this.Walk(checked((uint)rootPage), rows, visited, 0);
    return rows;
  }

  private void Walk(uint page, List<Row> rows, HashSet<uint> visited, int depth) {
    if (page == 0 || page > this.PageCount || !visited.Add(page) || depth > 64)
      throw new InvalidDataException($"SQLite b-tree references invalid or repeated page {page}.");
    var p = this.Page(page);
    var hdr = page == 1 ? 100 : 0;
    var type = p[hdr];
    var cells = BinaryPrimitives.ReadUInt16BigEndian(p[(hdr + 3)..]);
    switch (type) {
      case 0x0D: {
        for (var i = 0; i < cells; ++i) {
          var off = BinaryPrimitives.ReadUInt16BigEndian(p[(hdr + 8 + 2 * i)..]);
          var at = (int)off;
          var size = ReadVarint(p, ref at);
          var rowid = ReadVarint(p, ref at);
          rows.Add(new Row(rowid, DecodeRecord(this.Payload(p, at, size, tableLeaf: true))));
        }
        break;
      }
      case 0x05: {
        for (var i = 0; i < cells; ++i) {
          var off = BinaryPrimitives.ReadUInt16BigEndian(p[(hdr + 12 + 2 * i)..]);
          this.Walk(BinaryPrimitives.ReadUInt32BigEndian(p[off..]), rows, visited, depth + 1);
        }
        this.Walk(BinaryPrimitives.ReadUInt32BigEndian(p[(hdr + 8)..]), rows, visited, depth + 1);
        break;
      }
      default:
        throw new InvalidDataException($"SQLite page {page} is not a table b-tree page (type 0x{type:X2}).");
    }
  }

  private ReadOnlySpan<byte> Page(uint page) => this._data.AsSpan((int)((page - 1) * (long)this.PageSize), this.PageSize);

  private byte[] Payload(ReadOnlySpan<byte> page, int at, long size, bool tableLeaf) {
    if (size < 0 || size > int.MaxValue) throw new InvalidDataException("SQLite payload size out of range.");
    var local = LocalPayload(this.UsableSize, size, tableLeaf);
    var result = new byte[size];
    if (at + local > page.Length) throw new InvalidDataException("SQLite cell overruns its page.");
    page.Slice(at, local).CopyTo(result);
    var done = local;
    if (done < size) {
      var next = BinaryPrimitives.ReadUInt32BigEndian(page[(at + local)..]);
      var guard = 0;
      while (done < size) {
        if (next == 0 || next > this.PageCount || ++guard > this.PageCount)
          throw new InvalidDataException("SQLite overflow chain is broken.");
        var o = this.Page(next);
        var n = (int)Math.Min(this.UsableSize - 4, size - done);
        o.Slice(4, n).CopyTo(result.AsSpan(done));
        done += n;
        next = BinaryPrimitives.ReadUInt32BigEndian(o);
      }
    }
    return result;
  }

  /// <summary>Bytes of a payload of <paramref name="size"/> kept on the b-tree page itself (fileformat2 §1.6).</summary>
  public static int LocalPayload(int usable, long size, bool tableLeaf) {
    var x = tableLeaf ? usable - 35 : (usable - 12) * 64 / 255 - 23;
    if (size <= x) return (int)size;
    var m = (usable - 12) * 32 / 255 - 23;
    var k = m + (int)((size - m) % (usable - 4));
    return k <= x ? k : m;
  }

  /// <summary>Decodes a record (header of serial types, then the values).</summary>
  public static object?[] DecodeRecord(byte[] record) {
    var at = 0;
    var headerSize = ReadVarint(record, ref at);
    if (headerSize < 1 || headerSize > record.Length) throw new InvalidDataException("SQLite record header out of range.");
    var types = new List<long>();
    while (at < headerSize) types.Add(ReadVarint(record, ref at));
    var body = (int)headerSize;
    var values = new object?[types.Count];
    for (var i = 0; i < types.Count; ++i) {
      var t = types[i];
      var len = SerialLength(t);
      if (body + len > record.Length) throw new InvalidDataException("SQLite record value overruns the record.");
      var v = record.AsSpan(body, len);
      values[i] = t switch {
        0 => null,
        >= 1 and <= 6 => ReadSigned(v),
        7 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(v)),
        8 => 0L,
        9 => 1L,
        >= 12 when t % 2 == 0 => v.ToArray(),
        >= 13 => Encoding.UTF8.GetString(v),
        _ => throw new InvalidDataException($"SQLite serial type {t} is reserved."),
      };
      body += len;
    }
    return values;
  }

  public static int SerialLength(long type) => type switch {
    0 or 8 or 9 => 0,
    1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 6, 6 or 7 => 8,
    >= 12 => checked((int)((type - (type % 2 == 0 ? 12 : 13)) / 2)),
    _ => throw new InvalidDataException($"SQLite serial type {type} is reserved."),
  };

  private static long ReadSigned(ReadOnlySpan<byte> v) {
    long x = (sbyte)v[0];
    for (var i = 1; i < v.Length; ++i) x = (x << 8) | v[i];
    return x;
  }

  /// <summary>Reads a SQLite varint (1–9 bytes, big-endian, 7 bits per byte, the ninth byte whole).</summary>
  public static long ReadVarint(ReadOnlySpan<byte> s, ref int at) {
    long v = 0;
    for (var i = 0; i < 8; ++i) {
      if (at >= s.Length) throw new InvalidDataException("SQLite varint runs off the page.");
      var b = s[at++];
      v = (v << 7) | (uint)(b & 0x7F);
      if ((b & 0x80) == 0) return v;
    }
    if (at >= s.Length) throw new InvalidDataException("SQLite varint runs off the page.");
    return (v << 8) | s[at++];
  }
}
