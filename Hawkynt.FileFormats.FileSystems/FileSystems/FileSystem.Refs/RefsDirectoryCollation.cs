using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// The order of a ReFS directory table: key type first; name rows (0x30) by the name upcased
/// through the volume's own upcase table (OIDs 7/8, see <see cref="RefsUpcaseTable"/>) and
/// compared by UTF-16 code unit; file-number rows (0x20) by file number; others by their bytes.
/// Without a readable upcase table only ASCII names are collated (a-z folded to A-Z). Every
/// directory is checked to be stored in exactly this order before it is edited.
/// </summary>
internal sealed class RefsDirectoryCollation {
  private readonly RefsUpcaseTable? _upcase;

  private RefsDirectoryCollation(RefsUpcaseTable? upcase) => this._upcase = upcase;

  /// <summary>The collation of the directory holding <paramref name="rows"/>, refusing one this editor cannot insert <paramref name="newName"/> into in order.</summary>
  public static RefsDirectoryCollation For(RefsMetadataReader metadata, IReadOnlyList<RefsBTreeRow> rows, string newName) {
    RefsUpcaseTable? upcase;
    try { upcase = RefsUpcaseTable.Load(metadata); } catch (InvalidDataException) { upcase = null; }
    var collation = new RefsDirectoryCollation(upcase);
    if (upcase is null && (newName.Any(static c => c > 0x7F)
        || rows.Any(static r => BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x30 && !IsAscii(r.Key.AsSpan(4)))))
      throw new NotSupportedException("ReFS: names beyond ASCII need the volume's upcase table, which was not read; the directory is not edited.");
    for (var i = 1; i < rows.Count; ++i)
      if (collation.Compare(rows[i - 1].Key, rows[i].Key) >= 0)
        throw new NotSupportedException("ReFS: the directory's stored row order is not the collation this editor reproduces; it is not edited.");
    return collation;
  }

  public int Compare(byte[] a, byte[] b) {
    var ta = BinaryPrimitives.ReadUInt16LittleEndian(a);
    var tb = BinaryPrimitives.ReadUInt16LittleEndian(b);
    if (ta != tb) return ta.CompareTo(tb);
    if (ta == 0x30) return this.CompareNames(a.AsSpan(4), b.AsSpan(4));
    if (ta == 0x20 && a.Length >= 16 && b.Length >= 16)
      return BinaryPrimitives.ReadUInt64LittleEndian(a.AsSpan(8)).CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8)));
    return a.AsSpan(2).SequenceCompareTo(b.AsSpan(2));
  }

  public int CompareNames(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    => this._upcase?.CompareUtf16(left, right, caseSensitive: false) ?? CompareFolded(left, right);

  private static bool IsAscii(ReadOnlySpan<byte> utf16) {
    for (var i = 0; i + 1 < utf16.Length; i += 2)
      if (BinaryPrimitives.ReadUInt16LittleEndian(utf16[i..]) > 0x7F) return false;
    return true;
  }

  private static int CompareFolded(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
    var count = Math.Min(left.Length, right.Length) / 2;
    for (var i = 0; i < count; ++i) {
      int a = BinaryPrimitives.ReadUInt16LittleEndian(left[(i * 2)..]);
      int b = BinaryPrimitives.ReadUInt16LittleEndian(right[(i * 2)..]);
      if (a is >= 'a' and <= 'z') a -= 32;
      if (b is >= 'a' and <= 'z') b -= 32;
      if (a != b) return a.CompareTo(b);
    }
    return left.Length.CompareTo(right.Length);
  }
}
