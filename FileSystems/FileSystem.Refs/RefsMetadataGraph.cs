#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal sealed record RefsMetadataPageInfo(
  ulong PhysicalHead,
  IReadOnlyList<ulong> PhysicalSlots,
  bool VirtualAddresses,
  ulong TableId) {

  public bool IsContiguous {
    get {
      for (var i = 1; i < this.PhysicalSlots.Count; ++i)
        if (this.PhysicalSlots[i] != this.PhysicalSlots[0] + (ulong)i) return false;
      return this.PhysicalSlots.Count > 0;
    }
  }
}

/// <summary>
/// Reachability graph for live ReFS metadata pages. It records both directions:
/// where every child page-reference lives, and how every reachable metadata page
/// is physically laid out. That lets an offline writer refresh Merkle checksums
/// after content edits and also relocate the page itself while rewriting the
/// correct physical/virtual address in its parent and self descriptor.
/// </summary>
internal sealed class RefsMetadataGraph {
  private readonly Stream _stream;
  private readonly RefsMetadataReader _metadata;
  private readonly Dictionary<ulong, ulong[]> _pageSlots = [];
  private readonly Dictionary<ulong, RefsMetadataPageInfo> _pages = [];
  private readonly Dictionary<ulong, List<ParentReference>> _parents = [];
  private readonly Dictionary<ulong, int> _depth = [];
  private readonly HashSet<ulong> _scanned = [];
  private readonly int[] _rootDescriptorOffsets = new int[13];

  private sealed record ParentReference(
    bool IsCheckpoint,
    ulong ParentHead,
    int ReferenceOffset,
    int ReferenceLength);

  public RefsMetadataGraph(Stream stream, RefsMetadataReader metadata) {
    this._stream = stream;
    this._metadata = metadata;
    this.Build();
  }

  public RefsMetadataReader Metadata => this._metadata;
  public IReadOnlyCollection<RefsMetadataPageInfo> Pages => this._pages.Values;

  public bool TryGetPage(ulong physicalHead, out RefsMetadataPageInfo page)
    => this._pages.TryGetValue(physicalHead, out page!);

  public IReadOnlyList<ulong> GetPageSlots(ulong physicalHead) {
    if (this._pageSlots.TryGetValue(physicalHead, out var slots)) return slots;
    var count = this._metadata.PageSize / this._metadata.ClusterSize;
    var contiguous = new ulong[count];
    for (var i = 0; i < count; ++i) contiguous[i] = checked(physicalHead + (ulong)i);
    return contiguous;
  }

  public byte[] ReadPage(ulong physicalHead) {
    var slots = this.GetPageSlots(physicalHead);
    return this.ReadPageAt(slots);
  }

  public void WritePage(ulong physicalHead, ReadOnlySpan<byte> page) {
    var slots = this.GetPageSlots(physicalHead);
    this.WritePageAt(slots, page);
  }

