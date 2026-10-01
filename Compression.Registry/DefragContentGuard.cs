#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Runs an in-place maintenance pass and keeps its result only if every file still
/// reads back intact: <see cref="RunOrRebuild"/> falls back to a rebuild,
/// <see cref="RunVerifiedInPlace"/> refuses and rolls back.
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

  /// <summary>
  /// Runs an in-place maintenance pass that may only rearrange the image, never change
  /// what it holds: captures the <see cref="ArchiveSemanticManifest"/>, lets
  /// <paramref name="edit"/> write through journalled patches,
  /// and keeps the result only when the image length and the manifest — every path,
  /// byte, timestamp, link and container property the reader reports — are unchanged.
  /// </summary>
  /// <remarks>
  /// <para>Unlike <see cref="RunOrRebuild"/> there is no rebuild behind it: a rebuild
  /// keeps names and bytes and drops the rest, which is the loss this guard exists to
  /// refuse. Any failure — the edit's own refusal, an exception halfway through, a
  /// verification mismatch — rolls every patch back and surfaces as
  /// <see cref="NotSupportedException"/>, with the image byte for byte as it was.</para>
  /// <para>Only the journalled ranges are held in memory, so the guard scales to volumes
  /// far larger than RAM; the manifest pass reads every file once before and once after.</para>
  /// </remarks>
  /// <param name="image">The image, readable, writable and seekable.</param>
  /// <param name="ops">The reader the manifest is taken through.</param>
  /// <param name="edit">The pass; every write it makes must go through the journal it is handed.</param>
  /// <param name="operation">What to call the pass in a refusal message.</param>
  public static void RunVerifiedInPlace(
      Stream image,
      IArchiveFormatOperations ops,
      Action<Stream, InPlacePatchJournal> edit,
      string operation) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(ops);
    ArgumentNullException.ThrowIfNull(edit);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException($"{operation} needs a readable, writable, seekable stream.", nameof(image));

    var length = image.Length;
    ArchiveSemanticManifest before;
    try {
      before = ArchiveSemanticManifest.Capture(image, ops);
    } catch (Exception ex) when (ex is not NotSupportedException) {
      throw new NotSupportedException($"{operation}: the image cannot be read back to verify the pass ({ex.Message}); nothing was changed.", ex);
    }

    var journal = new InPlacePatchJournal();
    try {
      edit(image, journal);
      image.Flush();
      if (image.Length != length)
        throw new NotSupportedException($"{operation} changed the image size ({length} -> {image.Length}).");
      var after = ArchiveSemanticManifest.Capture(image, ops);
      before.RequireSameAs(after, operation);
    } catch (Exception ex) {
      journal.Rollback(image);
      if (image.Length != length) image.SetLength(length);
      image.Position = 0;
      if (ex is NotSupportedException) throw;
      throw new NotSupportedException($"{operation} refused: {ex.Message} The image was left unchanged.", ex);
    }
    image.Position = 0;
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
