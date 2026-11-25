using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Replaces a file with contents of a different length, the way Windows Server 2022/2025 do it
/// for a file held in its directory row (measured by having Windows make the same edit): the
/// clusters the file keeps are overwritten in place, a longer file gets its last entry extended
/// or one more extent entry for a newly allocated run, a shorter one loses its tail entries (the
/// cut entry shortened) and those clusters are freed; then the size fields are set. Everything
/// lives in the directory row's value:
/// <list type="bullet">
/// <item>value+0x58 logical size and value+0x60 allocated size;</item>
/// <item>the data attribute's [allocated, size, valid length, allocated] (u64 each), with the
/// extent count (u32) 0x10 bytes before them;</item>
/// <item>the embedded extent leaf (<see cref="RefsExtentLeaf"/>); an integrity stream's entries
/// carry the CRC32C of each cluster, recomputed for the new contents (the last cluster padded
/// with zeros, as written).</item>
/// </list>
/// On ReFS 3.14 the leaf, and so the row's value, grows or shrinks with its entries.
/// </summary>
/// <remarks>
/// Refused before the first write: anything <see cref="RefsInPlaceOverwrite"/> refuses besides
/// the length, files with their own object, an empty result, extent tables that are not one
/// embedded leaf, a ReFS 3.7 leaf without room for the new entries, size fields that are not
/// found exactly once, and growth the allocator row holding the file's last cluster cannot
/// satisfy with one contiguous free run.
/// </remarks>
internal static class RefsInPlaceResize {

  internal sealed record Plan(
    RefsBTreeRow Row,
    byte[] NewValue,
    IReadOnlyList<ulong> DataClusters,
    IReadOnlyList<ulong> Allocate,
    IReadOnlyList<ulong> Free,
    RefsAllocatorWriter? Allocator,
    bool Integrity);

  public static Plan Prepare(Stream image, RefsMetadataReader metadata, RefsMetadataGraph graph, string path, byte[] data) {
    var normalized = path.Replace('\\', '/').Trim('/');
    var newLength = data.LongLength;
    if (newLength <= 0) throw new NotSupportedException($"ReFS: emptying '{normalized}' is not made in place.");
    if (newLength > Array.MaxLength) throw new NotSupportedException("ReFS: replacements over 2 GiB are not made in place.");
    var file = new RefsNamespaceReader(metadata).ReadAll()
      .SingleOrDefault(f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase))
      ?? throw new NotSupportedException($"ReFS: '{normalized}' does not exist; creating files is refused.");
    if (file.IsDirectory || file.IsResident || file.Extents.Count == 0)
      throw new NotSupportedException($"ReFS: '{normalized}' is not an extent-backed file.");
    if (file.Extents.Any(static e => e.IsSparse))
      throw new NotSupportedException($"ReFS: '{normalized}' has sparse runs; it is not resized in place.");

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

