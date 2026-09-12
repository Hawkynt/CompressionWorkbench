#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// Reconstructs the ReFS UTF-16 upcase table from OID 0x07/0x08. ReFS stores
/// the fixed 65,536-entry Windows mapping as ordered B+ fragments with a short
/// header; the logical mapping is the final 128 KiB of the concatenated data.
/// </summary>
internal sealed class RefsUpcaseTable {
  private const int EntryCount = 65536;
  private const int LogicalBytes = EntryCount * 2;
  private readonly ushort[] _map;

  private RefsUpcaseTable(ushort[] map) => this._map = map;

  public ushort this[ushort codeUnit] => this._map[codeUnit];

  public static RefsUpcaseTable Load(RefsMetadataReader metadata) {
    ArgumentNullException.ThrowIfNull(metadata);
    foreach (var oid in new[] { 0x07UL, 0x08UL }) {
      if (!TryFindObjectRoot(metadata, oid, out var root)) continue;
      try {
        var fragments = new List<(ulong Key, byte[] Value)>();
        foreach (var row in metadata.WalkTree(root, virtualAddresses: true)) {
          if (!TryReadScalarKey(row.Key, out var key) || key == 0 || row.Value.Length == 0) continue;
          fragments.Add((key, row.Value));
        }
        if (fragments.Count == 0) continue;
        fragments.Sort((a, b) => a.Key.CompareTo(b.Key));

        var length = fragments.Sum(f => f.Value.Length);
        if (length < LogicalBytes) continue;
        var joined = new byte[length];
        var cursor = 0;
        foreach (var fragment in fragments) {
          fragment.Value.CopyTo(joined, cursor);
          cursor += fragment.Value.Length;
        }

        // The table carries a small implementation header before the 65,536
        // u16 entries. Taking the fixed-size logical tail is invariant across
        // the observed ReFS 3.x formats and avoids baking in a version-specific
        // header length.
        var logical = joined.AsSpan(joined.Length - LogicalBytes, LogicalBytes);
        var map = new ushort[EntryCount];
        var nonIdentity = 0;
        for (var i = 0; i < map.Length; ++i) {
          map[i] = BinaryPrimitives.ReadUInt16LittleEndian(logical.Slice(i * 2, 2));
          if (map[i] != i) ++nonIdentity;
        }

        // A corrupt fragment stream can still have the right byte count. The
        // Windows ReFS table is a fixed constant with 973 non-identity entries;
        // use that as a strong structural validation before trusting collation.
        if (nonIdentity != 973) continue;
        return new RefsUpcaseTable(map);
      } catch (InvalidDataException) {
        // Primary/duplicate are independent failover copies.
      }
    }
    throw new InvalidDataException("Neither ReFS Upcase Table copy could be reconstructed.");
  }

  public int CompareUtf16(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, bool caseSensitive) {
    if ((left.Length & 1) != 0 || (right.Length & 1) != 0)
      return CompareBytes(left, right);

    var count = Math.Min(left.Length, right.Length) / 2;
    for (var i = 0; i < count; ++i) {
      var a = BinaryPrimitives.ReadUInt16LittleEndian(left.Slice(i * 2, 2));
      var b = BinaryPrimitives.ReadUInt16LittleEndian(right.Slice(i * 2, 2));
      if (!caseSensitive) {
        a = this._map[a];
        b = this._map[b];
      }
      var cmp = a.CompareTo(b);
      if (cmp != 0) return cmp;
    }
    return left.Length.CompareTo(right.Length);
  }

