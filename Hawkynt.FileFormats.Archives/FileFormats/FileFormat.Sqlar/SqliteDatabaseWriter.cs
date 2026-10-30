using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Sqlar;

/// <summary>
/// Writes a fresh SQLite 3 database file (https://www.sqlite.org/fileformat2.html) holding the given
/// tables, built bottom-up: a table b-tree per table keyed by rowid and, optionally, the automatic
/// unique index SQLite keeps for a <c>PRIMARY KEY</c> on a non-integer column. Pages are packed,
/// payloads that do not fit spill into overflow chains, and there is no freelist, journal or WAL.
/// </summary>
public static class SqliteDatabaseWriter {

  /// <summary>Page size written (SQLite's default since 3.12).</summary>
  public const int PageSize = 4096;

  /// <summary>SQLite library version number recorded at header offset 96 (3.45.3).</summary>
  public const uint SqliteVersionNumber = 3045003;

  private const byte TableLeaf = 0x0D, TableInterior = 0x05, IndexLeaf = 0x0A, IndexInterior = 0x02;

  /// <summary>A table to write.</summary>
  /// <param name="Name">Table name as it appears in the schema.</param>
  /// <param name="Sql">The <c>CREATE TABLE</c> statement stored in the schema.</param>
  /// <param name="Rows">Rows in ascending rowid order.</param>
  /// <param name="AutoIndexColumn">Column of a <c>sqlite_autoindex_&lt;table&gt;_1</c> index (unique, BINARY collation), or -1.</param>
  public sealed record Table(string Name, string Sql, IReadOnlyList<SqliteDatabase.Row> Rows, int AutoIndexColumn = -1);

  public static byte[] Write(IReadOnlyList<Table> tables) {
    ArgumentNullException.ThrowIfNull(tables);
    var pages = new List<byte[]> { new byte[PageSize] }; // page 1: header + schema root
    var plans = new List<(Table Table, uint TableRoot, uint IndexRoot)>();
    foreach (var t in tables) {
      for (var i = 1; i < t.Rows.Count; ++i)
        if (t.Rows[i].RowId <= t.Rows[i - 1].RowId)
          throw new ArgumentException($"SQLite: rows of '{t.Name}' are not in ascending rowid order.", nameof(tables));
      var tableRoot = Allocate(pages);
      var indexRoot = t.AutoIndexColumn >= 0 ? Allocate(pages) : 0u;
      plans.Add((t, tableRoot, indexRoot));
    }

    var schemaRows = new List<SqliteDatabase.Row>();
    long schemaRowId = 0;
    foreach (var (t, tableRoot, indexRoot) in plans) {
      schemaRows.Add(new(++schemaRowId, ["table", t.Name, t.Name, (long)tableRoot, t.Sql]));
      if (indexRoot != 0)
        schemaRows.Add(new(++schemaRowId, ["index", $"sqlite_autoindex_{t.Name}_1", t.Name, (long)indexRoot, null]));
    }

    foreach (var (t, tableRoot, indexRoot) in plans) {
      BuildTable(pages, tableRoot, t.Rows);
      if (indexRoot != 0) {
        var keys = t.Rows.Select(r => (Key: r.Values[t.AutoIndexColumn], r.RowId)).ToList();
        keys.Sort((a, b) => BinaryCollation.Instance.Compare(a.Key, b.Key));
        for (var i = 1; i < keys.Count; ++i)
          if (BinaryCollation.Instance.Compare(keys[i - 1].Key, keys[i].Key) == 0)
            throw new ArgumentException($"SQLite: duplicate key '{keys[i].Key}' in the unique index of '{t.Name}'.", nameof(tables));
        BuildIndex(pages, indexRoot, [.. keys.Select(k => EncodeRecord([k.Key, k.RowId]))]);
      }
    }
    BuildTable(pages, 1, schemaRows);

    WriteHeader(pages[0], (uint)pages.Count);
    var image = new byte[pages.Count * PageSize];
    for (var i = 0; i < pages.Count; ++i) pages[i].CopyTo(image, i * PageSize);
    return image;
  }