  /// <summary>
  /// Repoints a live MSB+ metadata page after its bytes have already been copied
  /// to a new contiguous physical page. Parent references and the page's own LCN
  /// tuple are rewritten in the address space that tree uses: VLCNs for ordinary
  /// ReFS trees, PLCNs for Container-Table/Small-Allocator bootstrap trees.
  /// </summary>
  public void RelocatePage(ulong oldPhysicalHead, ulong newPhysicalHead) {
    if (oldPhysicalHead == newPhysicalHead) return;
    if (!this._pages.TryGetValue(oldPhysicalHead, out var info))
      throw new InvalidOperationException($"ReFS metadata page 0x{oldPhysicalHead:X} is not reachable from the active checkpoint.");
    if (!info.IsContiguous)
      throw new NotSupportedException("This generic metadata move requires a contiguous source MSB+ page; use the ReFS placement manager for scattered page slots.");

    var slotCount = this._metadata.PageSize / this._metadata.ClusterSize;
    if (info.PhysicalSlots.Count != slotCount)
      throw new InvalidDataException("ReFS metadata page has an unexpected number of physical slots.");

    var newPhysicalSlots = new ulong[slotCount];
    for (var i = 0; i < slotCount; ++i)
      newPhysicalSlots[i] = checked(newPhysicalHead + (ulong)i);

    var referenceLcns = new ulong[slotCount];
    for (var i = 0; i < slotCount; ++i) {
      if (!info.VirtualAddresses) {
        referenceLcns[i] = newPhysicalSlots[i];
        continue;
      }
      if (!this._metadata.TryPhysicalToVirtualLcn(newPhysicalSlots[i], out var virtualLcn))
        throw new InvalidOperationException(
          $"ReFS target PLCN 0x{newPhysicalSlots[i]:X} has no virtual-container address for this metadata tree.");
      referenceLcns[i] = virtualLcn;
    }

    var movedPage = this.ReadPageAt(newPhysicalSlots);
    if (movedPage.Length < 0x50 || !movedPage.AsSpan(0, 4).SequenceEqual("MSB+"u8))
      throw new InvalidDataException("Relocated ReFS metadata bytes are not an MSB+ page.");

    // MSB+ self-LCN slots use the same address space as the reference that
    // reaches the page. Ordinary trees therefore carry VLCNs here; the three
    // bootstrap-real trees carry physical LCNs. Never write a transient PLCN
    // tuple into a virtually addressed page.
    for (var i = 0; i < 4; ++i) {
      var lcn = i < referenceLcns.Length ? referenceLcns[i] : 0UL;
      BinaryPrimitives.WriteUInt64LittleEndian(movedPage.AsSpan(0x20 + i * 8, 8), lcn);
    }
    this.WritePageAt(newPhysicalSlots, movedPage);

    var changedParents = new HashSet<ulong>();
    var directCheckpointParents = new List<ParentReference>();
    if (!this._parents.TryGetValue(oldPhysicalHead, out var parents) || parents.Count == 0)
      throw new InvalidDataException("ReFS metadata page has no live parent reference.");

    foreach (var parent in parents) {
      if (parent.IsCheckpoint) {
        directCheckpointParents.Add(parent);
        continue;
      }

      var parentPage = this.ReadPage(parent.ParentHead);
      if (parent.ReferenceOffset < 0 || parent.ReferenceOffset + parent.ReferenceLength > parentPage.Length)
        throw new InvalidDataException("ReFS parent page reference lies outside its page.");
      var reference = parentPage.AsSpan(parent.ReferenceOffset, parent.ReferenceLength);
      RepointReference(reference, referenceLcns);
      RefsChecksum.RefreshPageReference(reference, movedPage);
      this.WritePage(parent.ParentHead, parentPage);
      changedParents.Add(parent.ParentHead);
    }

    // First propagate any changed ordinary parent pages. This may itself update
    // the checkpoint; applying direct-root edits afterwards avoids overwriting
    // those freshly-computed root digests with a stale checkpoint buffer.
    if (changedParents.Count > 0)
      this.RefreshChecksumPaths(changedParents);

    if (directCheckpointParents.Count > 0) {
      var checkpoint = this.ReadCheckpoint();
      foreach (var parent in directCheckpointParents) {
        if (parent.ReferenceOffset < 0 || parent.ReferenceOffset + parent.ReferenceLength > checkpoint.Length)
          throw new InvalidDataException("ReFS checkpoint root reference lies outside CHKP.");
        var reference = checkpoint.AsSpan(parent.ReferenceOffset, parent.ReferenceLength);
        RepointReference(reference, referenceLcns);
        RefsChecksum.RefreshPageReference(reference, movedPage);
      }
      var selfOffset = checked((int)ReadU32(checkpoint, 0x58));
      RefsChecksum.RefreshSelfChecksum(
        checkpoint,
        this._metadata.ClusterSize,
        selfOffset,
        checked((int)this._metadata.PageReferenceSize));
      this.WriteCheckpoint(checkpoint);
    }

    this._stream.Flush();
  }

