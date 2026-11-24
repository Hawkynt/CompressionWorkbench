using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Replaces a file with contents of a different length, the way Windows Server 2022/2025 do it
/// for a file held in its directory row (measured by having Windows make the same edit): the
/// clusters the file keeps are overwritten in place, a longer file gets one more extent entry
/// for a newly allocated run, a shorter one loses its tail entries (the cut entry shortened) and
/// those clusters are freed; then the size fields are set. Everything lives in the directory
/// row's value, whose length does not change:
/// <list type="bullet">
/// <item>value+0x58 logical size and value+0x60 allocated size;</item>
/// <item>the data attribute's [allocated, size, valid length, allocated] (u64 each), with the
/// extent count (u32) 0x10 bytes before them;</item>
/// <item>the embedded extent leaf: header [data start 0x28, data end, free, flags 0x0600, index
/// start, count, ..., index end at +0x20], 24-byte entries [VLCN u64, flags u32, VCN u32,
/// u32 0, cluster count u32] from +0x28, and an index of (offset | 0xFFFF0000) per entry, in
/// order, ending at the index end.</item>
/// </list>
/// </summary>
/// <remarks>
/// Refused before the first write: anything <see cref="RefsInPlaceOverwrite"/> refuses besides
/// the length, files with their own object, an empty result, extent tables that are not one
/// embedded leaf of plain 24-byte entries, a leaf without room for another entry, size fields
/// that are not found exactly once, and growth the allocator row holding the file's last
/// cluster cannot satisfy with one contiguous free run.
/// </remarks>
internal static class RefsInPlaceResize {

  internal sealed record Plan(
    RefsBTreeRow Row,
    byte[] NewValue,
    IReadOnlyList<ulong> DataClusters,
    IReadOnlyList<ulong> Allocate,
    IReadOnlyList<ulong> Free,
    RefsAllocatorWriter? Allocator,
    ulong DirectoryPage);

  public static Plan Prepare(Stream image, RefsMetadataReader metadata, RefsMetadataGraph graph, string path, long newLength) {
    var normalized = path.Replace('\\', '/').Trim('/');
    if (newLength <= 0) throw new NotSupportedException($"ReFS: emptying '{normalized}' is not made in place.");
    if (newLength > Array.MaxLength) throw new NotSupportedException("ReFS: replacements over 2 GiB are not made in place.");
    var file = new RefsNamespaceReader(metadata).ReadAll()
      .SingleOrDefault(f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase))
      ?? throw new NotSupportedException($"ReFS: '{normalized}' does not exist; creating files is refused.");
    if (file.IsDirectory || file.IsResident || file.Extents.Count == 0)
      throw new NotSupportedException($"ReFS: '{normalized}' is not an extent-backed file.");
    if (file.Extents.Any(static e => e.IsSparse || (e.Flags >> 16) != 0x18))
      throw new NotSupportedException($"ReFS: '{normalized}' has sparse runs or an integrity stream; it is not resized in place.");

    var parent = normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : "";
    var name = normalized[(normalized.LastIndexOf('/') + 1)..];
    var root = RefsInPlaceRename.ResolveDirectoryRoot(metadata, parent);
    var rows = metadata.WalkTree(root, virtualAddresses: true).ToList();
    var page = rows.Count > 0 ? rows[0].PhysicalPageLcn : 0;
    if (rows.Count == 0 || rows.Any(r => r.PhysicalPageLcn != page) || metadata.TranslateVirtualLcn(root.Lcns[0]) != page)
      throw new NotSupportedException("ReFS: the directory spans more than one page; only single-page directories are edited.");
    var row = rows.SingleOrDefault(r => r.Key.Length == 4 + name.Length * 2 && BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x30
        && BinaryPrimitives.ReadUInt16LittleEndian(r.Key.AsSpan(2)) == 1
        && string.Equals(Encoding.Unicode.GetString(r.Key, 4, r.Key.Length - 4), name, StringComparison.OrdinalIgnoreCase))
      ?? throw new NotSupportedException($"ReFS: '{normalized}' is not a file held in its directory row.");

    var value = row.Value.ToArray();
    var cs = (ulong)metadata.ClusterSize;
    var oldSize = (ulong)file.Size;
    var oldAlloc = U64(value, 0x60);
    if (U64(value, 0x58) != oldSize || oldAlloc % cs != 0 || oldAlloc == 0)
      throw new NotSupportedException($"ReFS: the size fields of '{normalized}' are not where this editor expects them.");
    var oldClusters = oldAlloc / cs;

