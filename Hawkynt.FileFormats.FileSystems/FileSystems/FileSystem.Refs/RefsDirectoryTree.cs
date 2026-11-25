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
/// place; a full leaf below the root is split (<see cref="PlanSplit"/>). Refused: growing a
/// one-page directory a level, a root without room for another leaf, and emptying a leaf
/// (its separator would have to go).
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

  /// <summary>
  /// A planned split of a full leaf below the root: the leaf keeps its first rows, a new page
  /// (next to it, see <see cref="RefsMetadataPageAllocation"/>) takes the rest, and the root
  /// gains an inner row whose separator is the kept half's last key and points at the old leaf,
  /// while the old inner row — its separator still bounding the moved rows — points at the new
  /// page; the root's leaf count (0x68) goes up by one. The halves are chosen so the pending
  /// rows fit in the halves their keys fall into.
  /// </summary>
  public sealed record Split(ulong Leaf, int Keep, byte[] Separator, RefsMetadataPageAllocation Page, RefsDirectoryTree Tree) {
    public IReadOnlyList<ulong> Clusters => this.Page.Physical;

    /// <summary>Writes the split: the new page, the shortened leaf, the root, then every checksum up to the checkpoint.</summary>
    public ulong Apply(Stream image, RefsMetadataReader metadata, RefsMetadataGraph graph) {
      var leafPage = graph.ReadPage(this.Leaf);
      var rows = RefsPageEditor.ReadRows(leafPage).Rows;
      var right = RefsPageEditor.PackRows(this.Page.Stamp(leafPage), rows.Skip(this.Keep).ToList());
      var left = RefsPageEditor.PackRows(leafPage, rows.Take(this.Keep).ToList());
      var rootPage = graph.ReadPage(this.Tree.RootPage);
      var inner = RefsPageEditor.ReadRows(rootPage, allowInner: true).Rows.ToList();
      var at = this.Tree._children.FindIndex(c => c.Leaf == this.Leaf);
      var oldRow = inner[at];
      var rightReference = oldRow.Value.ToArray();
      for (var i = 0; i < 4; ++i)
        BinaryPrimitives.WriteUInt64LittleEndian(rightReference.AsSpan(i * 8), i < this.Page.Virtual.Count ? this.Page.Virtual[i] : 0);
      RefsChecksum.RefreshPageReference(rightReference, right);
      inner[at] = oldRow with { Value = rightReference };
      inner.Insert(at, new RefsPageEditor.LiveRow(0xFFFF0000u, 0, 0, this.Separator, oldRow.Value.ToArray()));
      var newRoot = RefsPageEditor.PackRows(rootPage, inner, allowInner: true);
      BinaryPrimitives.WriteUInt64LittleEndian(newRoot.AsSpan(0x68), BinaryPrimitives.ReadUInt64LittleEndian(newRoot.AsSpan(0x68)) + 1);

      this.Page.Claim(image, metadata, right);
      graph.WritePage(this.Leaf, left);
      graph.WritePage(this.Tree.RootPage, newRoot);
      var head = this.Page.Physical[0];
      new RefsMetadataGraph(image, RefsMetadataReader.Open(image)).RefreshChecksumPaths([this.Leaf, head]);
      return head;
    }
  }

  /// <summary>Plans the split of <paramref name="leaf"/> that makes room for the <paramref name="pending"/> rows (key, bytes with index entry); refuses (writing nothing) when none does.</summary>
  public Split PlanSplit(RefsMetadataReader metadata, RefsMetadataGraph graph, ulong leaf, IReadOnlyList<(byte[] Key, int Cost)> pending, RefsDirectoryCollation collation) {
    if (this.IsSingleLeaf)
      throw new NotSupportedException("ReFS: the directory's only page is full; growing it a level is not made in place.");
    var (rows, capacity) = RefsPageEditor.ReadRows(graph.ReadPage(leaf));
    if (rows.Count < 2) throw new NotSupportedException("ReFS: the directory page is full with fewer than two rows; it is not split.");
    var costs = rows.Select(static r => RefsPageEditor.RowCost(r.Key.Length, r.Value.Length)).ToArray();
    var positions = pending.Select(p => (Position: rows.Count(r => collation.Compare(r.Key, p.Key) < 0), p.Cost)).ToList();
    var best = -1;
    var bestBalance = int.MaxValue;
    for (var keep = 1; keep < rows.Count; ++keep) {
      var left = costs.Take(keep).Sum() + positions.Where(p => p.Position < keep).Sum(static p => p.Cost);
      var right = costs.Skip(keep).Sum() + positions.Where(p => p.Position >= keep).Sum(static p => p.Cost);
      if (left > capacity || right > capacity) continue;
      var balance = Math.Abs(left - right);
      if (balance < bestBalance) { best = keep; bestBalance = balance; }
    }
    if (best < 0) throw new NotSupportedException("ReFS: no split of the directory page makes room for the new row.");
    var separator = rows[best - 1].Key;
    var (inner, innerCapacity) = RefsPageEditor.ReadRows(graph.ReadPage(this.RootPage), allowInner: true);
    var innerCost = inner.Sum(static r => RefsPageEditor.RowCost(r.Key.Length, r.Value.Length)) + RefsPageEditor.RowCost(separator.Length, inner[0].Value.Length);
    if (innerCost > innerCapacity)
      throw new NotSupportedException("ReFS: the directory's root page has no room for another leaf; growing it a level is not made in place.");
    var page = RefsMetadataPageAllocation.Plan(metadata, graph, graph.GetPageSlots(leaf));
    return new Split(leaf, best, separator, page, this);
  }

  private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
}