  /// <summary>
  /// Refreshes every page-reference checksum on paths from the changed pages to
  /// CHKP, deepest pages first, then refreshes the checkpoint self checksum.
  /// </summary>
  public void RefreshChecksumPaths(IEnumerable<ulong> changedPageHeads) {
    var pending = changedPageHeads.Where(h => h != 0).ToHashSet();
    if (pending.Count == 0) return;
    byte[]? checkpoint = null;
    var checkpointDirty = false;

    while (pending.Count > 0) {
      var childHead = pending.MaxBy(h => this._depth.GetValueOrDefault(h, 0));
      pending.Remove(childHead);
      var childPage = this.ReadPage(childHead);
      if (!this._parents.TryGetValue(childHead, out var parents)) continue;

      foreach (var parent in parents) {
        if (parent.IsCheckpoint) {
          checkpoint ??= this.ReadCheckpoint();
          if (parent.ReferenceOffset < 0 || parent.ReferenceOffset + parent.ReferenceLength > checkpoint.Length)
            throw new InvalidDataException("ReFS checkpoint root reference lies outside CHKP.");
          RefsChecksum.RefreshPageReference(
            checkpoint.AsSpan(parent.ReferenceOffset, parent.ReferenceLength), childPage);
          checkpointDirty = true;
          continue;
        }

        var parentPage = this.ReadPage(parent.ParentHead);
        if (parent.ReferenceOffset < 0 || parent.ReferenceOffset + parent.ReferenceLength > parentPage.Length)
          throw new InvalidDataException("ReFS parent page reference lies outside its page.");
        RefsChecksum.RefreshPageReference(
          parentPage.AsSpan(parent.ReferenceOffset, parent.ReferenceLength), childPage);
        this.WritePage(parent.ParentHead, parentPage);
        pending.Add(parent.ParentHead);
      }
    }

    if (!checkpointDirty || checkpoint == null) return;
    var selfOffset = checked((int)ReadU32(checkpoint, 0x58));
    RefsChecksum.RefreshSelfChecksum(
      checkpoint,
      this._metadata.ClusterSize,
      selfOffset,
      checked((int)this._metadata.PageReferenceSize));
    this.WriteCheckpoint(checkpoint);
    this._stream.Flush();
  }

  private void Build() {
    var checkpoint = this.ReadCheckpoint();
    var flags = ReadU32(checkpoint, 0x78);
    var rootCount = checked((int)ReadU32(checkpoint, 0x90));
    var offsetsBase = (flags & 0x0200) != 0 ? checked((int)ReadU32(checkpoint, 0x94)) : 0x94;
    if (rootCount < 13 || offsetsBase < 0 || offsetsBase + rootCount * 4 > checkpoint.Length)
      throw new InvalidDataException("ReFS checkpoint root directory is malformed.");

    for (var i = 0; i < 13; ++i)
      this._rootDescriptorOffsets[i] = checked((int)ReadU32(checkpoint, offsetsBase + i * 4));

    for (var i = 0; i < 13; ++i) {
      var reference = this._metadata.Roots[i];
      if (reference.Lcns.Count == 0) continue;
      var virtualAddresses = i is not (7 or 8 or 12);
      var slots = this.Resolve(reference, virtualAddresses);
      if (slots.Length == 0) continue;
      var descriptorOffset = this._rootDescriptorOffsets[i];
      if (descriptorOffset <= 0 || descriptorOffset + this._metadata.PageReferenceSize > checkpoint.Length) continue;
      this.RegisterPage(slots, depth: 0, virtualAddresses);
      this.AddParent(slots[0], new ParentReference(
        IsCheckpoint: true,
        ParentHead: this._metadata.ActiveCheckpointLcn,
        ReferenceOffset: descriptorOffset,
        ReferenceLength: checked((int)this._metadata.PageReferenceSize)));
      this.ScanTree(slots, virtualAddresses, depth: 0);
    }

    // Per-object trees are rooted by page references embedded in Object-Table
    // leaf values. Those references are virtual, exactly like ordinary roots.
    foreach (var row in this._metadata.WalkRoot(0)) {
      if (row.Value.Length < 0x20 + this._metadata.PageReferenceSize) continue;
      var reference = RefsPageReference.Parse(row.Value.AsSpan(0x20));
      if (reference.Lcns.Count == 0) continue;
      ulong[] slots;
      try { slots = this.Resolve(reference, virtualAddresses: true); }
      catch (InvalidDataException) { continue; }
      if (slots.Length == 0) continue;
      this.RegisterPage(slots, depth: 1, virtualAddresses: true);
      this.AddParent(slots[0], new ParentReference(
        IsCheckpoint: false,
        ParentHead: row.PhysicalPageLcn,
        ReferenceOffset: row.RowOffset + row.ValueOffset + 0x20,
        ReferenceLength: checked((int)this._metadata.PageReferenceSize)));
      this.ScanTree(slots, virtualAddresses: true, depth: 1);
    }
  }

