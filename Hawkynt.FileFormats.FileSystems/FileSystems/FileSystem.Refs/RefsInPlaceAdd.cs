using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Adds a new file to an existing directory the way Windows Server 2022 (ReFS 3.7) does it,
/// measured by having Windows add a file: a name row (type 0x30, kind 1) whose value is the same
/// record an existing file of the directory holds, with its timestamps, sizes, file number
/// (value+0x80) and one extent for a newly allocated run; a file-number row (type 0x20, see
/// <see cref="RefsDirectoryRows"/>); and, on ReFS 3.7, the directory header's last file number
/// (type 0x10, value+0x80) advanced. The data goes into a free run of the allocator row that
/// holds the template file's clusters. Every page-reference checksum up to the checkpoint is
/// refreshed.
/// </summary>
/// <remarks>
/// Refused before the first write: a name that exists (case-insensitively), a directory whose order
/// <see cref="RefsDirectoryCollation"/> does not reproduce,
/// an empty file, a directory without a plain extent-backed file to take the record from,
/// multi-page directories, a page without room for the two rows, and no free run.
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
    var root = RefsInPlaceRename.ResolveDirectoryRoot(metadata, parent);
    var rows = metadata.WalkTree(root, virtualAddresses: true).ToList();
    var page = rows.Count > 0 ? rows[0].PhysicalPageLcn : 0;
    if (rows.Count == 0 || rows.Any(r => r.PhysicalPageLcn != page) || metadata.TranslateVirtualLcn(root.Lcns[0]) != page)
      throw new NotSupportedException("ReFS: the directory spans more than one page; only single-page directories are edited.");
    var collation = RefsDirectoryCollation.For(metadata, rows, name);
    var nameKey = new byte[4 + name.Length * 2];
    BinaryPrimitives.WriteUInt16LittleEndian(nameKey, 0x30);
    BinaryPrimitives.WriteUInt16LittleEndian(nameKey.AsSpan(2), 1);
    Encoding.Unicode.GetBytes(name).CopyTo(nameKey, 4);
    if (rows.Any(r => BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x30 && collation.Compare(r.Key, nameKey) == 0))
      throw new IOException($"ReFS: '{normalized}' already exists.");

    // The template: a plain extent-backed file held in a name row of this directory, whose
    // record this editor already rewrites (one leaf entry, size block found once).
    var files = new RefsNamespaceReader(metadata).ReadAll();
    (RefsBTreeRow Row, RefsFileRecord File, int Block, int Leaf)? template = null;
    foreach (var row in rows) {
      if (BinaryPrimitives.ReadUInt16LittleEndian(row.Key) != 0x30 || BinaryPrimitives.ReadUInt16LittleEndian(row.Key.AsSpan(2)) != 1) continue;
      var rowName = Encoding.Unicode.GetString(row.Key, 4, row.Key.Length - 4);
      var filePath = parent.Length == 0 ? rowName : parent + "/" + rowName;
      var file = files.FirstOrDefault(f => string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase));
      if (file is null || file.IsResident || file.Extents.Count == 0 || file.Extents.Any(static e => e.IsSparse || (e.Flags >> 16) != 0x18)) continue;
      var v = row.Value;
      var size = (ulong)file.Size;
      var alloc = BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(0x60));
      if (BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(0x58)) != size) continue;
      var block = RefsInPlaceResize.FindOnce(v, [alloc, size, size, alloc], 0x68);
      var leaf = RefsInPlaceResize.FindLeaf(v, (uint)file.Extents.Count);
      if (block < 0 || leaf < 0 || BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(block - 0x10)) != (uint)file.Extents.Count) continue;
      template = (row, file, block, leaf);
      break;
    }
    if (template is not { } t) throw new NotSupportedException("ReFS: the directory holds no plain file whose record a new file can take.");

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
    var fileNumber = Math.Max(counter, highest) + 1;

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
    RefsInPlaceResize.WriteLeaf(value, t.Leaf, [(vstart, t.File.Extents[0].Flags, 0u, (uint)clusterCount)], normalized);

    var idKey = RefsDirectoryRows.IdKey(fileNumber);
    var idValue = RefsDirectoryRows.IdValue(name);
    var templateId = rows.FirstOrDefault(static r => BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x20);
    var namePosition = rows.Count(r => collation.Compare(r.Key, nameKey) < 0);
    if (!RefsPageEditor.CanInsertRow(graph, page, namePosition, nameKey.Length + idKey.Length + 32, value.Length + idValue.Length))
      throw new NotSupportedException("ReFS: the directory page has no room for the new file's rows.");
    byte[]? headerValue = null;
    if (counter > 0) {
      headerValue = header!.Value.ToArray();
      BinaryPrimitives.WriteUInt64LittleEndian(headerValue.AsSpan(0x80), fileNumber);
    }

    allocator.SetAllocated(run, allocated: true);
    RefsInPlaceOverwrite.Apply(image, metadata.ClusterSize, run, data);
    RefsPageEditor.InsertRow(graph, page, namePosition, nameKey, value, t.Row.Flags);
    var current = metadata.WalkTree(root, virtualAddresses: true).ToList();
    var idPosition = current.Count(r => collation.Compare(r.Key, idKey) < 0);
    RefsPageEditor.InsertRow(graph, page, idPosition, idKey, idValue, templateId?.Flags ?? 0);
    if (headerValue is not null) RefsPageEditor.ReplaceValue(graph, header!, headerValue);
    graph.RefreshChecksumPaths([page]);
  }
}