    // The data attribute's size block, exactly once.
    var block = FindOnce(value, [oldAlloc, oldSize, oldSize, oldAlloc], 0x68);
    if (block < 0) throw new NotSupportedException($"ReFS: the data attribute sizes of '{normalized}' were not found exactly once.");
    var countField = block - 0x10;

    // The embedded leaf: its header, entries and index.
    var leaf = FindLeaf(value, (uint)file.Extents.Count);
    if (leaf < 0 || U32(value, countField) != (uint)file.Extents.Count)
      throw new NotSupportedException($"ReFS: the extent table of '{normalized}' is not one embedded leaf this editor rewrites.");
    var entries = new List<(ulong Vlcn, uint Flags, uint Vcn, uint Run)>();
    for (var i = 0; i < file.Extents.Count; ++i) {
      var e = leaf + 0x28 + i * 24;
      entries.Add((U64(value, e), U32(value, e + 8), U32(value, e + 12), U32(value, e + 20)));
    }
    ulong cover = 0;
    foreach (var (_, flags, vcn, run) in entries) {
      if (vcn != cover || (flags >> 16) != 0x18) throw new NotSupportedException($"ReFS: the extent table of '{normalized}' is not in file order.");
      cover += run;
    }
    if (cover != oldClusters) throw new NotSupportedException($"ReFS: the extents of '{normalized}' do not cover its allocation.");

    var newClusters = ((ulong)newLength + cs - 1) / cs;
    var allocate = new List<ulong>();
    var free = new List<ulong>();
    RefsAllocatorWriter? allocator = null;
    if (newClusters != oldClusters) allocator = MediumAllocator(metadata, graph, file, normalized);
    var refcount = new RefsBlockRefcount(metadata);

    if (newClusters > oldClusters) {
      var need = checked((int)(newClusters - oldClusters));
      if (U32(value, leaf + 8) < 28) throw new NotSupportedException($"ReFS: the extent table of '{normalized}' has no room for another entry.");
      var last = entries[^1];
      var lastPhysical = metadata.TranslateVirtualLcn(last.Vlcn + last.Run - 1);
      var (rowStart, rowLength) = allocator!.RangeOfPhysical(lastPhysical)
        ?? throw new NotSupportedException("ReFS: the allocator row holding the file's last cluster was not found.");
      if (!allocator.TryFindFreeRun(need, p => p >= rowStart && p < rowStart + rowLength, out var start))
        throw new NotSupportedException($"ReFS: the allocator row holding '{normalized}' has no free run of {need} clusters.");
      for (var i = 0; i < need; ++i) allocate.Add(start + (ulong)i);
      if (!metadata.TryPhysicalToVirtualLcn(start, out var vstart))
        throw new NotSupportedException("ReFS: the new run has no virtual address.");
      for (var i = 1; i < need; ++i)
        if (!metadata.TryPhysicalToVirtualLcn(start + (ulong)i, out var v) || v != vstart + (ulong)i)
          throw new NotSupportedException("ReFS: the new run crosses a container boundary.");
      var at = leaf + 0x28 + entries.Count * 24;
      var dataEnd = leaf + (int)U32(value, leaf + 4);
      if (at != dataEnd) throw new NotSupportedException("ReFS: the extent leaf's entries are not packed as this editor expects.");
      entries.Add((vstart, last.Flags, (uint)oldClusters, (uint)need));
    } else if (newClusters < oldClusters) {
      var keep = new List<(ulong Vlcn, uint Flags, uint Vcn, uint Run)>();
      foreach (var entry in entries) {
        var end = entry.Vcn + (ulong)entry.Run;
        if (entry.Vcn >= newClusters) {
          for (uint i = 0; i < entry.Run; ++i) free.Add(metadata.TranslateVirtualLcn(entry.Vlcn + i));
        } else if (end > newClusters) {
          var cut = (uint)(newClusters - entry.Vcn);
          for (var i = cut; i < entry.Run; ++i) free.Add(metadata.TranslateVirtualLcn(entry.Vlcn + i));
          keep.Add(entry with { Run = cut });
        } else {
          keep.Add(entry);
        }
      }
      entries = keep;
    }

    // Clusters the file keeps or gains, in file order, for the data write.
    var data = new List<ulong>();
    foreach (var (vlcn, _, _, run) in entries)
      for (uint i = 0; i < run && data.Count < (int)newClusters; ++i)
        data.Add(metadata.TranslateVirtualLcn(vlcn + i));
    foreach (var lcn in data.Concat(free))
      if (!allocate.Contains(lcn) && refcount.IsSharedPhysicalRange(lcn, 1))
        throw new NotSupportedException($"ReFS: '{normalized}' shares clusters with another stream; it is not resized in place.");