  private void ScanTree(ulong[] physicalSlots, bool virtualAddresses, int depth) {
    if (physicalSlots.Length == 0) return;
    var head = physicalSlots[0];
    this.RegisterPage(physicalSlots, depth, virtualAddresses);
    if (!this._scanned.Add(head)) return;

    var page = this.ReadPage(head);
    if (page.Length < 0x80 || !page.AsSpan(0, 4).SequenceEqual("MSB+"u8)) return;
    var tableId = page.Length >= 0x50
      ? BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(0x48, 8))
      : 0UL;
    this._pages[head] = new RefsMetadataPageInfo(head, physicalSlots, virtualAddresses, tableId);

    var nodeOffset = 0x50 + checked((int)ReadU32(page, 0x50));
    if (nodeOffset < 0x50 || nodeOffset + 40 > page.Length) return;
    var nodeFlags = ReadU32(page, nodeOffset + 0x0C);
    if ((nodeFlags & 0x100) == 0) return;

    var indexStart = nodeOffset + checked((int)ReadU32(page, nodeOffset + 0x10));
    var indexEnd = nodeOffset + checked((int)ReadU32(page, nodeOffset + 0x20));
    if (indexStart < nodeOffset || indexEnd < indexStart || indexEnd > page.Length || ((indexEnd - indexStart) & 3) != 0)
      return;

