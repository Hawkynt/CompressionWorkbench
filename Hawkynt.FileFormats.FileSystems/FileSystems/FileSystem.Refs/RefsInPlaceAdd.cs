using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Adds a new file to an existing directory the way Windows Server 2022 (ReFS 3.7) does it,
/// measured by having Windows add a file: a name row (type 0x30, kind 1) whose value is the same
/// record an existing file of the directory holds, with its timestamps, sizes, file number
/// (value+0x80) and one extent for a newly allocated run; a file-number row (type 0x20, see
/// <see cref="RefsDirectoryRows"/>); the directory's last file number advanced in its object-table
/// row and, on ReFS 3.7, in its header (type 0x10, value+0x80). The data goes into a free run of the allocator row that
/// holds the template file's clusters. Every page-reference checksum up to the checkpoint is
/// refreshed.
/// </summary>
/// <remarks>
/// Refused before the first write: a name that exists (case-insensitively), a directory whose order
/// <see cref="RefsDirectoryCollation"/> does not reproduce,
/// an empty file, a directory without an extent-backed file to take the record from (an
/// integrity stream gives its kind and its per-cluster CRC32C entries to the new file),
/// directories deeper than a root over leaves, a leaf without room for its row (a page split is
/// not made), and no free run.
/// </remarks>
internal static class RefsInPlaceAdd {

  public static void Add(Stream image, string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(data);
    if (!image.CanSeek || !image.CanWrite) throw new IOException("ReFS: the image must be seekable and writable.");
    var normalized = path.Replace('\\', '/').Trim('/');
    var parent = normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : "";
    var name = normalized[(normalized.LastIndexOf('/') + 1)..];
    if (name.Length is 0 or > 255 || name.IndexOfAny(['\0', '/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name is "." or "..")
      throw new ArgumentException($"ReFS: '{name}' is not a valid file name.", nameof(path));
    if (data.Length == 0) throw new NotSupportedException("ReFS: empty files are not added in place.");

    var metadata = RefsMetadataReader.Open(image);
    var (directoryOid, root) = RefsInPlaceRename.ResolveDirectory(metadata, parent);
    var tree = RefsDirectoryTree.Open(metadata, root);
    var rows = tree.Rows;
    var collation = RefsDirectoryCollation.For(metadata, rows, name);
    var nameKey = new byte[4 + name.Length * 2];
    BinaryPrimitives.WriteUInt16LittleEndian(nameKey, 0x30);
    BinaryPrimitives.WriteUInt16LittleEndian(nameKey.AsSpan(2), 1);
    Encoding.Unicode.GetBytes(name).CopyTo(nameKey, 4);
    if (rows.Any(r => BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x30 && collation.Compare(r.Key, nameKey) == 0))
      throw new IOException($"ReFS: '{normalized}' already exists.");

    // The template: an extent-backed file held in a name row of this directory, whose record
    // this editor already rewrites (one embedded leaf, size block found once). A new file in a
    // directory of integrity streams is one too (Windows gives it the directory's setting).
    // A directory of small files kept in their rows (ReFS 3.14) has none; the root's files are
    // the fallback, the record carrying nothing of its directory.
    var files = new RefsNamespaceReader(metadata).ReadAll();
    var candidates = rows.Select(r => (Row: r, Directory: parent));
    if (parent.Length > 0)
      candidates = candidates.Concat(metadata.WalkTree(RefsInPlaceRename.ResolveDirectoryRoot(metadata, ""), virtualAddresses: true).Select(static r => (Row: r, Directory: "")));
    (RefsBTreeRow Row, RefsFileRecord File, int Block, int Leaf, RefsExtent First)? template = null;
    foreach (var (row, directory) in candidates) {
      if (BinaryPrimitives.ReadUInt16LittleEndian(row.Key) != 0x30 || BinaryPrimitives.ReadUInt16LittleEndian(row.Key.AsSpan(2)) != 1) continue;
      var rowName = Encoding.Unicode.GetString(row.Key, 4, row.Key.Length - 4);
      var filePath = directory.Length == 0 ? rowName : directory + "/" + rowName;
      var file = files.FirstOrDefault(f => string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase));
      if (file is null || file.IsResident || file.Extents.Count == 0 || file.Extents.Any(static e => e.IsSparse)) continue;
      var v = row.Value;
      var size = (ulong)file.Size;
      var alloc = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(0x60));
      if (BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(0x58)) != size) continue;
      var block = RefsInPlaceResize.FindOnce(v, [alloc, size, size, alloc], 0x68);
      var leaf = RefsExtentLeaf.Find(v, (uint)file.Extents.Count);
      if (block < 0 || leaf < block || BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(block - 0x10)) != (uint)file.Extents.Count) continue;
      if (RefsExtentLeaf.Read(v, leaf) is not { Count: > 0 } entries || entries.Any(e => e.IsIntegrity != entries[0].IsIntegrity)) continue;
      template = (row, file, block, leaf, entries[0]);
      break;
    }
    if (template is not { } t) throw new NotSupportedException("ReFS: the directory holds no extent-backed file whose record a new file can take.");

