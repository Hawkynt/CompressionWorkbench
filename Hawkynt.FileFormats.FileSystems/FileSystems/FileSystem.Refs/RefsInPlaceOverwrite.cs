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
/// Refused, the image unchanged: a length change (that needs metadata edits Windows has not
/// accepted from this editor), resident files (their bytes live in a metadata page), sparse
/// runs, integrity streams (each extent entry carries a CRC32 per cluster, see
/// <see cref="RefsNamespaceReader"/>), and clusters another stream shares (block clones,
/// snapshots), which Windows copies on write.
/// </remarks>
internal static class RefsInPlaceOverwrite {

  /// <summary>The clusters an overwrite of <paramref name="path"/> with <paramref name="length"/> bytes writes, in file order; throws when it is refused.</summary>
  public static IReadOnlyList<ulong> Plan(Stream image, RefsMetadataReader metadata, string path, long length) {
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
    if (file.Extents.Any(static e => (e.Flags >> 16) > 0x18))
      throw new NotSupportedException($"ReFS: '{normalized}' is an integrity stream; its cluster checksums would have to change too.");

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
