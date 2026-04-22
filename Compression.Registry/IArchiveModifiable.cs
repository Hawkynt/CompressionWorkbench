namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can mutate an existing archive in place. Implemented
/// by R/W filesystems and other formats whose container can be edited without a full
/// rebuild. Formats that can only be re-created from scratch (ZIP, 7z, TAR, …) do not
/// implement this — the shrink operation is their replacement for removal.
/// </summary>
public interface IArchiveModifiable {
  /// <summary>
  /// Appends or replaces files inside <paramref name="archive"/>. On replacement the
  /// previous bytes are wiped the same way <see cref="Remove"/> wipes them.
  /// </summary>
  void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs);

  /// <summary>
  /// Removes the named entries from <paramref name="archive"/> and wipes all on-disk
  /// traces: file data bytes, trailing cluster-tip slack, and the directory entries
  /// themselves. Free-space bookkeeping is updated consistently. No forensic recovery
  /// of the removed content should be possible from the resulting bytes.
  /// </summary>
  void Remove(Stream archive, string[] entryNames);
}
