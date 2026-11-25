using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// A directory table as the in-place editors change it: either one leaf page, or a root inner
/// page over leaf pages (two levels), as Windows writes a directory that outgrew one page —
/// measured on the test tree's 200-file directory (17, 4, 11 and 3 leaves at 4 and 64 KiB):
/// <list type="bullet">
/// <item>inner rows: key = a separator, value = the child's page reference (four LCNs and its
/// checksum); every key in the child is at most its separator; the last row has an empty key
/// (flags 2) and takes everything beyond;</item>
/// <item>the root page's descriptor (0x54) holds the leaf-page count at 0x68 and the table's
/// row count at 0x70 (one leaf: 0 and the leaf's rows), which Windows keeps current.</item>
/// </list>
/// A row goes to the first leaf whose separator is not below its key. Leaves are edited in
/// place; one without room would need a split, and one that would empty would need its
/// separator removed — both are refused here.
/// </summary>
internal sealed class RefsDirectoryTree {
  private readonly List<(byte[] Separator, ulong Leaf)> _children;

  private RefsDirectoryTree(IReadOnlyList<RefsBTreeRow> rows, ulong rootPage, bool singleLeaf, List<(byte[] Separator, ulong Leaf)> children) {
    this.Rows = rows;
    this.RootPage = rootPage;
    this.IsSingleLeaf = singleLeaf;
    this._children = children;
  }

  public IReadOnlyList<RefsBTreeRow> Rows { get; }
  public ulong RootPage { get; }
  public bool IsSingleLeaf { get; }
  public IEnumerable<ulong> Leaves => this._children.Select(static c => c.Leaf);

  public static RefsDirectoryTree Open(RefsMetadataReader metadata, RefsPageReference root) {
    var rows = metadata.WalkTree(root, virtualAddresses: true).ToList();
    if (rows.Count == 0) throw new InvalidDataException("ReFS: the directory has no rows.");
    var rootPage = metadata.TranslateVirtualLcn(root.Lcns[0]);
    var page = metadata.TryReadReferencedPage(root, virtualAddresses: true)
      ?? throw new InvalidDataException("ReFS: the directory's root page was not read.");
    var node = 0x50 + (int)U32(page, 0x50);
    if ((U32(page, node + 12) & 0x100) == 0) {
      if (rows.Any(r => r.PhysicalPageLcn != rootPage))
        throw new NotSupportedException("ReFS: the directory's rows are not where its root page says.");
      return new RefsDirectoryTree(rows, rootPage, true, [([], rootPage)]);
    }
    var children = new List<(byte[] Separator, ulong Leaf)>();
    var indexStart = (int)U32(page, node + 16);
    var count = (int)U32(page, node + 20);
    for (var i = 0; i < count; ++i) {
      var row = node + (int)(U32(page, node + indexStart + i * 4) & 0xFFFF);
      var keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(row + 4));
      var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(row + 6));
      var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(row + 10));
      var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(row + 12));
      if (valueLength < 0x30) throw new InvalidDataException("ReFS: an inner directory row holds no page reference.");
      var child = RefsPageReference.Parse(page.AsSpan(row + valueOffset, valueLength));
      var childPage = metadata.TryReadReferencedPage(child, virtualAddresses: true)
        ?? throw new InvalidDataException("ReFS: a directory leaf page was not read.");
      if ((U32(childPage, 0x50 + (int)U32(childPage, 0x50) + 12) & 0x100) != 0)
        throw new NotSupportedException("ReFS: the directory is more than two levels deep; it is not edited.");
      children.Add((page.AsSpan(row + keyOffset, keyLength).ToArray(), metadata.TranslateVirtualLcn(child.Lcns[0])));
    }
    if (children.Count == 0 || children[^1].Separator.Length != 0 || rows.Any(r => children.All(c => c.Leaf != r.PhysicalPageLcn)))
      throw new NotSupportedException("ReFS: the directory's inner page is not laid out as this editor expects.");
    return new RefsDirectoryTree(rows, rootPage, false, children);
  }

  /// <summary>The leaf a row keyed <paramref name="key"/> belongs to.</summary>
  public ulong LeafFor(byte[] key, RefsDirectoryCollation collation) {
    foreach (var (separator, leaf) in this._children)
      if (separator.Length == 0 || collation.Compare(key, separator) <= 0) return leaf;
    return this._children[^1].Leaf;
  }

  /// <summary>The sorted position of <paramref name="key"/> among the rows of <paramref name="leaf"/>, leaving out <paramref name="excluding"/>.</summary>
  public int PositionIn(ulong leaf, byte[] key, RefsDirectoryCollation collation, params RefsBTreeRow[] excluding)
    => this.Rows.Count(r => r.PhysicalPageLcn == leaf && !excluding.Contains(r) && collation.Compare(r.Key, key) < 0);

  public int RowsIn(ulong leaf) => this.Rows.Count(r => r.PhysicalPageLcn == leaf);

  /// <summary>
  /// Moves the root's table row count by <paramref name="delta"/> after rows were inserted or
  /// deleted in the leaves below it (a single-leaf root is kept by <see cref="RefsPageEditor"/>).
  /// </summary>
  public void AdjustRowCount(RefsMetadataGraph graph, int delta) {
    if (this.IsSingleLeaf || delta == 0) return;
    var page = graph.ReadPage(this.RootPage);
    if (BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(0x54)) != 0x28 || BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(0x70)) != (ulong)this.Rows.Count)
      throw new InvalidDataException("ReFS: the directory's root page does not hold its row count where expected.");
    BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x70), (ulong)(this.Rows.Count + delta));
    graph.WritePage(this.RootPage, page);
  }

  /// <summary>Refuses an edit a multi-leaf directory cannot take in place: a leaf left empty.</summary>
  public void RequireNotEmptied(ulong leaf, int removed) {
    if (!this.IsSingleLeaf && this.RowsIn(leaf) <= removed)
      throw new NotSupportedException("ReFS: the edit would empty a page of the directory; merging pages is not made in place.");
  }

  private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
}
