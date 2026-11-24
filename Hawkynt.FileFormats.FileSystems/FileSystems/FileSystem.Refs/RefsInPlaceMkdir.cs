using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Creates an empty directory the way Windows Server 2022 (ReFS 3.7) does, measured by having
/// Windows create one:
/// <list type="bullet">
/// <item>a new object (the next object number past the highest directory object) with its own
/// table: one MSB+ leaf page holding only the directory header row (type 0x10), the page placed
/// in a free run of the Medium Allocator row that holds an existing directory's page;</item>
/// <item>the header row: the record of an existing directory with new timestamps, the
/// directory attribute (0x10000000 at value+0x48), its last file number (value+0x80) at 1, and
/// the embedded name record carrying the parent's object number and the new name;</item>
/// <item>an object-table row (root 0): the existing directory's row with the page reference
/// (four virtual LCNs and its CRC64) pointing at the new page and the file-number counter
/// (+0xC8) at 1;</item>
/// <item>a name row (type 0x30, kind 2) in the parent: [u64 0, object number, four
/// timestamps, 16 zero bytes, u32 0x10000000, u32 0].</item>
/// </list>
/// Every page-reference checksum from the changed pages up to the checkpoint is then refreshed.
/// </summary>
/// <remarks>
/// Refused before the first write: ReFS 3.14 (its header record differs), a name that exists,
/// a parent spanning more than one page, a parent without a subdirectory whose page and records
/// match these layouts (it is the template), and no room or no free run.
/// </remarks>
internal static class RefsInPlaceMkdir {
  private const int HeaderNameRecord = 0x188;     // the embedded name record in a 3.7 header value