  private static bool TryFindObjectRoot(
      RefsMetadataReader metadata,
      ulong wantedOid,
      out RefsPageReference root) {
    try {
      foreach (var row in metadata.WalkRoot(0)) {
        if (row.Key.Length < 16 || row.Value.Length < 0x20 + metadata.PageReferenceSize) continue;
        if (BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(8, 8)) != wantedOid) continue;
        var candidate = RefsPageReference.Parse(row.Value.AsSpan(0x20));
        if (candidate.Lcns.Count == 0) continue;
        root = candidate;
        return true;
      }
    } catch (InvalidDataException) { }
    root = RefsPageReference.Empty;
    return false;
  }

  private static bool TryReadScalarKey(ReadOnlySpan<byte> key, out ulong value) {
    if (key.Length >= 8) {
      var first = BinaryPrimitives.ReadUInt64LittleEndian(key[..8]);
      if (AllZero(key[8..])) { value = first; return true; }
      var last = BinaryPrimitives.ReadUInt64LittleEndian(key[^8..]);
      if (AllZero(key[..^8])) { value = last; return true; }
    }
    value = 0;
    return false;
  }

  private static bool AllZero(ReadOnlySpan<byte> bytes) {
    foreach (var value in bytes)
      if (value != 0) return false;
    return true;
  }

  internal static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
    var count = Math.Min(left.Length, right.Length);
    for (var i = 0; i < count; ++i) {
      var cmp = left[i].CompareTo(right[i]);
      if (cmp != 0) return cmp;
    }
    return left.Length.CompareTo(right.Length);
  }
}

/// <summary>
/// Schema-aware ReFS key ordering. The Schema Table selector is retained for
/// validation/reporting, while schemas whose exact tuple is known use the
/// decoded on-disk tuple rather than a bytewise approximation.
/// </summary>
internal sealed class RefsKeyComparer : IComparer<byte[]> {
  private readonly uint _schemaId;
  private readonly uint _rulesSelector;
  private readonly RefsUpcaseTable? _upcase;
  private readonly bool _caseSensitiveDirectory;

  public RefsKeyComparer(
      uint schemaId,
      RefsSchemaCatalog catalog,
      RefsUpcaseTable? upcase = null,
      bool caseSensitiveDirectory = false) {
    this._schemaId = schemaId;
    this._rulesSelector = catalog.Get(schemaId).KeyRulesSelector;
    this._upcase = upcase;
    this._caseSensitiveDirectory = caseSensitiveDirectory;
  }

  public uint SchemaId => this._schemaId;
  public uint RulesSelector => this._rulesSelector;

  public int Compare(byte[]? x, byte[]? y) {
    if (ReferenceEquals(x, y)) return 0;
    if (x is null) return -1;
    if (y is null) return 1;
    return this.Compare(x.AsSpan(), y.AsSpan());
  }

