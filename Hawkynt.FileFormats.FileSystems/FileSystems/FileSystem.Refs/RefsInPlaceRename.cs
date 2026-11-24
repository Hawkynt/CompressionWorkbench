using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Renames a file within its directory by rewriting the directory's one leaf page in place: the
/// row keyed by the old name is removed and the same value is inserted under the new name at its
/// collation position, then every page-reference checksum from that page up to the checkpoint is
/// refreshed (<see cref="RefsMetadataGraph.RefreshChecksumPaths"/>). Directory keys are
/// [u16 0x30, u16 kind, UTF-16 name]; rows collate by key type, then by the name with a-z folded
/// to A-Z, compared by UTF-16 code unit — checked against the order the page already holds before
/// anything is written. Folding beyond ASCII would need the volume's upcase table, so a directory
/// holding a non-ASCII name, or a non-ASCII new name, is refused.
/// </summary>
/// <remarks>
/// Only files whose whole record lives in the directory row (kind 1) are renamed: a file or
/// directory with its own object (kind 2) also names itself elsewhere. Refused as well: a
/// directory whose rows span more than one page, a page without room for the new key, a name
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
    var rows = metadata.WalkTree(directoryRoot, virtualAddresses: true).ToList();
    if (rows.Count == 0) throw new InvalidDataException("ReFS: the directory has no rows.");
    var page = rows[0].PhysicalPageLcn;
    if (rows.Any(r => r.PhysicalPageLcn != page) || metadata.TranslateVirtualLcn(directoryRoot.Lcns[0]) != page)
      throw new NotSupportedException("ReFS: the directory spans more than one page; only single-page directories are renamed in.");

    int Compare(byte[] a, byte[] b) {
      var ta = BinaryPrimitives.ReadUInt16LittleEndian(a);
      var tb = BinaryPrimitives.ReadUInt16LittleEndian(b);
      if (ta != tb) return ta.CompareTo(tb);
      if (ta != 0x30) return a.AsSpan(2).SequenceCompareTo(b.AsSpan(2));
      return CompareFolded(a.AsSpan(4), b.AsSpan(4));
    }
    if (newName.Any(static c => c > 0x7F) || rows.Any(static r => BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x30 && !IsAscii(r.Key.AsSpan(4))))
      throw new NotSupportedException("ReFS: names beyond ASCII need the volume's upcase table to collate; the directory is not edited.");
    for (var i = 1; i < rows.Count; ++i)
      if (Compare(rows[i - 1].Key, rows[i].Key) >= 0)
        throw new NotSupportedException("ReFS: the directory's stored row order is not the collation this editor reproduces; it is not edited.");

    bool IsName(RefsBTreeRow row, string name)
      => row.Key.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(row.Key) == 0x30
         && CompareFolded(row.Key.AsSpan(4), Encoding.Unicode.GetBytes(name)) == 0
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
    var remaining = rows.Where(r => !ReferenceEquals(r, target)).ToList();
    var position = remaining.Count(r => Compare(r.Key, newKey) < 0);

    var graph = new RefsMetadataGraph(image, metadata);
    if (!RefsPageEditor.CanInsertRow(graph, page, Math.Min(position, rows.Count), newKey.Length, target.Value.Length))
      throw new NotSupportedException("ReFS: the directory page has no room for the longer name.");

    RefsPageEditor.DeleteRow(graph, target);
    RefsPageEditor.InsertRow(graph, page, position, newKey, target.Value, target.Flags);
    graph.RefreshChecksumPaths([page]);
  }

  private static bool IsAscii(ReadOnlySpan<byte> utf16) {
    for (var i = 0; i + 1 < utf16.Length; i += 2)
      if (BinaryPrimitives.ReadUInt16LittleEndian(utf16[i..]) > 0x7F) return false;
    return true;
  }

  private static int CompareFolded(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
    var count = Math.Min(left.Length, right.Length) / 2;
    for (var i = 0; i < count; ++i) {
      int a = BinaryPrimitives.ReadUInt16LittleEndian(left[(i * 2)..]);
      int b = BinaryPrimitives.ReadUInt16LittleEndian(right[(i * 2)..]);
      if (a is >= 'a' and <= 'z') a -= 32;
      if (b is >= 'a' and <= 'z') b -= 32;
      if (a != b) return a.CompareTo(b);
    }
    return left.Length.CompareTo(right.Length);
  }

  internal static RefsPageReference ResolveDirectoryRoot(RefsMetadataReader metadata, string path) {
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
    return objects.TryGetValue(oid, out var directory) ? directory : throw new InvalidDataException("ReFS: the directory object is missing.");
  }
}
