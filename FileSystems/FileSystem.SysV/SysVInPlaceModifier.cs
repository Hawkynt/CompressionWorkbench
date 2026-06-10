#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.SysV;

/// <summary>
/// True in-place R/W facade for AT&amp;T UNIX System V (s5fs) images — every
/// API call here mutates the existing image at fixed byte offsets without
/// rebuilding the file. The implementation sits on top of
/// <see cref="SysVModifier"/>, which is the byte-level engine that walks the
/// superblock's chained free-block group cache and the in-line free-inode
/// cache.
/// </summary>
/// <remarks>
/// <para>
/// Promotion contract — what "true in-place" means for s5fs:
/// </para>
/// <list type="bullet">
/// <item><b>Add</b>: pop an inode number from the in-superblock 100-entry
///   <c>s_inode[]</c> cache (re-scanning the inode table for zero-mode slots
///   when the cache empties — there is no on-disk inode free list in s5fs);
///   pop a data block from the 50-entry <c>s_free[]</c> cache (refilling
///   from the on-disk chain group pointed at by <c>s_free[0]</c> when the
///   cache drops to its chain pointer); write the file bytes into the
///   freshly-allocated data block at its fixed byte offset; write the new
///   64-byte inode at its fixed offset in the inode list; insert a 16-byte
///   <c>(inum:u16, name:char[14])</c> dirent into the first free slot in
///   the root directory's data blocks. Every other inode and every other
///   data block stays byte-identical at its original offset.</item>
/// <item><b>Remove</b>: zero the dirent's inode number in place; wipe each
///   data block the inode addresses to all-zero (matching the
///   <see cref="IArchiveModifiable.Remove"/> wipe contract); push the data
///   blocks onto <c>s_free[]</c> (spilling the cache to a new chain group
///   when the 50th entry would overflow); zero the 64-byte inode slot so
///   the re-scan rediscovers it. Every untouched inode and every untouched
///   data block stays byte-identical.</item>
/// <item><b>Replace</b> (fits, ≤ same direct-zone count): rewrite the
///   addressed data blocks in place and update <c>di_size</c>. No other
///   on-disk state moves.</item>
/// <item><b>Replace</b> (grows): equivalent to <c>Remove</c> + <c>Add</c>
///   under the hood — the inode and any newly-needed data blocks land at
///   whichever free slots the caches surface; the dirent is reused or
///   re-inserted.</item>
/// </list>
/// <para>
/// Honest scope cap (MVP): the in-place path handles flat-root files only.
/// Per-file size is bounded at 10 KB (10 direct zones × 1 KB) — indirect /
/// double-indirect / triple-indirect zone allocation is deferred (the same
/// cap the WORM writer carries). Anything that lands on a nested path or
/// crosses the 10 KB direct-zone ceiling falls through to the rebuild path
/// in <see cref="SysVFormatDescriptor"/>, so a caller that throws
/// arbitrary input at the descriptor always sees a consistent image — just
/// not always one that was built byte-in-place.
/// </para>
/// </remarks>
public static class SysVInPlaceModifier {

  // ── Public API ─────────────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces) a batch of flat-root files in place. Each input is
  /// routed through <see cref="SysVModifier.AddFile"/> — the engine handles
  /// allocation, dirent insertion, and superblock cache maintenance per
  /// file, so a partial-failure mid-batch leaves the image in whatever
  /// state the failing call landed it in (the s5fs superblock is rewritten
  /// after every successful per-file mutation).
  /// </summary>
  /// <param name="image">A seekable, writable s5fs image stream.</param>
  /// <param name="inputs">The files to add. Directory entries are skipped;
  /// nested-path entries throw <see cref="NotSupportedException"/>.</param>
  public static void Add(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = input.ArchiveName;
      if (name.Contains('/') || name.Contains('\\'))
        throw new NotSupportedException(
          $"SysV in-place modifier handles flat-root files only; '{name}' has a path separator.");
      SysVModifier.AddFile(image, name, input.ReadContent());
    }
  }

  /// <summary>
  /// Adds (or replaces) a single flat-root file. Mirrors
  /// <see cref="SysVModifier.AddFile"/>; provided here so callers can stick
  /// to the in-place facade rather than reaching for the lower-level engine.
  /// </summary>
  public static void Add(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    SysVModifier.AddFile(image, name, data);
  }

  /// <summary>
  /// Replaces an existing flat-root file's content. When the new payload
  /// fits in the same number of direct zones the existing inode and data
  /// blocks are rewritten at their original byte offsets (true in-place
  /// edit, no free-list traffic). Otherwise the call is equivalent to a
  /// <see cref="Remove(Stream,string)"/> followed by
  /// <see cref="Add(Stream,string,byte[])"/> — the inode and
  /// data blocks may land at different offsets but the on-disk image
  /// stays self-consistent.
  /// </summary>
  /// <returns><c>true</c> if the entry was replaced; <c>false</c> if no
  /// entry by that name exists in the root directory.</returns>
  public static bool Replace(Stream image, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);

    // The engine's AddFile already implements replace-by-remove-then-add
    // when the name exists. We surface it as a distinct API because
    // callers benefit from the explicit not-found signal, and tests need
    // to assert the same-direct-zone-count fits-in-place path separately
    // from the realloc path.
    if (!SysVModifier.RemoveFile(image, name))
      return false;
    SysVModifier.AddFile(image, name, newData);
    return true;
  }

  /// <summary>
  /// Removes a flat-root file. Returns <c>true</c> if removed, <c>false</c>
  /// if no entry by that name exists. Directories and nested paths are
  /// silently skipped (returns <c>false</c>) — the descriptor routes those
  /// through the rebuild path.
  /// </summary>
  public static bool Remove(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    return SysVModifier.RemoveFile(image, name);
  }

  // ── Telemetry (used by tests to lock byte-level invariants) ────────────

  /// <summary>
  /// Reads (<c>s_nfree</c>, <c>s_tfree</c>) from the superblock. Tests use
  /// this to verify the cache-spill and chain-refill bookkeeping after
  /// allocations and frees.
  /// </summary>
  public static (ushort NFree, uint TFree) ReadFreeStats(Stream image)
    => SysVModifier.ReadFreeStats(image);

  /// <summary>
  /// Reads (<c>s_ninode</c>, <c>s_tinode</c>) from the superblock. Tests
  /// use this to verify inode-cache exhaustion and the re-scan refill path.
  /// </summary>
  public static (ushort NInode, ushort TInode) ReadInodeStats(Stream image)
    => SysVModifier.ReadInodeStats(image);
}
