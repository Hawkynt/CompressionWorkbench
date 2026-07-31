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
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(readContents);
    ArgumentNullException.ThrowIfNull(inPlace);
    ArgumentNullException.ThrowIfNull(rebuild);

    archive.Position = 0;
    using var snapshot = new MemoryStream();
    archive.CopyTo(snapshot);

    IReadOnlyList<byte[]> before;
    try {
      archive.Position = 0;
      before = readContents(archive);
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
      kept = SameContents(before, readContents(archive));
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

  private static string Digest(byte[] data)
    => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
}
