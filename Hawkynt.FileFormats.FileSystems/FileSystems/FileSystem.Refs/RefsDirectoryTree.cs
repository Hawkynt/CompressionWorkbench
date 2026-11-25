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
/// place; a full leaf below the root is split, and a full one-page directory grows a level
/// (<see cref="PlanSplit"/>). Refused: a root without room for another leaf, and emptying a
/// leaf (its separator would have to go).
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
  public abstract record PlannedSplit {
    /// <summary>The clusters the new pages take.</summary>
    public abstract IReadOnlyList<ulong> Clusters { get; }

    /// <summary>Writes the split and refreshes every checksum up to the checkpoint.</summary>
    public abstract void Apply(Stream image, RefsMetadataReader metadata, RefsMetadataGraph graph);
  }

  public sealed record Split(ulong Leaf, int Keep, byte[] Separator, RefsMetadataPageAllocation Page, RefsDirectoryTree Tree) : PlannedSplit {
    public override IReadOnlyList<ulong> Clusters => this.Page.Physical;

    /// <summary>Writes the split: the new page, the shortened leaf, the root, then every checksum up to the checkpoint.</summary>
    public override void Apply(Stream image, RefsMetadataReader metadata, RefsMetadataGraph graph) {
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
    }
  }

  /// <summary>
  /// A planned growth of a full one-page directory by a level, the shape Windows gives a
  /// directory that outgrew its page: two new leaf pages take the rows (the first half, and the
  /// rest), and the root page becomes an inner node (node flags | 0x101) holding two rows — the
  /// first half's last key pointing at the first leaf, and the empty end key (flags 2; index
  /// word 0xFFFF on ReFS 3.7 and 0 on 3.14) pointing at the second — with its leaf count (0x68)
  /// at 2 and its row count (0x70) unchanged. A leaf page below a root carries no descriptor:
  /// node offset 8 at 0x50, the node at 0x58 running to the page's end.
  /// </summary>
  public sealed record RootSplit(int Keep, byte[] Separator, RefsMetadataPageAllocation Pages, RefsDirectoryTree Tree, bool Current) : PlannedSplit {
    public override IReadOnlyList<ulong> Clusters => this.Pages.AllPhysical.ToList();

    public override void Apply(Stream image, RefsMetadataReader metadata, RefsMetadataGraph graph) {
      var rootPage = graph.ReadPage(this.Tree.RootPage);
      var rows = RefsPageEditor.ReadRows(rootPage).Rows;
      var template = LeafTemplate(rootPage);
      var left = RefsPageEditor.PackRows(this.Pages.Stamp(template, 0), rows.Take(this.Keep).ToList());
      var right = RefsPageEditor.PackRows(this.Pages.Stamp(template, 1), rows.Skip(this.Keep).ToList());

      var root = rootPage.ToArray();
      var node = 0x50 + (int)U32(root, 0x50);
      BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(node + 12), U32(root, node + 12) | 0x101);
      // The leaf count first: a root with leaves keeps its row count when repacked.
      BinaryPrimitives.WriteUInt64LittleEndian(root.AsSpan(0x68), 2);
      var newRoot = RefsPageEditor.PackRows(root, [
        new RefsPageEditor.LiveRow(0xFFFF0000u, 0, 0, this.Separator, Reference(this.Pages.VirtualOf(0), left)),
        new RefsPageEditor.LiveRow(this.Current ? 0u : 0xFFFF0000u, 2, 0, [], Reference(this.Pages.VirtualOf(1), right)),
      ], allowInner: true);

      this.Pages.Claim(image, metadata, left, right);
      graph.WritePage(this.Tree.RootPage, newRoot);
      new RefsMetadataGraph(image, RefsMetadataReader.Open(image)).RefreshChecksumPaths([this.Pages.PhysicalOf(0)[0], this.Pages.PhysicalOf(1)[0]]);
    }

    private static byte[] LeafTemplate(byte[] rootPage) {
      var page = new byte[rootPage.Length];
      rootPage.AsSpan(0, 0x50).CopyTo(page);
      BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0x50), 8);
      const int node = 0x58;
      var indexEnd = page.Length - node;
      BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node), 0x28);
      BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 4), 0x28);
      BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 8), (uint)(indexEnd - 0x28));
      BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 0x10), (uint)indexEnd);
      BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 0x20), (uint)indexEnd);
      return page;
    }

    /// <summary>A page reference: four virtual LCNs, the checksum descriptor (CRC64, 8 bytes) and the page's digest.</summary>
    private static byte[] Reference(IReadOnlyList<ulong> virtualLcns, byte[] page) {
      var reference = new byte[0x30];
      for (var i = 0; i < 4; ++i) BinaryPrimitives.WriteUInt64LittleEndian(reference.AsSpan(i * 8), i < virtualLcns.Count ? virtualLcns[i] : 0);
      reference[0x22] = 2;
      reference[0x23] = 8;
      BinaryPrimitives.WriteUInt32LittleEndian(reference.AsSpan(0x24), 8);
      RefsChecksum.RefreshPageReference(reference, page);
      return reference;
    }
  }

  /// <summary>Plans the split of <paramref name="leaf"/> that makes room for the <paramref name="pending"/> rows (key, bytes with index entry); refuses (writing nothing) when none does.</summary>
  public PlannedSplit PlanSplit(RefsMetadataReader metadata, RefsMetadataGraph graph, ulong leaf, IReadOnlyList<(byte[] Key, int Cost)> pending, RefsDirectoryCollation collation) {
    var page = graph.ReadPage(leaf);
    var (rows, capacity) = RefsPageEditor.ReadRows(page);
    if (rows.Count < 2) throw new NotSupportedException("ReFS: the directory page is full with fewer than two rows; it is not split.");
    // A leaf below a root has its node at 0x58 and the rest of the page; the root's descriptor
    // takes more, so a root's rows have more room once they move down a level.
    if (this.IsSingleLeaf) capacity = page.Length - 0x58 - 0x28;
    var costs = rows.Select(static r => RefsPageEditor.RowCost(r)).ToArray();
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
    if (this.IsSingleLeaf)
      return new RootSplit(best, separator, RefsMetadataPageAllocation.Plan(metadata, graph, graph.GetPageSlots(leaf), count: 2), this, metadata.Header.MinorVersion >= 14);
    var (inner, innerCapacity) = RefsPageEditor.ReadRows(graph.ReadPage(this.RootPage), allowInner: true);
    var innerCost = inner.Sum(static r => RefsPageEditor.RowCost(r)) + RefsPageEditor.RowCost(separator.Length, inner[0].Value.Length);
    if (innerCost > innerCapacity)
      throw new NotSupportedException("ReFS: the directory's root page has no room for another leaf; growing it a level is not made in place.");
    var allocation = RefsMetadataPageAllocation.Plan(metadata, graph, graph.GetPageSlots(leaf));
    return new Split(leaf, best, separator, allocation, this);
  }

  private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
}
