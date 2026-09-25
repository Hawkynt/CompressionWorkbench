#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Runs an in-place defragmentation and keeps its result only if every file
/// still reads back byte for byte; otherwise the image is restored and rebuilt.
/// </summary>
/// <remarks>
/// <para>Byte-moving defragmenters relink allocation structures as they go, and
/// a filesystem that chains its files sector by sector — Atari DOS, Apple DOS —
/// has a link inside every sector to rewrite. Getting one of those wrong leaves
/// the file listed at the right length and full of the wrong bytes, which no
/// count-based check notices.</para>
///
/// <para>The guard is for volumes small enough to hold in memory twice, which is
/// what these formats are: a floppy is a few hundred kilobytes.</para>
/// </remarks>
public static class DefragContentGuard {

  /// <summary>
  /// A verified directory entry.  Unlike a payload-only snapshot this keeps
  /// the name and directory bit, so an in-place pass cannot silently swap two
  /// equal-sized files or move a file to a different directory.
  /// </summary>
  public sealed record DefragContentEntry(
      string Path,
      bool IsDirectory,
      byte[] Payload,
      long? Length = null,
      ulong NativeAttributes = 0,
      DateTimeOffset? Created = null,
      DateTimeOffset? Modified = null,
      DateTimeOffset? Accessed = null,
      DateTimeOffset? Changed = null,
      uint LinkCount = 1,
      string? LinkIdentity = null,
      string? SymbolicLinkTarget = null,
      IReadOnlyList<(long Offset, long Length)>? SparseExtents = null);

  /// <summary>
  /// Snapshots <paramref name="archive" />, runs <paramref name="inPlace" />,
  /// and verifies the contents. On any mismatch — or any exception — the
  /// snapshot is restored and <paramref name="rebuild" /> runs instead.
  /// </summary>
  /// <param name="archive">The image, read/write and seekable.</param>
  /// <param name="readContents">Reads every file's bytes from an image stream.</param>
  /// <param name="inPlace">The in-place pass to attempt.</param>
  /// <param name="rebuild">The fallback, which must not depend on the in-place attempt.</param>
  public static void RunOrRebuild(
      Stream archive,
      Func<Stream, IReadOnlyList<byte[]>> readContents,
      Action inPlace,
      Action rebuild) {
    RunOrRebuildCore(archive, readContents, SameContents, inPlace, rebuild);
  }

  /// <summary>
  /// The identity-preserving form of <see cref="RunOrRebuild(Stream, Func{Stream,
  /// IReadOnlyList{byte[]}}, Action, Action)"/>.  Callers should prefer this
  /// overload whenever their reader exposes paths or directory entries.
  /// </summary>
  public static void RunOrRebuild(
      Stream archive,
      Func<Stream, IReadOnlyList<DefragContentEntry>> readEntries,
      Action inPlace,
      Action rebuild) {
    RunOrRebuildCore(archive, readEntries, SameEntries, inPlace, rebuild);
  }

  /// <summary>
  /// Convenience overload for the common reader shape used by filesystem
  /// descriptors. The tuple name is the stable path; the payload remains the
  /// byte-level part of the identity.
  /// </summary>
  public static void RunOrRebuild(
      Stream archive,
      Func<Stream, IEnumerable<(string Path, byte[] Data)>> readEntries,
      Action inPlace,
      Action rebuild) {
    ArgumentNullException.ThrowIfNull(readEntries);
    RunOrRebuildCore(
      archive,
      stream => readEntries(stream)
        .Select(e => new DefragContentEntry(e.Path, false, e.Data))
        .ToList(),
      SameEntries,
      inPlace,
      rebuild);
  }

  private static void RunOrRebuildCore<T>(
      Stream archive,
      Func<Stream, IReadOnlyList<T>> readSnapshot,
      Func<IReadOnlyList<T>, IReadOnlyList<T>, bool> sameSnapshot,
      Action inPlace,
      Action rebuild) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(readSnapshot);
    ArgumentNullException.ThrowIfNull(inPlace);
    ArgumentNullException.ThrowIfNull(rebuild);

    archive.Position = 0;
    using var snapshot = new MemoryStream();
    archive.CopyTo(snapshot);

    IReadOnlyList<T> before;
    try {
      archive.Position = 0;
      before = readSnapshot(archive);
    } catch {
      // An image we cannot read before the pass gives nothing to compare
      // against, so the pass is not worth attempting.
      Restore(archive, snapshot);
      rebuild();
      return;
    }

    var kept = false;
    try {
      archive.Position = 0;
      inPlace();
      archive.Position = 0;
      kept = sameSnapshot(before, readSnapshot(archive));
    } catch {
      kept = false;
    }

    if (kept) return;

    Restore(archive, snapshot);
    rebuild();
  }

  private static void Restore(Stream archive, MemoryStream snapshot) {
    archive.Position = 0;
    snapshot.Position = 0;
    snapshot.CopyTo(archive);
    archive.SetLength(snapshot.Length);
    archive.Flush();
    archive.Position = 0;
  }

  /// <summary>
  /// Whether the same payloads are present, regardless of order or name. A
  /// defragmenter may rename nothing, but a rebuild can reorder the directory.
  /// </summary>
  private static bool SameContents(IReadOnlyList<byte[]> before, IReadOnlyList<byte[]> after) {
    if (before.Count != after.Count) return false;

    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var payload in before) {
      var key = Digest(payload);
      counts.TryGetValue(key, out var n);
      counts[key] = n + 1;
    }
    foreach (var payload in after) {
      var key = Digest(payload);
      if (!counts.TryGetValue(key, out var n) || n == 0) return false;
      counts[key] = n - 1;
    }
    return true;
  }

  private static bool SameEntries(IReadOnlyList<DefragContentEntry> before,
      IReadOnlyList<DefragContentEntry> after) {
    if (before.Count != after.Count) return false;

    static string Key(DefragContentEntry e) {
      var sparse = e.SparseExtents is null
        ? ""
        : string.Join(';', e.SparseExtents.Select(x => $"{x.Offset}:{x.Length}"));
      return string.Join('\u001f',
        e.Path,
        e.IsDirectory ? 'D' : 'F',
        e.Length ?? e.Payload.LongLength,
        e.NativeAttributes,
        e.Created?.UtcTicks,
        e.Modified?.UtcTicks,
        e.Accessed?.UtcTicks,
        e.Changed?.UtcTicks,
        e.LinkCount,
        e.LinkIdentity,
        e.SymbolicLinkTarget,
        sparse,
        Digest(e.Payload));
    }

    var expected = before.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
    var actual = after.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
    return expected.SequenceEqual(actual, StringComparer.Ordinal);
  }

  private static string Digest(byte[] data)
    => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
}