    // The embedded leaf and its entries.
    var leaf = RefsExtentLeaf.Find(value, (uint)file.Extents.Count);
    var entries = leaf < 0 ? null : RefsExtentLeaf.Read(value, leaf);
    if (entries is null || U32(value, countField) != (uint)file.Extents.Count || leaf < block)
      throw new NotSupportedException($"ReFS: the extent table of '{normalized}' is not one embedded leaf this editor rewrites.");
    var integrity = entries[0].IsIntegrity;
    if (entries.Any(e => e.IsIntegrity != integrity))
      throw new NotSupportedException($"ReFS: the extent table of '{normalized}' mixes integrity and plain entries.");
    ulong cover = 0;
    foreach (var entry in entries) {
      if (entry.Vcn != cover) throw new NotSupportedException($"ReFS: the extent table of '{normalized}' is not in file order.");
      cover += entry.Run;
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
      var last = entries[^1];
      var lastPhysical = metadata.TranslateVirtualLcn(last.Vlcn + last.Run - 1);
      // The clusters right after the file's last one, when free and virtually contiguous with
      // it, extend its last entry; otherwise the new run gets an entry of its own.
      var following = Enumerable.Range(1, need).Select(i => lastPhysical + (ulong)i).ToList();
      var extendable = following.All(allocator!.CoversPhysical) && allocator.AreAllocated(following, expectedAllocated: false)
        && following.Select((p, i) => metadata.TryPhysicalToVirtualLcn(p, out var v) && v == last.Vlcn + last.Run + (ulong)i).All(static ok => ok);
      if (extendable) {
        allocate.AddRange(following);
        entries[^1] = last with { Run = last.Run + (uint)need };
      } else {
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
        entries.Add(new RefsExtent(vstart, last.Flags, (uint)oldClusters, (uint)need, integrity ? [] : null));
      }
    } else if (newClusters < oldClusters) {
      var keep = new List<RefsExtent>();
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
    // An integrity stream's entries carry the CRC32C of each cluster as it will be written.
    if (integrity)
      entries = entries.Select(e => e with { Crcs = RefsExtentLeaf.ClusterCrcs(data, metadata.ClusterSize, e.Vcn, (int)e.Run) }).ToList();

    // Clusters the file keeps or gains, in file order, for the data write.
    var clusters = new List<ulong>();
    foreach (var entry in entries)
      for (uint i = 0; i < entry.Run && clusters.Count < (int)newClusters; ++i)
        clusters.Add(metadata.TranslateVirtualLcn(entry.Vlcn + i));
    foreach (var lcn in clusters.Concat(free))
      if (!allocate.Contains(lcn) && refcount.IsSharedPhysicalRange(lcn, 1))
        throw new NotSupportedException($"ReFS: '{normalized}' shares clusters with another stream; it is not resized in place.");

    // The new value: sizes, count, then the leaf (which may change the value's length).
    var newAlloc = newClusters * cs;
    W64(value, 0x58, (ulong)newLength);
    W64(value, 0x60, newAlloc);
    W64(value, block, newAlloc);
    W64(value, block + 8, (ulong)newLength);
    W64(value, block + 16, (ulong)newLength);
    W64(value, block + 24, newAlloc);
    W32(value, countField, (uint)entries.Count);
    value = RefsExtentLeaf.Write(value, leaf, entries, normalized);
    if (!RefsPageEditor.CanReplaceValue(graph, row, value.Length))
      throw new NotSupportedException("ReFS: the directory page cannot take the rewritten row.");
    return new Plan(row, value, clusters, allocate, free, allocator, integrity);
  }

  public static void Apply(Stream image, RefsMetadataReader metadata, RefsMetadataGraph graph, Plan plan, byte[] data) {
    if (plan.Allocate.Count > 0) plan.Allocator!.SetAllocated(plan.Allocate, allocated: true);
    if (plan.Integrity) RefsExtentLeaf.WritePadded(image, metadata.ClusterSize, plan.DataClusters, data);
    else RefsInPlaceOverwrite.Apply(image, metadata.ClusterSize, plan.DataClusters, data);
    graph.RefreshChecksumPaths([RefsPageEditor.ReplaceValue(graph, plan.Row, plan.NewValue)]);
    if (plan.Free.Count > 0) plan.Allocator!.SetAllocated(plan.Free, allocated: false);
  }

  private static RefsAllocatorWriter MediumAllocator(RefsMetadataReader metadata, RefsMetadataGraph graph, RefsFileRecord file, string name) {
    var clusters = file.Extents.SelectMany(e => Enumerable.Range(0, (int)e.ClusterCount).Select(i => metadata.TranslateVirtualLcn(e.VirtualLcn + (ulong)i))).ToList();
    return RefsAllocatorWriter.WindowsMedium(metadata, graph, clusters) ?? throw new NotSupportedException($"ReFS: the Medium Allocator does not show the clusters of '{name}' the way Windows-formatted volumes do; it is not resized.");
  }

  internal static int FindOnce(byte[] value, ulong[] pattern, int from) {
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

  private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
  private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o, 8));
  private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);
  private static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o, 8), v);
}
