using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Removes a file held in its directory row: its clusters are released in the Medium Allocator
/// (the bits of its allocator rows cleared in place) and its rows are deleted from the leaf
/// pages holding them (<see cref="RefsDirectoryTree"/>); every page-reference checksum from the changed pages up to the
/// checkpoint is then refreshed (<see cref="RefsMetadataGraph.RefreshChecksumPaths"/>).
/// </summary>
/// <remarks>
/// Refused before the first write: directories, files with their own object (kind 2), sparse
/// runs, extent entries of an unknown shape (integrity-stream entries, with their checksums,
/// go with the row), clusters another stream shares, clusters the allocator does not show as
/// allocated, directories deeper than a root over leaves, and a removal that would empty a leaf
/// of a larger directory (merging pages is not made).
/// </remarks>
internal static class RefsInPlaceRemove {
  private const ulong RootDirectoryOid = 0x600;

  public static void Remove(Stream image, string path) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || !image.CanWrite) throw new IOException("ReFS: the image must be seekable and writable.");
    var normalized = path.Replace('\\', '/').Trim('/');
    var parentPath = normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : "";
    var name = normalized[(normalized.LastIndexOf('/') + 1)..];

    var metadata = RefsMetadataReader.Open(image);
    var files = new RefsNamespaceReader(metadata).ReadAll();
    var file = files.SingleOrDefault(f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase))
      ?? throw new FileNotFoundException($"ReFS: '{normalized}' was not found.", normalized);
    if (file.IsDirectory) throw new NotSupportedException($"ReFS: '{normalized}' is a directory; only files are removed.");
    if (file.Extents.Any(static e => e.IsSparse))
      throw new NotSupportedException($"ReFS: '{normalized}' has sparse runs; it is not removed in place.");
    // An integrity stream's per-cluster checksums sit in its extent entries, inside the row
    // that is deleted; only entries of a known shape are accepted.
    if (file.Extents.Any(static e => (e.Flags >> 16) is var size && size != 0x18 && size != 0x18 + 4 * e.ClusterCount))
      throw new NotSupportedException($"ReFS: '{normalized}' has extent entries of a shape this editor does not remove.");

    var directoryRoot = RefsInPlaceRename.ResolveDirectoryRoot(metadata, parentPath);
    var tree = RefsDirectoryTree.Open(metadata, directoryRoot);
    var rows = tree.Rows;
    var targets = rows.Where(r => r.Key.Length == 4 + name.Length * 2 && BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x30
        && string.Equals(Encoding.Unicode.GetString(r.Key, 4, r.Key.Length - 4), name, StringComparison.OrdinalIgnoreCase)).ToList();
    if (targets.Count != 1) throw new NotSupportedException($"ReFS: '{normalized}' is not one directory row.");
    var target = targets[0];
    if (BinaryPrimitives.ReadUInt16LittleEndian(target.Key.AsSpan(2)) != 1)
      throw new NotSupportedException($"ReFS: '{normalized}' has its own object; only files held in their directory row are removed.");
    // The file-number row (type 0x20) goes with the name row.
    var idRow = RefsDirectoryRows.FindIdRow(rows, RefsDirectoryRows.FileNumber(target.Value), Encoding.Unicode.GetString(target.Key, 4, target.Key.Length - 4))
      ?? throw new NotSupportedException($"ReFS: the file-number row of '{normalized}' was not found; it is not removed.");
    if (target.PhysicalPageLcn == idRow.PhysicalPageLcn) tree.RequireNotEmptied(target.PhysicalPageLcn, 2);
    else {
      tree.RequireNotEmptied(target.PhysicalPageLcn, 1);
      tree.RequireNotEmptied(idRow.PhysicalPageLcn, 1);
    }

    var clusters = new List<ulong>();
    foreach (var extent in file.Extents)
      for (uint i = 0; i < extent.ClusterCount; ++i)
        clusters.Add(metadata.TranslateVirtualLcn(checked(extent.VirtualLcn + i)));
    var refcount = new RefsBlockRefcount(metadata);
    if (clusters.Any(lcn => refcount.IsSharedPhysicalRange(lcn, 1)))
      throw new NotSupportedException($"ReFS: '{normalized}' shares clusters with another stream; it is not removed in place.");

    var graph = new RefsMetadataGraph(image, metadata);
    if (!RefsPageEditor.CanReplaceValue(graph, target, target.Value.Length))
      throw new NotSupportedException("ReFS: the directory page is not one this editor rewrites.");
    if (clusters.Count > 0) {
      // The Medium Allocator of a Windows-formatted volume, keyed by physical LCN.
      var allocator = RefsAllocatorWriter.WindowsMedium(metadata, graph, clusters)
        ?? throw new NotSupportedException($"ReFS: the Medium Allocator does not show every cluster of '{normalized}' allocated the way Windows-formatted volumes do; it is not removed.");
      // Throws before writing when a row cannot be rewritten in place.
      try {
        allocator.SetAllocated(clusters, allocated: false);
      } catch (InvalidOperationException e) {
        throw new NotSupportedException("ReFS: " + e.Message, e);
      }
    }
    // The allocator rewrite changed pages, so the directory row is read afresh.
    var current = RefsMetadataReader.Open(image);
    var currentTree = RefsDirectoryTree.Open(current, RefsInPlaceRename.ResolveDirectoryRoot(current, parentPath));
    var row = currentTree.Rows.Single(r => r.Key.AsSpan().SequenceEqual(target.Key));
    var currentIdRow = currentTree.Rows.Single(r => r.Key.AsSpan().SequenceEqual(idRow.Key));
    var after = new RefsMetadataGraph(image, current);
    RefsPageEditor.DeleteRow(after, row);
    RefsPageEditor.DeleteRow(after, currentIdRow);
    currentTree.AdjustRowCount(after, -2);
    after.RefreshChecksumPaths([row.PhysicalPageLcn, currentIdRow.PhysicalPageLcn, currentTree.RootPage]);
  }
}