    var cs = (ulong)metadata.ClusterSize;
    var clusterCount = ((ulong)data.Length + cs - 1) / cs;
    var graph = new RefsMetadataGraph(image, metadata);
    var templateClusters = t.File.Extents.SelectMany(e => Enumerable.Range(0, (int)e.ClusterCount)
      .Select(i => metadata.TranslateVirtualLcn(e.VirtualLcn + (ulong)i))).ToList();
    var allocator = RefsAllocatorWriter.WindowsMedium(metadata, graph, templateClusters)
      ?? throw new NotSupportedException("ReFS: the volume's Medium Allocator is not one this editor writes.");
    var (rowStart, rowLength) = allocator.RangeOfPhysical(templateClusters[^1])
      ?? throw new NotSupportedException("ReFS: the allocator row holding the template file was not found.");
    if (!allocator.TryFindFreeRun(checked((int)clusterCount), p => p >= rowStart && p < rowStart + rowLength, out var start))
      throw new NotSupportedException($"ReFS: no free run of {clusterCount} clusters next to the directory's files.");
    var run = Enumerable.Range(0, (int)clusterCount).Select(i => start + (ulong)i).ToList();
    if (!metadata.TryPhysicalToVirtualLcn(start, out var vstart)
        || run.Select((p, i) => metadata.TryPhysicalToVirtualLcn(p, out var v) && v == vstart + (ulong)i).Any(static ok => !ok))
      throw new NotSupportedException("ReFS: the new run crosses a container boundary.");