    for (var p = indexStart; p < indexEnd; p += 4) {
      var encoded = ReadU32(page, p);
      var rowOffset = nodeOffset + (int)(encoded & 0xFFFF);
      if (rowOffset < nodeOffset || rowOffset + 16 > page.Length) continue;
      var rowSize = checked((int)ReadU32(page, rowOffset));
      var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 0x0A, 2));
      var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 0x0C, 2));
      if (rowSize < 16 || rowOffset + rowSize > page.Length || valueOffset + valueLength > rowSize) continue;
      if (valueLength < 32) continue;

      var reference = RefsPageReference.Parse(page.AsSpan(rowOffset + valueOffset, valueLength));
      if (reference.Lcns.Count == 0) continue;
      ulong[] childSlots;
      try { childSlots = this.Resolve(reference, virtualAddresses); }
      catch (InvalidDataException) { continue; }
      if (childSlots.Length == 0) continue;
      this.RegisterPage(childSlots, depth + 1, virtualAddresses);
      this.AddParent(childSlots[0], new ParentReference(
        IsCheckpoint: false,
        ParentHead: head,
        ReferenceOffset: rowOffset + valueOffset,
        ReferenceLength: valueLength));
      this.ScanTree(childSlots, virtualAddresses, depth + 1);
    }
  }

  private ulong[] Resolve(RefsPageReference reference, bool virtualAddresses) {
    var result = new ulong[reference.Lcns.Count];
    for (var i = 0; i < result.Length; ++i)
      result[i] = virtualAddresses ? this._metadata.TranslateVirtualLcn(reference.Lcns[i]) : reference.Lcns[i];
    return result;
  }

  private void RegisterPage(ulong[] slots, int depth, bool virtualAddresses) {
    if (slots.Length == 0) return;
    this._pageSlots[slots[0]] = slots;
    if (!this._depth.TryGetValue(slots[0], out var oldDepth) || depth > oldDepth)
      this._depth[slots[0]] = depth;
    if (this._pages.TryGetValue(slots[0], out var existing) && existing.VirtualAddresses != virtualAddresses)
      throw new InvalidDataException("A live ReFS metadata page is referenced through incompatible address spaces.");
    if (!this._pages.ContainsKey(slots[0]))
      this._pages[slots[0]] = new RefsMetadataPageInfo(slots[0], slots, virtualAddresses, 0);
  }

  private void AddParent(ulong childHead, ParentReference parent) {
    if (!this._parents.TryGetValue(childHead, out var list))
      this._parents[childHead] = list = [];
    if (!list.Contains(parent)) list.Add(parent);
  }

  private byte[] ReadPageAt(IReadOnlyList<ulong> slots) {
    var page = new byte[this._metadata.PageSize];
    var written = 0;
    foreach (var lcn in slots) {
      if (written >= page.Length) break;
      var offset = checked((long)lcn * this._metadata.ClusterSize);
      if (offset < 0 || offset + this._metadata.ClusterSize > this._stream.Length)
        throw new InvalidDataException("ReFS metadata page points outside the image.");
      this._stream.Position = offset;
      var take = Math.Min(this._metadata.ClusterSize, page.Length - written);
      this._stream.ReadExactly(page.AsSpan(written, take));
      written += take;
    }
    if (written != page.Length) throw new InvalidDataException("ReFS metadata page is incomplete.");
    return page;
  }

  private void WritePageAt(IReadOnlyList<ulong> slots, ReadOnlySpan<byte> page) {
    if (page.Length != this._metadata.PageSize)
      throw new ArgumentException("ReFS metadata writes must contain exactly one metadata page.", nameof(page));
    var consumed = 0;
    foreach (var lcn in slots) {
      if (consumed >= page.Length) break;
      var offset = checked((long)lcn * this._metadata.ClusterSize);
      if (offset < 0 || offset + this._metadata.ClusterSize > this._stream.Length)
        throw new InvalidDataException("ReFS metadata page points outside the image.");
      var take = Math.Min(this._metadata.ClusterSize, page.Length - consumed);
      this._stream.Position = offset;
      this._stream.Write(page.Slice(consumed, take));
      consumed += take;
    }
    if (consumed != page.Length) throw new InvalidDataException("ReFS metadata page write was incomplete.");
  }

  private byte[] ReadCheckpoint() {
    var page = new byte[this._metadata.PageSize];
    var clusters = this._metadata.PageSize / this._metadata.ClusterSize;
    for (var i = 0; i < clusters; ++i) {
      var lcn = checked(this._metadata.ActiveCheckpointLcn + (ulong)i);
      var offset = checked((long)lcn * this._metadata.ClusterSize);
      if (offset + this._metadata.ClusterSize > this._stream.Length)
        throw new InvalidDataException("ReFS checkpoint lies outside the image.");
      this._stream.Position = offset;
      this._stream.ReadExactly(page.AsSpan(i * this._metadata.ClusterSize, this._metadata.ClusterSize));
    }
    return page;
  }

  private void WriteCheckpoint(ReadOnlySpan<byte> page) {
    var clusters = this._metadata.PageSize / this._metadata.ClusterSize;
    for (var i = 0; i < clusters; ++i) {
      var lcn = checked(this._metadata.ActiveCheckpointLcn + (ulong)i);
      this._stream.Position = checked((long)lcn * this._metadata.ClusterSize);
      this._stream.Write(page.Slice(i * this._metadata.ClusterSize, this._metadata.ClusterSize));
    }
  }

  private static void RepointReference(Span<byte> reference, IReadOnlyList<ulong> lcns) {
    if (reference.Length < 32) throw new InvalidDataException("ReFS page reference is too short for its LCN slots.");
    for (var i = 0; i < 4; ++i) {
      var lcn = i < lcns.Count ? lcns[i] : 0UL;
      BinaryPrimitives.WriteUInt64LittleEndian(reference.Slice(i * 8, 8), lcn);
    }
  }

  private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset)
    => offset >= 0 && offset + 4 <= bytes.Length
      ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4))
      : throw new InvalidDataException("ReFS metadata field lies outside its page.");
}