  public static void Create(Stream image, string path) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || !image.CanWrite) throw new IOException("ReFS: the image must be seekable and writable.");
    var normalized = path.Replace('\\', '/').Trim('/');
    var parent = normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : "";
    var name = normalized[(normalized.LastIndexOf('/') + 1)..];
    if (name.Length is 0 or > 255 || name.IndexOfAny(['\0', '/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name is "." or "..")
      throw new ArgumentException($"ReFS: '{name}' is not a valid directory name.", nameof(path));

    var metadata = RefsMetadataReader.Open(image);
    if (metadata.Header.MinorVersion >= 14)
      throw new NotSupportedException("ReFS: creating directories on ReFS 3.14 is not made in place; its directory records differ.");
    var (parentOid, parentRoot) = RefsInPlaceRename.ResolveDirectory(metadata, parent);
    var rows = metadata.WalkTree(parentRoot, virtualAddresses: true).ToList();
    var parentPage = rows.Count > 0 ? rows[0].PhysicalPageLcn : 0;
    if (rows.Count == 0 || rows.Any(r => r.PhysicalPageLcn != parentPage) || metadata.TranslateVirtualLcn(parentRoot.Lcns[0]) != parentPage)
      throw new NotSupportedException("ReFS: the parent directory spans more than one page.");
    var collation = RefsDirectoryCollation.For(metadata, rows, name);
    var nameKey = new byte[4 + name.Length * 2];
    BinaryPrimitives.WriteUInt16LittleEndian(nameKey, 0x30);
    BinaryPrimitives.WriteUInt16LittleEndian(nameKey.AsSpan(2), 2);
    Encoding.Unicode.GetBytes(name).CopyTo(nameKey, 4);
    if (rows.Any(r => BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x30 && collation.Compare(r.Key, nameKey) == 0))
      throw new IOException($"ReFS: '{normalized}' already exists.");

    // Template: an existing subdirectory anywhere, its name row, object row and table page.
    var template = FindTemplate(metadata, parentOid) ?? throw new NotSupportedException("ReFS: the parent holds no subdirectory whose records match the layout this editor writes.");
    var objectRows = metadata.WalkRoot(0).ToList();
    var objectPage = objectRows[0].PhysicalPageLcn;
    if (objectRows.Any(r => r.PhysicalPageLcn != objectPage))
      throw new NotSupportedException("ReFS: the object table spans more than one page.");
    var oid = objectRows.Select(static r => BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8))).Where(static o => o >= 0x700).DefaultIfEmpty(0x700UL).Max() + 1;

    // The page: clusters, allocation, bytes.
    var graph = new RefsMetadataGraph(image, metadata);
    var pageClusters = Math.Max(1, metadata.PageSize / metadata.ClusterSize);
    var templateClusters = template.PagePhysical;
    var allocator = RefsAllocatorWriter.WindowsMedium(metadata, graph, templateClusters)
      ?? throw new NotSupportedException("ReFS: the volume's Medium Allocator is not one this editor writes.");
    var (rowStart, rowLength) = allocator.RangeOfPhysical(templateClusters[0])
      ?? throw new NotSupportedException("ReFS: the allocator row holding directory pages was not found.");
    // Metadata pages start on a page boundary (every directory page Windows wrote does).
    List<ulong>? physical = null;
    for (var candidate = (rowStart + (ulong)pageClusters - 1) / (ulong)pageClusters * (ulong)pageClusters;
         candidate + (ulong)pageClusters <= rowStart + rowLength; candidate += (ulong)pageClusters) {
      var run = Enumerable.Range(0, pageClusters).Select(i => candidate + (ulong)i).ToList();
      if (run.All(allocator.CoversPhysical) && allocator.AreAllocated(run, expectedAllocated: false)) { physical = run; break; }
    }
    if (physical is null) throw new NotSupportedException("ReFS: no free run for the new directory page.");
    var virtualLcns = new List<ulong>();
    foreach (var p in physical) {
      if (!metadata.TryPhysicalToVirtualLcn(p, out var v)) throw new NotSupportedException("ReFS: the new page has no virtual address.");
      virtualLcns.Add(v);
    }

    var now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
    var header = template.HeaderValue.ToArray();
    for (var o = 0x28; o < 0x48; o += 8) BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(o), now);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x48), 0x10000000);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x80), 1);
    RewriteNameRecord(header, name);
    var page = BuildPage(template.Page, template.HeaderRowOffset, header, virtualLcns, oid, pageClusters);

    var objectValue = template.ObjectValue.ToArray();
    for (var i = 0; i < 4; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(objectValue.AsSpan(0x20 + i * 8), i < virtualLcns.Count ? virtualLcns[i] : 0);
    RefsChecksum.RefreshPageReference(objectValue.AsSpan(0x20, checked((int)metadata.PageReferenceSize)), page);
    BinaryPrimitives.WriteUInt64LittleEndian(objectValue.AsSpan(0xC8), 1);
    var objectKey = template.ObjectKey.ToArray();
    BinaryPrimitives.WriteUInt64LittleEndian(objectKey.AsSpan(8), oid);

    var nameValue = template.NameValue.ToArray();
    BinaryPrimitives.WriteUInt64LittleEndian(nameValue.AsSpan(0x08), oid);
    for (var o = 0x10; o < 0x30; o += 8) BinaryPrimitives.WriteUInt64LittleEndian(nameValue.AsSpan(o), now);

    var namePosition = rows.Count(r => collation.Compare(r.Key, nameKey) < 0);
    var objectPosition = objectRows.Count(r => BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8)) < oid);
    if (!RefsPageEditor.CanInsertRow(graph, parentPage, namePosition, nameKey.Length, nameValue.Length)
        || !RefsPageEditor.CanInsertRow(graph, objectPage, objectPosition, objectKey.Length, objectValue.Length))
      throw new NotSupportedException("ReFS: the parent directory or the object table has no room for the new rows.");

    allocator.SetAllocated(physical, allocated: true);
    for (var i = 0; i < pageClusters; ++i) {
      image.Position = (long)physical[i] * metadata.ClusterSize;
      image.Write(page, i * metadata.ClusterSize, Math.Min(metadata.ClusterSize, page.Length - i * metadata.ClusterSize));
    }
    image.Flush();
    RefsPageEditor.InsertRow(graph, objectPage, objectPosition, objectKey, objectValue, template.ObjectFlags);
    RefsPageEditor.InsertRow(graph, parentPage, namePosition, nameKey, nameValue, template.NameFlags);
    // The object-table page was repacked, so the offsets of the page references it holds moved:
    // the checksums are refreshed through a graph built from the volume as it now is.
    new RefsMetadataGraph(image, RefsMetadataReader.Open(image)).RefreshChecksumPaths([parentPage, objectPage]);
  }

  private sealed record Template(
    byte[] NameValue, ushort NameFlags,
    byte[] ObjectKey, byte[] ObjectValue, ushort ObjectFlags,
    byte[] Page, IReadOnlyList<ulong> PagePhysical, int HeaderRowOffset, byte[] HeaderValue);

  private static Template? FindTemplate(RefsMetadataReader metadata, ulong parentOid) {
    var objects = metadata.WalkRoot(0).ToDictionary(static r => BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8)));
    foreach (var (dirOid, dirObject) in objects) {
      if (dirOid != parentOid || dirObject.Value.Length < 0xD0) continue;
      var root = RefsPageReference.Parse(dirObject.Value.AsSpan(0x20));
      List<RefsBTreeRow> rows;
      try { rows = metadata.WalkTree(root, virtualAddresses: true).ToList(); } catch (InvalidDataException) { continue; }
      foreach (var row in rows) {
        if (row.Key.Length <= 4 || BinaryPrimitives.ReadUInt16LittleEndian(row.Key) != 0x30 || BinaryPrimitives.ReadUInt16LittleEndian(row.Key.AsSpan(2)) != 2) continue;
        if (row.Value.Length != 72 || BinaryPrimitives.ReadUInt32LittleEndian(row.Value.AsSpan(0x40)) != 0x10000000) continue;
        var childOid = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0x08));
        if (!objects.TryGetValue(childOid, out var childObject) || childObject.Value.Length < 0xD0) continue;
        var childRoot = RefsPageReference.Parse(childObject.Value.AsSpan(0x20));
        List<RefsBTreeRow> childRows;
        try { childRows = metadata.WalkTree(childRoot, virtualAddresses: true).ToList(); } catch (InvalidDataException) { continue; }
        var headerRow = childRows.FirstOrDefault(static r => r.Key.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(r.Key) == 0x10);
        if (headerRow is null || headerRow.Value.Length != 640 || !NameRecordMatches(headerRow.Value, Encoding.Unicode.GetString(row.Key, 4, row.Key.Length - 4))) continue;
        var physical = childRoot.Lcns.Select(metadata.TranslateVirtualLcn).ToList();
        var page = metadata.TryReadReferencedPage(childRoot, virtualAddresses: true);
        if (page is null) continue;
        var headerOffset = (int)(headerRow.AbsoluteRowOffset - (long)physical[0] * metadata.ClusterSize);
        if (headerOffset < 0x80 || headerOffset >= page.Length) continue;
        return new Template(row.Value, row.Flags, childObject.Key, childObject.Value, childObject.Flags, page, physical, headerOffset, headerRow.Value);
      }
    }
    return null;
  }

  /// <summary>
  /// The embedded name record of a 3.7 directory header at value+0x188: [record size u32 =
  /// align8(0x30 + n), 0x10 u16, 0x20 + n u16, u16 0, 0x18 u16, 0x18 + n u32, 0x18 + n u32,
  /// u32 0], then [0x80000002, 0x000D0039, the parent reference (16 bytes)] and the UTF-16 name
  /// of n bytes at +0x30 — measured on Windows-created directories. The parent reference is
  /// taken from a sibling unchanged.
  /// </summary>
  private static bool NameRecordMatches(byte[] header, string name) {
    var r = HeaderNameRecord;
    var n = Encoding.Unicode.GetByteCount(name);
    if (header.Length < r + 0x30 + n) return false;
    return BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(r)) == (uint)((0x30 + n + 7) & ~7)
           && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(r + 6)) == 0x20 + n
           && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(r + 0x0C)) == (uint)(0x18 + n)
           && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(r + 0x10)) == (uint)(0x18 + n)
           && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(r + 0x18)) == 0x80000002
           && header.AsSpan(r + 0x30, n).SequenceEqual(Encoding.Unicode.GetBytes(name))
           && InnerDataEnd(header) == r + (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(r));
  }

  private static int InnerDataEnd(byte[] header) => 0xA8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0xA8 + 4));

  private static void RewriteNameRecord(byte[] header, string name) {
    var r = HeaderNameRecord;
    var oldSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(r));
    var nameBytes = Encoding.Unicode.GetBytes(name);
    var n = nameBytes.Length;
    var newSize = (0x30 + n + 7) & ~7;
    var delta = newSize - oldSize;
    var free = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0xA8 + 8));
    if (delta > free) throw new NotSupportedException("ReFS: the directory name does not fit the header record.");
    header.AsSpan(r + 0x30, Math.Max(oldSize, newSize) - 0x30).Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(r), (uint)newSize);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(r + 6), (ushort)(0x20 + n));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(r + 0x0C), (uint)(0x18 + n));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(r + 0x10), (uint)(0x18 + n));
    nameBytes.CopyTo(header, r + 0x30);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xA8 + 4), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0xA8 + 4)) + (uint)delta);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xA8 + 8), (uint)(free - delta));
  }

  /// <summary>The template page with its self LCNs and table number replaced and its leaf holding only the header row.</summary>
  private static byte[] BuildPage(byte[] template, int headerRowOffset, byte[] headerValue, IReadOnlyList<ulong> virtualLcns, ulong oid, int pageClusters) {
    var page = template.ToArray();
    for (var i = 0; i < 4; ++i) BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x20 + i * 8), i < virtualLcns.Count ? virtualLcns[i] : 0);
    BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x48), oid);
    var node = 0x50 + (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x50));
    var dataStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(node));
    var indexEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(node + 0x20));
    var rowSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(headerRowOffset));
    var row = page.AsSpan(headerRowOffset, rowSize).ToArray();
    var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(10));
    var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(12));
    if (valueLength != headerValue.Length) throw new InvalidDataException("ReFS: the template header row changed length.");
    headerValue.CopyTo(row, valueOffset);
    // Index tag of the template's header entry, kept as found.
    var indexStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(node + 0x10));
    uint tag = 0xFFFF0000u;
    for (var p = node + indexStart; p < node + indexEnd; p += 4) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(p));
      if (node + (int)(entry & 0xFFFF) == headerRowOffset) tag = entry & 0xFFFF0000u;
    }
    page.AsSpan(node + dataStart, indexEnd - dataStart).Clear();
    row.CopyTo(page, node + dataStart);
    var dataEnd = dataStart + rowSize;
    var newIndexStart = indexEnd - 4;
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 4), (uint)dataEnd);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 8), (uint)(newIndexStart - dataEnd));
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 0x10), (uint)newIndexStart);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 0x14), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + newIndexStart), (uint)dataStart | tag);
    return page;
  }
}
