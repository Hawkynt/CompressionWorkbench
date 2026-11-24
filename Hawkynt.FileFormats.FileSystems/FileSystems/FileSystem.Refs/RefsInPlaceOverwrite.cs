namespace FileSystem.Refs;

/// <summary>
/// Replaces the contents of an existing file with contents of the same length by writing the
/// new bytes over the clusters the file already owns, which is what Windows itself does when a
/// file on a ReFS volume is overwritten: on Windows Server 2022 (ReFS 3.7) and 2025 (ReFS 3.14)
/// a same-length overwrite of a plain (non-integrity) stream changes those data clusters in
/// place, plus timestamps and a new checkpoint. No metadata is touched here — the file keeps its
/// old modification time — so the volume stays exactly as consistent as it was.
/// </summary>
/// <remarks>
/// Refused, the image unchanged: a length change (<see cref="RefsInPlaceResize"/> handles it),
/// resident files (their bytes live in a metadata page), sparse runs, integrity-stream entries
/// not held in the file's directory row (their checksums are rewritten by
/// <see cref="PlanChecksums"/>), and clusters another stream shares (block clones, snapshots),
/// which Windows copies on write.
/// </remarks>
internal static class RefsInPlaceOverwrite {

  /// <summary>The clusters an overwrite of <paramref name="path"/> with <paramref name="length"/> bytes writes, in file order; throws when it is refused.</summary>
  public static IReadOnlyList<ulong> Plan(Stream image, RefsMetadataReader metadata, string path, long length, bool allowIntegrity = false) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(metadata);
    if (!image.CanSeek || !image.CanWrite) throw new IOException("ReFS: the image must be seekable and writable.");
    if (length > Array.MaxLength) throw new NotSupportedException("ReFS: replacements over 2 GiB are not made in place.");
    var normalized = path.Replace('\\', '/').Trim('/');
    var matches = new RefsNamespaceReader(metadata).ReadAll()
      .Where(f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 0)
      throw new NotSupportedException($"ReFS: '{normalized}' does not exist; creating files is refused.");
    if (matches.Count > 1) throw new NotSupportedException($"ReFS: '{normalized}' is ambiguous under case-insensitive lookup.");
    var file = matches[0];
    if (file.IsDirectory) throw new NotSupportedException($"ReFS: '{normalized}' is a directory.");
    if (file.IsResident || file.Extents.Count == 0)
      throw new NotSupportedException($"ReFS: '{normalized}' is stored in its metadata page; only extent-backed files are overwritten in place.");
    if (length != file.Size)
      throw new NotSupportedException($"ReFS: '{normalized}' would change length ({file.Size} to {length} bytes); only same-length overwrites are made.");
    if (file.Extents.Any(static e => e.IsSparse))
      throw new NotSupportedException($"ReFS: '{normalized}' has sparse runs; it is not overwritten in place.");
    if (!allowIntegrity && file.Extents.Any(static e => (e.Flags >> 16) > 0x18))
      throw new NotSupportedException($"ReFS: '{normalized}' is an integrity stream; its cluster checksums would have to change too.");
    if (allowIntegrity && file.Extents.Any(static e => (e.Flags >> 16) is var size && size != 0x18 && size != 0x18 + 4 * e.ClusterCount))
      throw new NotSupportedException($"ReFS: '{normalized}' has extent entries of a shape this editor does not rewrite.");

