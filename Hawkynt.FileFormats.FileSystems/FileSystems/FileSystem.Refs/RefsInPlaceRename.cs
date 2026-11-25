using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Renames a file within its directory by rewriting the directory's leaf pages in place
/// (<see cref="RefsDirectoryTree"/>): the row keyed by the old name is removed and the same value
/// is inserted under the new name at its collation position in the leaf that key belongs to,
/// then every page-reference checksum from those pages up to the checkpoint is
/// refreshed (<see cref="RefsMetadataGraph.RefreshChecksumPaths"/>). Directory keys are
/// [u16 0x30, u16 kind, UTF-16 name]; rows collate as <see cref="RefsDirectoryCollation"/> says —
/// checked against the order the page already holds before anything is written.
/// </summary>
/// <remarks>
/// Only files whose whole record lives in the directory row (kind 1) are renamed: a file or
/// directory with its own object (kind 2) also names itself elsewhere. Refused as well: a
/// directory deeper than a root over leaves, a leaf without room for the new key or one the
/// move would empty, a name
/// already present under case-insensitive lookup, and a page whose stored order the collation
/// here does not reproduce. Every check runs before the first write.
/// </remarks>
internal static class RefsInPlaceRename {
  private const ulong RootDirectoryOid = 0x600;

  public static void Rename(Stream image, string sourcePath, string destinationPath) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || !image.CanWrite) throw new IOException("ReFS: the image must be seekable and writable.");
    var source = sourcePath.Replace('\\', '/').Trim('/');
    var destination = destinationPath.Replace('\\', '/').Trim('/');
    var sourceParent = source.Contains('/') ? source[..source.LastIndexOf('/')] : "";
    var destinationParent = destination.Contains('/') ? destination[..destination.LastIndexOf('/')] : "";
    if (!string.Equals(sourceParent, destinationParent, StringComparison.OrdinalIgnoreCase))
      throw new NotSupportedException("ReFS: a rename stays within its directory; moving between directories is refused.");
    var newName = destination[(destination.LastIndexOf('/') + 1)..];
    if (newName.Length is 0 or > 255 || newName.IndexOfAny(['\0', '/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || newName is "." or "..")
      throw new ArgumentException($"ReFS: '{newName}' is not a valid file name.", nameof(destinationPath));
    var oldName = source[(source.LastIndexOf('/') + 1)..];
    if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;

    var metadata = RefsMetadataReader.Open(image);
    var directoryRoot = ResolveDirectoryRoot(metadata, sourceParent);
    var tree = RefsDirectoryTree.Open(metadata, directoryRoot);
    var rows = tree.Rows;

    var collation = RefsDirectoryCollation.For(metadata, rows, newName);

    bool IsName(RefsBTreeRow row, string name)
      => row.Key.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(row.Key) == 0x30
         && collation.CompareNames(row.Key.AsSpan(4), Encoding.Unicode.GetBytes(name)) == 0
         && row.Key.Length - 4 == name.Length * 2;
    var targets = rows.Where(r => IsName(r, oldName)).ToList();
    if (targets.Count == 0) throw new FileNotFoundException($"ReFS: '{source}' was not found.", source);
    if (targets.Count > 1) throw new NotSupportedException($"ReFS: '{source}' is ambiguous.");
    var target = targets[0];
    if (BinaryPrimitives.ReadUInt16LittleEndian(target.Key.AsSpan(2)) != 1)
      throw new NotSupportedException($"ReFS: '{source}' has its own object (a directory or a large file record); only files held in their directory row are renamed.");
    if (rows.Any(r => !ReferenceEquals(r, target) && IsName(r, newName)))
      throw new IOException($"ReFS: '{newName}' already exists.");

    var newKey = new byte[4 + newName.Length * 2];
    target.Key.AsSpan(0, 4).CopyTo(newKey);
    Encoding.Unicode.GetBytes(newName).CopyTo(newKey, 4);
    // The renamed row goes to the leaf its new key belongs to, which may be another one.
    var leaf = tree.LeafFor(newKey, collation);
    var position = tree.PositionIn(leaf, newKey, collation, target);
    if (leaf != target.PhysicalPageLcn) tree.RequireNotEmptied(target.PhysicalPageLcn, 1);

    // The file-number row (type 0x20) names the file too; it gets the new name.
    var storedName = Encoding.Unicode.GetString(target.Key, 4, target.Key.Length - 4);
    var idRow = RefsDirectoryRows.FindIdRow(rows, RefsDirectoryRows.FileNumber(target.Value), storedName)
      ?? throw new NotSupportedException($"ReFS: the file-number row of '{source}' was not found; it is not renamed.");
    var idValue = RefsDirectoryRows.IdValue(newName);

    var graph = new RefsMetadataGraph(image, metadata);
    if (!RefsPageEditor.CanInsertRow(graph, leaf, position, newKey.Length + idValue.Length, target.Value.Length))
      throw new NotSupportedException("ReFS: the directory page has no room for the longer name; splitting pages is not made in place.");

    RefsPageEditor.DeleteRow(graph, target);
    RefsPageEditor.InsertRow(graph, leaf, position, newKey, target.Value, target.Flags);
    RefsPageEditor.ReplaceValue(graph, idRow, idValue);
    graph.RefreshChecksumPaths([target.PhysicalPageLcn, leaf, idRow.PhysicalPageLcn]);
  }

  internal static RefsPageReference ResolveDirectoryRoot(RefsMetadataReader metadata, string path)
    => ResolveDirectory(metadata, path).Root;

  /// <summary>The object number and table root of the directory at <paramref name="path"/>.</summary>
  internal static (ulong Oid, RefsPageReference Root) ResolveDirectory(RefsMetadataReader metadata, string path) {
    var objects = new Dictionary<ulong, RefsPageReference>();
    foreach (var row in metadata.WalkRoot(0)) {
      if (row.Key.Length < 16 || row.Value.Length < 0x40) continue;
      var reference = RefsPageReference.Parse(row.Value.AsSpan(0x20));
      if (reference.Lcns.Count > 0) objects[BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(8, 8))] = reference;
    }
    var oid = RootDirectoryOid;
    foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      if (!objects.TryGetValue(oid, out var root)) throw new InvalidDataException("ReFS: a directory object is missing.");
      var row = metadata.WalkTree(root, virtualAddresses: true).FirstOrDefault(r =>
        r.Key.Length > 4 && BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x30 && BinaryPrimitives.ReadUInt16LittleEndian(r.Key.AsSpan(2)) == 2
        && string.Equals(Encoding.Unicode.GetString(r.Key, 4, r.Key.Length - 4), part, StringComparison.OrdinalIgnoreCase));
      if (row is null || row.Value.Length < 0x10) throw new FileNotFoundException($"ReFS: directory '{part}' was not found.");
      oid = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0x08));
    }
    return objects.TryGetValue(oid, out var directory) ? (oid, directory) : throw new InvalidDataException("ReFS: the directory object is missing.");
  }
}
