namespace FileFormat.Ha;

/// <summary>
/// Edits an HA archive by rewriting it: every member that stays is copied byte for byte, deleted
/// records are compacted away (as <c>ha</c> itself does on its next write), new members are appended.
/// </summary>
public static class HaModifier {
  /// <summary>Adds files, replacing members of the same name.</summary>
  /// <param name="archive">The archive, readable, writable and seekable.</param>
  /// <param name="files">The files to add, names '/'-separated.</param>
  public static void Add(Stream archive, IReadOnlyList<(string Name, byte[] Data, DateTime? ModifiedUtc)> files) {
    ArgumentNullException.ThrowIfNull(files);
    var names = files.Select(f => Normalise(f.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Rewrite(archive, e => !names.Contains(e.FileName), files.Select(f => HaWriter.BuildFileRecord(f.Name, f.Data, f.ModifiedUtc)).ToList());
  }

  /// <summary>Removes members by name (a directory record by its own name).</summary>
  /// <param name="archive">The archive, readable, writable and seekable.</param>
  /// <param name="names">The member names, '/'-separated.</param>
  /// <returns>How many records were removed.</returns>
  public static int Remove(Stream archive, IEnumerable<string> names) {
    ArgumentNullException.ThrowIfNull(names);
    var set = names.Select(Normalise).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var removed = 0;
    Rewrite(archive, e => {
      if (!set.Contains(e.FileName))
        return true;
      ++removed;
      return false;
    }, []);
    return removed;
  }

  private static string Normalise(string name) => string.Join('/', name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

  private static void Rewrite(Stream archive, Func<HaEntry, bool> keep, IReadOnlyList<byte[]> appended) {
    ArgumentNullException.ThrowIfNull(archive);
    List<(long Offset, long Length)> kept;
    using (var reader = new HaReader(archive, leaveOpen: true)) {
      if (reader.IsTruncated)
        throw new NotSupportedException("The HA archive has a record that does not parse; refusing to edit it.");
      kept = [.. reader.Entries.Where(keep).Select(e => (e.RecordOffset, e.DataOffset + e.CompressedSize - e.RecordOffset))];
    }

    var count = kept.Count + appended.Count;
    if (count > ushort.MaxValue)
      throw new NotSupportedException("An HA archive holds at most 65535 members.");

    using var image = new MemoryStream();
    image.Write([(byte)'H', (byte)'A', (byte)count, (byte)(count >> 8)]);
    foreach (var (offset, length) in kept) {
      archive.Position = offset;
      var record = new byte[length];
      archive.ReadExactly(record);
      image.Write(record);
    }

    foreach (var record in appended)
      image.Write(record);

    archive.Position = 0;
    image.Position = 0;
    image.CopyTo(archive);
    archive.SetLength(image.Length);
    archive.Flush();
  }
}