    var clusterSize = metadata.ClusterSize;
    var needed = (length + clusterSize - 1) / clusterSize;
    var clusters = new List<ulong>((int)needed);
    foreach (var extent in file.Extents.OrderBy(static e => e.FileVcn)) {
      for (uint i = 0; i < extent.ClusterCount && clusters.Count < needed; ++i)
        clusters.Add(metadata.TranslateVirtualLcn(checked(extent.VirtualLcn + i)));
    }
    if (clusters.Count < needed) throw new InvalidDataException($"ReFS: the extents of '{normalized}' do not cover its length.");
    var refcount = new RefsBlockRefcount(metadata);
    foreach (var lcn in clusters) {
      if ((long)(lcn + 1) * clusterSize > image.Length)
        throw new InvalidDataException($"ReFS: '{normalized}' points outside the image.");
      if (refcount.IsSharedPhysicalRange(lcn, 1))
        throw new NotSupportedException($"ReFS: '{normalized}' shares clusters with another stream; it is not overwritten in place.");
    }
    return clusters;
  }

  /// <summary>
  /// For an integrity stream: the directory row whose extent entries carry the file's per-cluster
  /// checksums, and its value with those checksums recomputed for <paramref name="data"/> — CRC32C
  /// over each whole cluster as it will be on disk (the new bytes, then whatever the last cluster
  /// already holds past the end), as Windows-written integrity streams carry them (measured: each
  /// 4-byte value after a 24-byte entry is the CRC32C of that cluster). Null for a plain stream.
  /// Refused when the entries do not live in the file's directory row (a separate extent page).
  /// </summary>
  public static (RefsBTreeRow Row, byte[] Value)? PlanChecksums(
      Stream image, RefsMetadataReader metadata, string path, IReadOnlyList<ulong> clusters, byte[] data) {
    var normalized = path.Replace('\\', '/').Trim('/');
    var file = new RefsNamespaceReader(metadata).ReadAll()
      .Single(f => string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase));
    if (file.Extents.All(static e => (e.Flags >> 16) == 0x18)) return null;
    var parent = normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : "";
    var name = normalized[(normalized.LastIndexOf('/') + 1)..];
    var root = RefsInPlaceRename.ResolveDirectoryRoot(metadata, parent);
    var rows = metadata.WalkTree(root, virtualAddresses: true).ToList();
    var row = rows.SingleOrDefault(r => r.Key.Length == 4 + name.Length * 2
        && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x30
        && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(r.Key.AsSpan(2)) == 1
        && string.Equals(System.Text.Encoding.Unicode.GetString(r.Key, 4, r.Key.Length - 4), name, StringComparison.OrdinalIgnoreCase))
      ?? throw new NotSupportedException($"ReFS: the checksums of '{normalized}' are not held in its directory row.");
    var value = row.Value.ToArray();
    var clusterSize = metadata.ClusterSize;
    var buffer = new byte[clusterSize];
    var index = 0;
    foreach (var extent in file.Extents.OrderBy(static e => e.FileVcn)) {
      var at = extent.ValueRelativeOffset;
      if (at < 0 || at + 0x18 + 4 * (long)extent.ClusterCount > value.Length
          || System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(at)) != extent.VirtualLcn
          || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(at + 12)) != extent.FileVcn)
        throw new NotSupportedException($"ReFS: the extent entries of '{normalized}' do not live in its directory row; it is not overwritten.");
      for (var i = 0; i < extent.ClusterCount; ++i, ++index) {
        if (index >= clusters.Count) throw new NotSupportedException($"ReFS: '{normalized}' has more clusters than its length needs.");
        image.Position = (long)clusters[index] * clusterSize;
        image.ReadExactly(buffer);
        var offset = (long)index * clusterSize;
        var take = (int)Math.Min(clusterSize, data.LongLength - offset);
        data.AsSpan((int)offset, take).CopyTo(buffer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(at + 0x18 + 4 * i), RefsChecksum.Crc32C(buffer));
      }
    }
    return (row, value);
  }

  /// <summary>Writes <paramref name="data"/> over the planned clusters; bytes past its end in the last cluster stay as they were.</summary>
  public static void Apply(Stream image, int clusterSize, IReadOnlyList<ulong> clusters, byte[] data) {
    for (var i = 0; i < clusters.Count; ++i) {
      var offset = (long)i * clusterSize;
      var take = (int)Math.Min(clusterSize, data.LongLength - offset);
      image.Position = (long)clusters[i] * clusterSize;
      image.Write(data, (int)offset, take);
    }
    image.Flush();
  }
}