    // File number: one past the highest in use (and, on ReFS 3.7, the header's counter).
    var header = rows.FirstOrDefault(static r => BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x10);
    var counter = header is not null && metadata.Header.MinorVersion < 14 && header.Value.Length >= 0x88
      ? BinaryPrimitives.ReadUInt64LittleEndian(header.Value.AsSpan(0x80)) : 0UL;
    var highest = rows.Where(static r => BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x20 && r.Key.Length >= 16)
      .Select(static r => BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8))).DefaultIfEmpty(0UL).Max();
    // The directory's object-table row keeps the same counter (RefsDirectoryRows.ObjectRow);
    // it must agree with the rows, or the directory is not one this editor understands.
    var objectRow = RefsDirectoryRows.ObjectRow(metadata, directoryOid)
      ?? throw new NotSupportedException("ReFS: the directory's object-table row was not found.");
    var objectCounter = BinaryPrimitives.ReadUInt64LittleEndian(objectRow.Row.Value.AsSpan(objectRow.CounterOffset));
    if (objectCounter < highest || (counter > 0 && objectCounter != counter))
      throw new NotSupportedException("ReFS: the directory's file-number counters disagree; it is not added to.");
    var fileNumber = Math.Max(Math.Max(counter, highest), objectCounter) + 1;
    var objectValue = objectRow.Row.Value.ToArray();
    BinaryPrimitives.WriteUInt64LittleEndian(objectValue.AsSpan(objectRow.CounterOffset), fileNumber);

    var value = t.Row.Value.ToArray();
    var now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
    for (var o = 0x28; o < 0x48; o += 8) BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(o), now);
    var newAlloc = clusterCount * cs;
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x58), (ulong)data.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x60), newAlloc);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(RefsDirectoryRows.FileNumberOffset), fileNumber);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(t.Block), newAlloc);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(t.Block + 8), (ulong)data.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(t.Block + 16), (ulong)data.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(t.Block + 24), newAlloc);
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(t.Block - 0x10), 1);
    var crcs = t.First.IsIntegrity ? RefsExtentLeaf.ClusterCrcs(data, metadata.ClusterSize, 0, (int)clusterCount) : null;
    value = RefsExtentLeaf.Write(value, t.Leaf, [new RefsExtent(vstart, t.First.Flags, 0u, (uint)clusterCount, crcs)], normalized);

    var idKey = RefsDirectoryRows.IdKey(fileNumber);
    var idValue = RefsDirectoryRows.IdValue(name);
    var templateId = rows.FirstOrDefault(static r => BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x20);
    // The two rows go to the leaves their keys belong to (one leaf, or two of a larger directory).
    var nameLeaf = tree.LeafFor(nameKey, collation);
    var idLeaf = tree.LeafFor(idKey, collation);
    var namePosition = tree.PositionIn(nameLeaf, nameKey, collation);
    var idPosition = tree.PositionIn(idLeaf, idKey, collation);
    var room = nameLeaf == idLeaf
      ? RefsPageEditor.CanInsertRow(graph, nameLeaf, namePosition, nameKey.Length + idKey.Length + 32, value.Length + idValue.Length)
      : RefsPageEditor.CanInsertRow(graph, nameLeaf, namePosition, nameKey.Length, value.Length)
        && RefsPageEditor.CanInsertRow(graph, idLeaf, idPosition, idKey.Length, idValue.Length);
    if (!room) {
      // A full leaf of a larger directory is split first, when one split makes room for every
      // row that goes to it; the add is then planned afresh on the split directory.
      var nameCost = RefsPageEditor.RowCost(nameKey.Length, value.Length);
      var idCost = RefsPageEditor.RowCost(idKey.Length, idValue.Length);
      var nameFits = RefsPageEditor.CanInsertRow(graph, nameLeaf, namePosition, nameKey.Length, value.Length);
      var idFits = RefsPageEditor.CanInsertRow(graph, idLeaf, idPosition, idKey.Length, idValue.Length);
      var split = nameLeaf == idLeaf
        ? tree.PlanSplit(metadata, graph, nameLeaf, [(idKey, idCost), (nameKey, nameCost)], collation)
        : !nameFits && idFits ? tree.PlanSplit(metadata, graph, nameLeaf, [(nameKey, nameCost)], collation)
        : nameFits && !idFits ? tree.PlanSplit(metadata, graph, idLeaf, [(idKey, idCost)], collation)
        : throw new NotSupportedException("ReFS: two pages of the directory would have to split for the new file; that is not made in place.");
      if (!allocator.TryFindFreeRun(checked((int)clusterCount), p => p >= rowStart && p < rowStart + rowLength && !split.Clusters.Contains(p), out _))
        throw new NotSupportedException($"ReFS: no free run of {clusterCount} clusters next to the directory's files besides the new directory page.");
      split.Apply(image, metadata, graph);
      Add(image, path, data);
      return;
    }
    byte[]? headerValue = null;
    if (counter > 0) {
      headerValue = header!.Value.ToArray();
      BinaryPrimitives.WriteUInt64LittleEndian(headerValue.AsSpan(0x80), fileNumber);
    }

    allocator.SetAllocated(run, allocated: true);
    if (crcs is not null) RefsExtentLeaf.WritePadded(image, metadata.ClusterSize, run, data);
    else RefsInPlaceOverwrite.Apply(image, metadata.ClusterSize, run, data);
    // A file-number row sorts before every name row, so inserting it never moves the name row's position.
    RefsPageEditor.InsertRow(graph, nameLeaf, namePosition, nameKey, value, t.Row.Flags);
    RefsPageEditor.InsertRow(graph, idLeaf, idPosition, idKey, idValue, templateId?.Flags ?? 0);
    if (headerValue is not null) RefsPageEditor.ReplaceValue(graph, header!, headerValue);
    tree.AdjustRowCount(graph, +2);
    var objectPage = RefsPageEditor.ReplaceValue(graph, objectRow.Row, objectValue);
    // The object-table page was repacked, so the offsets of the page references it holds moved:
    // the checksums are refreshed through a graph built from the volume as it now is.
    new RefsMetadataGraph(image, RefsMetadataReader.Open(image)).RefreshChecksumPaths([nameLeaf, idLeaf, header?.PhysicalPageLcn ?? nameLeaf, tree.RootPage, objectPage]);
  }
}