  private static uint Allocate(List<byte[]> pages) {
    pages.Add(new byte[PageSize]);
    return (uint)pages.Count;
  }

  private static void WriteHeader(byte[] page1, uint pageCount) {
    "SQLite format 3\0"u8.CopyTo(page1);
    BinaryPrimitives.WriteUInt16BigEndian(page1.AsSpan(16), PageSize);
    page1[18] = 1; // file format write version: legacy (rollback journal)
    page1[19] = 1; // file format read version
    page1[20] = 0; // reserved bytes per page
    page1[21] = 64; // maximum embedded payload fraction (must be 64)
    page1[22] = 32; // minimum embedded payload fraction (must be 32)
    page1[23] = 32; // leaf payload fraction (must be 32)
    BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(24), 1); // file change counter
    BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(28), pageCount);
    BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(40), 1); // schema cookie
    BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(44), 4); // schema format number
    BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(56), 1); // text encoding: UTF-8
    BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(92), 1); // version-valid-for = change counter
    BinaryPrimitives.WriteUInt32BigEndian(page1.AsSpan(96), SqliteVersionNumber);
  }

  private static int Offset(uint page) => page == 1 ? 100 : 0;

  private static bool Fits(IEnumerable<byte[]> cells, int headerSize, int offset)
    => offset + headerSize + cells.Sum(c => c.Length + 2) <= PageSize;

  // ── table b-trees ───────────────────────────────────────────────────────

  private static void BuildTable(List<byte[]> pages, uint root, IReadOnlyList<SqliteDatabase.Row> rows) {
    var cells = new List<byte[]>(rows.Count);
    foreach (var row in rows) {
      var record = EncodeRecord(row.Values);
      var head = new MemoryStream();
      WriteVarint(head, record.Length);
      WriteVarint(head, row.RowId);
      cells.Add(Cell(pages, head.ToArray(), record, tableLeaf: true));
    }
    if (Fits(cells, 8, Offset(root))) {
      WritePage(pages, root, TableLeaf, cells, 0);
      return;
    }

    // Leaves, then interior levels of (child page, largest rowid in that child) until one fits the root.
    var level = new List<(uint Page, long MaxRowId)>();
    foreach (var (start, count) in Groups(cells, 8)) {
      var page = Allocate(pages);
      WritePage(pages, page, TableLeaf, cells.GetRange(start, count), 0);
      level.Add((page, rows[start + count - 1].RowId));
    }
    while (true) {
      var interiorCells = level.Select(c => TableInteriorCell(c.Page, c.MaxRowId)).ToList();
      // n children = n-1 cells plus the right-most pointer.
      if (Fits(interiorCells.Take(level.Count - 1), 12, Offset(root))) {
        WritePage(pages, root, TableInterior, interiorCells.GetRange(0, level.Count - 1), level[^1].Page);
        return;
      }
      var next = new List<(uint, long)>();
      foreach (var (start, count) in ChildGroups(interiorCells)) {
        var page = Allocate(pages);
        WritePage(pages, page, TableInterior, interiorCells.GetRange(start, count - 1), level[start + count - 1].Page);
        next.Add((page, level[start + count - 1].MaxRowId));
      }
      level = next;
    }
  }

  private static byte[] TableInteriorCell(uint child, long rowId) {
    var ms = new MemoryStream();
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(b, child);
    ms.Write(b);
    WriteVarint(ms, rowId);
    return ms.ToArray();
  }

  /// <summary>Greedy split of leaf cells into pages: (first cell, count) per page.</summary>
  private static List<(int Start, int Count)> Groups(List<byte[]> cells, int headerSize) {
    var groups = new List<(int, int)>();
    var start = 0;
    var used = headerSize;
    for (var i = 0; i < cells.Count; ++i) {
      if (i > start && used + cells[i].Length + 2 > PageSize) {
        groups.Add((start, i - start));
        start = i;
        used = headerSize;
      }
      used += cells[i].Length + 2;
    }
    groups.Add((start, cells.Count - start));
    return groups;
  }

  /// <summary>
  /// Split of a level's children into interior pages, each (first child, child count): a page holds
  /// count-1 cells (one per child but its last) and points right at its last child. No page is left with
  /// a single child.
  /// </summary>
  private static List<(int Start, int Count)> ChildGroups(List<byte[]> childCells) {
    var groups = new List<(int Start, int Count)>();
    var start = 0;
    var used = 12;
    for (var i = 1; i < childCells.Count; ++i) {
      // Adding child i turns child i-1 into a cell.
      var need = childCells[i - 1].Length + 2;
      if (used + need > PageSize) {
        groups.Add((start, i - start));
        start = i;
        used = 12;
        continue;
      }
      used += need;
    }
    groups.Add((start, childCells.Count - start));
    if (groups.Count > 1 && groups[^1].Count == 1) {
      var (s, c) = groups[^2];
      groups[^2] = (s, c - 1);
      groups[^1] = (s + c - 1, 2);
    }
    return groups;
  }

  // ── index b-trees ───────────────────────────────────────────────────────

  private static void BuildIndex(List<byte[]> pages, uint root, List<byte[]> keys) {
    var leafCells = keys.Select(k => {
      var head = new MemoryStream();
      WriteVarint(head, k.Length);
      return Cell(pages, head.ToArray(), k, tableLeaf: false);
    }).ToList();
    if (Fits(leafCells, 8, Offset(root))) {
      WritePage(pages, root, IndexLeaf, leafCells, 0);
      return;
    }

    // Leaves with one key between each pair of leaves lifted out as the parent's separator.
    var leaves = new List<List<int>>();
    var separators = new List<int>();
    var current = new List<int>();
    var used = 8;
    for (var i = 0; i < leafCells.Count; ++i) {
      if (current.Count > 0 && used + leafCells[i].Length + 2 > PageSize) {
        leaves.Add(current);
        separators.Add(i);
        current = [];
        used = 8;
        continue;
      }
      current.Add(i);
      used += leafCells[i].Length + 2;
    }
    if (current.Count == 0) {
      // The last key was lifted with nothing after it: it starts the last leaf instead, and the
      // previous leaf's last key becomes the separator.
      var last = separators[^1];
      var previous = leaves[^1];
      separators[^1] = previous[^1];
      previous.RemoveAt(previous.Count - 1);
      current = [last];
    }
    leaves.Add(current);

    var children = new List<uint>();
    foreach (var leaf in leaves) {
      var page = Allocate(pages);
      WritePage(pages, page, IndexLeaf, [.. leaf.Select(i => leafCells[i])], 0);
      children.Add(page);
    }
    BuildIndexInterior(pages, root, children, [.. separators.Select(i => keys[i])]);
  }

  private static void BuildIndexInterior(List<byte[]> pages, uint root, List<uint> children, List<byte[]> separators) {
    // separators[i] sits between children[i] and children[i+1]; cell i = (children[i], separators[i]).
    var cells = separators.Select((k, i) => {
      var head = new MemoryStream();
      Span<byte> b = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32BigEndian(b, children[i]);
      head.Write(b);
      WriteVarint(head, k.Length);
      return Cell(pages, head.ToArray(), k, tableLeaf: false);
    }).ToList();
    if (Fits(cells, 12, Offset(root))) {
      WritePage(pages, root, IndexInterior, cells, children[^1]);
      return;
    }

    // Pages of (children a..b, cells a..b-1, right-most b); separator b is lifted between pages.
    var groups = new List<(int Start, int End)>();
    var lifted = new List<int>();
    var start = 0;
    var used = 12;
    for (var i = 0; i < cells.Count; ++i) {
      if (i > start && used + cells[i].Length + 2 > PageSize) {
        groups.Add((start, i));
        lifted.Add(i);
        start = i + 1;
        used = 12;
        continue;
      }
      used += cells[i].Length + 2;
    }
    groups.Add((start, children.Count - 1));
    if (groups[^1].Start == groups[^1].End && groups.Count > 1) {
      // The last page would hold a single child and no cell: lift the previous page's last separator instead.
      var (ps, pe) = groups[^2];
      groups[^2] = (ps, pe - 1);
      groups[^1] = (pe, children.Count - 1);
      lifted[^1] = pe - 1;
    }

    var parents = new List<uint>();
    foreach (var (s, e) in groups) {
      var page = Allocate(pages);
      WritePage(pages, page, IndexInterior, cells.GetRange(s, e - s), children[e]);
      parents.Add(page);
    }
    BuildIndexInterior(pages, root, parents, [.. lifted.Select(i => separators[i])]);
  }

  // ── cells and pages ─────────────────────────────────────────────────────

  /// <summary>
  /// A cell: <paramref name="head"/>, the locally kept part of <paramref name="payload"/>, and when it
  /// spills, the first page of an overflow chain written here.
  /// </summary>
  private static byte[] Cell(List<byte[]> pages, byte[] head, byte[] payload, bool tableLeaf) {
    var usable = PageSize;
    var local = SqliteDatabase.LocalPayload(usable, payload.Length, tableLeaf);
    var cell = new byte[head.Length + local + (local < payload.Length ? 4 : 0)];
    head.CopyTo(cell, 0);
    payload.AsSpan(0, local).CopyTo(cell.AsSpan(head.Length));
    if (local < payload.Length) {
      byte[]? previous = null;
      for (var at = local; at < payload.Length; at += usable - 4) {
        var page = Allocate(pages);
        if (previous != null) BinaryPrimitives.WriteUInt32BigEndian(previous, page);
        else BinaryPrimitives.WriteUInt32BigEndian(cell.AsSpan(head.Length + local), page);
        previous = pages[(int)page - 1];
        payload.AsSpan(at, Math.Min(usable - 4, payload.Length - at)).CopyTo(previous.AsSpan(4));
      }
    }
    return cell;
  }

  private static void WritePage(List<byte[]> pages, uint pageNumber, byte type, List<byte[]> cells, uint rightMost) {
    var page = pages[(int)pageNumber - 1];
    var offset = Offset(pageNumber);
    var interior = type is TableInterior or IndexInterior;
    var headerSize = interior ? 12 : 8;
    if (!Fits(cells, headerSize, offset))
      throw new InvalidOperationException("SQLite page overfilled.");
    var content = PageSize;
    for (var i = 0; i < cells.Count; ++i) {
      content -= cells[i].Length;
      cells[i].CopyTo(page, content);
      BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(offset + headerSize + 2 * i), (ushort)content);
    }
    page[offset] = type;
    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(offset + 1), 0); // no freeblocks
    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(offset + 3), (ushort)cells.Count);
    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(offset + 5), (ushort)(content == 65536 ? 0 : content));
    page[offset + 7] = 0; // fragmented free bytes
    if (interior) BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(offset + 8), rightMost);
  }

  // ── records ─────────────────────────────────────────────────────────────

  /// <summary>Encodes a record: values may be null, int, long, double, string or byte[].</summary>
  public static byte[] EncodeRecord(IReadOnlyList<object?> values) {
    ArgumentNullException.ThrowIfNull(values);
    var types = new MemoryStream();
    var body = new MemoryStream();
    foreach (var v in values) {
      switch (v) {
        case null: WriteVarint(types, 0); break;
        case int i: WriteInteger(types, body, i); break;
        case long l: WriteInteger(types, body, l); break;
        case double d: {
          WriteVarint(types, 7);
          Span<byte> b = stackalloc byte[8];
          BinaryPrimitives.WriteInt64BigEndian(b, BitConverter.DoubleToInt64Bits(d));
          body.Write(b);
          break;
        }
        case string s: {
          var bytes = Encoding.UTF8.GetBytes(s);
          WriteVarint(types, 2L * bytes.Length + 13);
          body.Write(bytes);
          break;
        }
        case byte[] blob:
          WriteVarint(types, 2L * blob.Length + 12);
          body.Write(blob);
          break;
        default: throw new ArgumentException($"SQLite: cannot store a {v.GetType().Name}.", nameof(values));
      }
    }
    // The header size counts its own varint.
    var typeLength = (int)types.Length;
    var headerSize = typeLength + 1;
    while (VarintLength(headerSize) + typeLength != headerSize) headerSize = typeLength + VarintLength(headerSize);
    var record = new MemoryStream();
    WriteVarint(record, headerSize);
    types.WriteTo(record);
    body.WriteTo(record);
    return record.ToArray();
  }

  private static void WriteInteger(MemoryStream types, MemoryStream body, long v) {
    if (v == 0) { WriteVarint(types, 8); return; }
    if (v == 1) { WriteVarint(types, 9); return; }
    var (type, len) = v switch {
      >= sbyte.MinValue and <= sbyte.MaxValue => (1, 1),
      >= short.MinValue and <= short.MaxValue => (2, 2),
      >= -8388608 and <= 8388607 => (3, 3),
      >= int.MinValue and <= int.MaxValue => (4, 4),
      >= -140737488355328 and <= 140737488355327 => (5, 6),
      _ => (6, 8),
    };
    WriteVarint(types, type);
    for (var i = len - 1; i >= 0; --i) body.WriteByte((byte)(v >> (8 * i)));
  }

  public static int VarintLength(long v) {
    var u = (ulong)v;
    if ((u & 0xFF00000000000000UL) != 0) return 9;
    var n = 1;
    while ((u >>= 7) != 0) ++n;
    return n;
  }

  public static void WriteVarint(Stream s, long value) {
    var v = (ulong)value;
    if ((v & 0xFF00000000000000UL) != 0) {
      Span<byte> nine = stackalloc byte[9];
      nine[8] = (byte)v;
      v >>= 8;
      for (var i = 7; i >= 0; --i) {
        nine[i] = (byte)((v & 0x7F) | 0x80);
        v >>= 7;
      }
      s.Write(nine);
      return;
    }
    Span<byte> tmp = stackalloc byte[9];
    var n = 0;
    do {
      tmp[n++] = (byte)(v & 0x7F);
      v >>= 7;
    } while (v != 0);
    for (var i = n - 1; i >= 0; --i) s.WriteByte((byte)(tmp[i] | (i > 0 ? 0x80 : 0)));
  }

  /// <summary>SQLite's BINARY collation and type order: NULL &lt; numbers &lt; text (memcmp of UTF-8) &lt; blobs.</summary>
  internal sealed class BinaryCollation : IComparer<object?> {
    public static readonly BinaryCollation Instance = new();

    public int Compare(object? x, object? y) {
      var rx = Rank(x);
      var ry = Rank(y);
      if (rx != ry) return rx.CompareTo(ry);
      return x switch {
        null => 0,
        string sx => Encoding.UTF8.GetBytes(sx).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes((string)y!)),
        byte[] bx => bx.AsSpan().SequenceCompareTo((byte[])y!),
        _ => Convert.ToDouble(x, System.Globalization.CultureInfo.InvariantCulture)
          .CompareTo(Convert.ToDouble(y, System.Globalization.CultureInfo.InvariantCulture)),
      };
    }

    private static int Rank(object? v) => v switch { null => 0, string => 2, byte[] => 3, _ => 1 };
  }
}