    // The new value: sizes, count, leaf header, entries and index.
    var newAlloc = newClusters * cs;
    W64(value, 0x58, (ulong)newLength);
    W64(value, 0x60, newAlloc);
    W64(value, block, newAlloc);
    W64(value, block + 8, (ulong)newLength);
    W64(value, block + 16, (ulong)newLength);
    W64(value, block + 24, newAlloc);
    W32(value, countField, (uint)entries.Count);
    var oldCount = (int)U32(value, leaf + 20);
    var indexEnd = (int)U32(value, leaf + 0x20);
    var delta = entries.Count - oldCount;
    W32(value, leaf + 4, U32(value, leaf + 4) + (uint)(delta * 24));
    W32(value, leaf + 8, (uint)((int)U32(value, leaf + 8) - delta * 28));
    W32(value, leaf + 16, (uint)(indexEnd - entries.Count * 4));
    W32(value, leaf + 20, (uint)entries.Count);
    var area = value.AsSpan(leaf + 0x28, indexEnd - 0x28);
    area.Clear();
    for (var i = 0; i < entries.Count; ++i) {
      var e = leaf + 0x28 + i * 24;
      W64(value, e, entries[i].Vlcn);
      W32(value, e + 8, entries[i].Flags);
      W32(value, e + 12, entries[i].Vcn);
      W32(value, e + 16, 0);
      W32(value, e + 20, entries[i].Run);
      W32(value, leaf + indexEnd - (entries.Count - i) * 4, (uint)(0x28 + i * 24) | 0xFFFF0000u);
    }
    if (!RefsPageEditor.CanReplaceValue(graph, row, value.Length))
      throw new NotSupportedException("ReFS: the directory page cannot take the rewritten row.");
    return new Plan(row, value, data, allocate, free, allocator, page);
  }

  public static void Apply(Stream image, RefsMetadataReader metadata, RefsMetadataGraph graph, Plan plan, byte[] data) {
    if (plan.Allocate.Count > 0) plan.Allocator!.SetAllocated(plan.Allocate, allocated: true);
    RefsInPlaceOverwrite.Apply(image, metadata.ClusterSize, plan.DataClusters, data);
    graph.RefreshChecksumPaths([RefsPageEditor.ReplaceValue(graph, plan.Row, plan.NewValue)]);
    if (plan.Free.Count > 0) plan.Allocator!.SetAllocated(plan.Free, allocated: false);
  }

  private static RefsAllocatorWriter MediumAllocator(RefsMetadataReader metadata, RefsMetadataGraph graph, RefsFileRecord file, string name) {
    var clusters = file.Extents.SelectMany(e => Enumerable.Range(0, (int)e.ClusterCount).Select(i => metadata.TranslateVirtualLcn(e.VirtualLcn + (ulong)i))).ToList();
    try {
      var allocator = new RefsAllocatorWriter(metadata, graph, RefsAllocatorTier.Medium, physicalAddressing: true);
      if (clusters.All(allocator.CoversPhysical) && allocator.AreAllocated(clusters, expectedAllocated: true)) return allocator;
    } catch (Exception e) when (e is InvalidOperationException or InvalidDataException) {
      // fall through
    }
    throw new NotSupportedException($"ReFS: the Medium Allocator does not show the clusters of '{name}' the way Windows-formatted volumes do; it is not resized.");
  }

  private static int FindOnce(byte[] value, ulong[] pattern, int from) {
    var found = -1;
    for (var p = from; p + pattern.Length * 8 <= value.Length; p += 4) {
      var match = true;
      for (var i = 0; i < pattern.Length && match; ++i) match = U64(value, p + i * 8) == pattern[i];
      if (!match) continue;
      if (found >= 0) return -1;
      found = p;
    }
    return found;
  }

  private static int FindLeaf(byte[] value, uint count) {
    var found = -1;
    for (var p = 0x80; p + 0x28 <= value.Length; p += 4) {
      if (U32(value, p) != 0x28 || U32(value, p + 12) != 0x0600 || U32(value, p + 20) != count) continue;
      var end = U32(value, p + 4);
      var free = U32(value, p + 8);
      var indexStart = U32(value, p + 16);
      var indexEnd = U32(value, p + 0x20);
      if (end != 0x28 + count * 24 || indexStart != indexEnd - count * 4 || free != indexStart - end || p + indexEnd > value.Length) continue;
      if (found >= 0) return -1;
      found = p;
    }
    return found;
  }

  private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
  private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o, 8));
  private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);
  private static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o, 8), v);
}