  public int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
    return this._schemaId switch {
      // allocator / integrity / refcount ranges: (start, length)
      0xE010 or 0xE080 or 0xE0B0 => CompareU64Tuple(left, right, 0, 8),

      // Object Table SmsBigIdentifier: high-half then OID low-half. The high
      // half is zero on normal objects but remains part of the ordering.
      0xE030 => CompareU64Tuple(left, right, 0, 8),

      // Parent-Child link rule: (ParentOID@+8, ChildOID@+0x18).
      0xE040 => CompareU64Tuple(left, right, 8, 0x18, 0, 0x10),

      // Schema key: u32 schema id followed by zero padding.
      0xE060 => CompareU32Tuple(left, right, 0, 4),

      // Upcase/Logfile information tables use scalar numeric keys.
      0xE090 => CompareScalarKey(left, right),

      // Container Table: (ContainerId, constant tag).
      0xE0C0 => CompareU64Tuple(left, right, 0, 8),

      // Attribute schemas. The embedded type is the first u16. Filename keys
      // (0x30/0x40) carry UTF-16 after the 4-byte type/flags prefix and use the
      // volume Upcase Table unless the directory opted into case sensitivity.
      0x130 or 0x140 => this.CompareFilenameKey(left, right),

      // Most embedded attribute keys begin with a fixed type/subtype followed
      // by numeric identity fields. Little-endian numeric comparison is required
      // before a byte tail, not raw memcmp on the whole key.
      0x110 or 0x120 or 0x150 or 0x160 or 0x170 or 0x180 or 0x190 or 0x1A0
        or 0x1B0 or 0x1C0 or 0x1D0 or 0x1E0 or 0x1F0 or 0x200
        => CompareAttributeKey(left, right),

      // Container Index is empty on checkpoint-consistent corpus images, so
      // there is no persisted key grammar to infer. The remaining system
      // schemas likewise lack a fully decoded comparator contract. Refuse to
      // write them rather than silently sorting with memcmp.
      _ => throw new NotSupportedException(
        $"ReFS schema 0x{this._schemaId:X} / key-rules selector {this._rulesSelector} has no proven writable key comparator."),
    };
  }

  private int CompareFilenameKey(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
    if (left.Length < 4 || right.Length < 4)
      return RefsUpcaseTable.CompareBytes(left, right);
    var typeCmp = BinaryPrimitives.ReadUInt16LittleEndian(left[..2])
      .CompareTo(BinaryPrimitives.ReadUInt16LittleEndian(right[..2]));
    if (typeCmp != 0) return typeCmp;
    var flagsCmp = BinaryPrimitives.ReadUInt16LittleEndian(left.Slice(2, 2))
      .CompareTo(BinaryPrimitives.ReadUInt16LittleEndian(right.Slice(2, 2)));
    if (flagsCmp != 0) return flagsCmp;
    if (this._caseSensitiveDirectory)
      return RefsUpcaseTable.CompareBytes(left[4..], right[4..]);
    if (this._upcase == null)
      throw new InvalidOperationException("ReFS filename mutation requires the volume Upcase Table.");
    return this._upcase.CompareUtf16(left[4..], right[4..], caseSensitive: false);
  }

  private static int CompareAttributeKey(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
    if (left.Length >= 4 && right.Length >= 4) {
      var type = BinaryPrimitives.ReadUInt16LittleEndian(left[..2])
        .CompareTo(BinaryPrimitives.ReadUInt16LittleEndian(right[..2]));
      if (type != 0) return type;
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(left.Slice(2, 2))
        .CompareTo(BinaryPrimitives.ReadUInt16LittleEndian(right.Slice(2, 2)));
      if (flags != 0) return flags;
    }

    var cursor = 4;
    while (cursor + 8 <= left.Length && cursor + 8 <= right.Length) {
      var cmp = BinaryPrimitives.ReadUInt64LittleEndian(left.Slice(cursor, 8))
        .CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(right.Slice(cursor, 8)));
      if (cmp != 0) return cmp;
      cursor += 8;
    }
    return RefsUpcaseTable.CompareBytes(left[cursor..], right[cursor..]);
  }

  private static int CompareScalarKey(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
    if (left.Length >= 8 && right.Length >= 8) {
      var leftScalar = ScalarValue(left);
      var rightScalar = ScalarValue(right);
      var cmp = leftScalar.CompareTo(rightScalar);
      if (cmp != 0) return cmp;
    }
    return RefsUpcaseTable.CompareBytes(left, right);
  }

  private static ulong ScalarValue(ReadOnlySpan<byte> key) {
    var first = BinaryPrimitives.ReadUInt64LittleEndian(key[..8]);
    var firstTailZero = true;
    foreach (var b in key[8..]) firstTailZero &= b == 0;
    if (firstTailZero) return first;
    return BinaryPrimitives.ReadUInt64LittleEndian(key[^8..]);
  }

  private static int CompareU64Tuple(
      ReadOnlySpan<byte> left,
      ReadOnlySpan<byte> right,
      params int[] offsets) {
    foreach (var offset in offsets) {
      if (offset + 8 > left.Length || offset + 8 > right.Length) break;
      var cmp = BinaryPrimitives.ReadUInt64LittleEndian(left.Slice(offset, 8))
        .CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(right.Slice(offset, 8)));
      if (cmp != 0) return cmp;
    }
    return RefsUpcaseTable.CompareBytes(left, right);
  }

  private static int CompareU32Tuple(
      ReadOnlySpan<byte> left,
      ReadOnlySpan<byte> right,
      params int[] offsets) {
    foreach (var offset in offsets) {
      if (offset + 4 > left.Length || offset + 4 > right.Length) break;
      var cmp = BinaryPrimitives.ReadUInt32LittleEndian(left.Slice(offset, 4))
        .CompareTo(BinaryPrimitives.ReadUInt32LittleEndian(right.Slice(offset, 4)));
      if (cmp != 0) return cmp;
    }
    return RefsUpcaseTable.CompareBytes(left, right);
  }
}