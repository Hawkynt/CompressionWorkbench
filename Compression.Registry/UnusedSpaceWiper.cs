#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Generic unused-space wiper that works with any format exposing an extent or
/// layout map. Enumerates all live regions, sorts them, and zero-fills every gap.
///
/// <para>This covers free clusters/sectors, inter-entry padding in archives,
/// dead bytes after file removal, and any other region not claimed by a live
/// extent. For cluster-tip wiping (trailing slack within a Used extent), callers
/// can supply a file-size lookup so the wiper knows the true file length vs.
/// the cluster-aligned extent length.</para>
/// </summary>
public static class UnusedSpaceWiper {

  /// <summary>
  /// Zero-fills every byte in <paramref name="image"/> that is not covered by a
  /// live (non-Free) extent in <paramref name="extents"/>. Optionally wipes
  /// cluster tips when <paramref name="fileSizeLookup"/> is provided.
  /// </summary>
  /// <param name="image">Readable, writable, seekable stream.</param>
  /// <param name="extents">All known extents (any order; gaps are treated as free).</param>
  /// <param name="imageSize">Total size of the image in bytes.</param>
  /// <param name="wipeClusterTips">Whether to zero the tail of Used extents
  /// where the actual file size is smaller than the extent length.</param>
  /// <param name="fileSizeLookup">Optional: maps a file name to its actual byte
  /// size (from the directory entry). When non-null and <paramref name="wipeClusterTips"/>
  /// is true, the trailing bytes of each Used extent beyond the file's real size
  /// are zeroed.</param>
  /// <returns>Total number of bytes written as zeros.</returns>
  public static long Wipe(
      Stream image,
      IEnumerable<DefragBlockInfo> extents,
      long imageSize,
      bool wipeClusterTips = true,
      Func<string, long>? fileSizeLookup = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(extents);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Stream must be readable, writable, and seekable.", nameof(image));

    // Collect and sort all non-Free extents by offset.
    var live = new List<DefragBlockInfo>();
    foreach (var ex in extents)
      if (ex.Kind != DefragBlockKind.Free)
        live.Add(ex);
    live.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

    var totalWiped = 0L;
    var cursor = 0L;
    var zeroBuf = new byte[64 * 1024]; // pre-zeroed by CLR

    foreach (var ex in live) {
      // Clip extent to image bounds.
      var extStart = ex.Offset;
      var extEnd = Math.Min(ex.Offset + ex.Length, imageSize);
      if (extStart >= imageSize) continue;
      if (extEnd <= extStart) continue;

      // Gap before this extent = unused space → zero-fill.
      if (extStart > cursor)
        totalWiped += ZeroRange(image, cursor, extStart - cursor, zeroBuf);

      // Cluster-tip wiping: if the file's actual size is less than the
      // extent length, the tail is slack and should be zeroed.
      if (wipeClusterTips && ex.Kind == DefragBlockKind.Used
          && ex.FileName != null && fileSizeLookup != null) {
        var actualSize = fileSizeLookup(ex.FileName);
        if (actualSize >= 0 && actualSize < ex.Length) {
          var tipStart = ex.Offset + actualSize;
          var tipLen = extEnd - tipStart;
          if (tipLen > 0)
            totalWiped += ZeroRange(image, tipStart, tipLen, zeroBuf);
        }
      }

      cursor = Math.Max(cursor, extEnd);
    }

    // Trailing gap after last live extent.
    if (cursor < imageSize)
      totalWiped += ZeroRange(image, cursor, imageSize - cursor, zeroBuf);

    image.Flush();
    return totalWiped;
  }

  /// <summary>
  /// Read-only companion to <see cref="Wipe"/>: returns the total number of
  /// bytes in <paramref name="imageSize"/> that are NOT covered by a live
  /// extent. Useful for telling the user how much of their image is unused
  /// *before* the I/O-skipping optimisation in <see cref="Wipe"/> hides the
  /// fact that most unused bytes were already zero.
  /// </summary>
  public static long ComputeUnusedBytes(
      IEnumerable<DefragBlockInfo> extents,
      long imageSize,
      bool includeClusterTips = false,
      Func<string, long>? fileSizeLookup = null) {
    ArgumentNullException.ThrowIfNull(extents);

    var live = new List<DefragBlockInfo>();
    foreach (var ex in extents)
      if (ex.Kind != DefragBlockKind.Free)
        live.Add(ex);
    live.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

    var unused = 0L;
    var cursor = 0L;
    foreach (var ex in live) {
      var extStart = ex.Offset;
      var extEnd = Math.Min(ex.Offset + ex.Length, imageSize);
      if (extStart >= imageSize) continue;
      if (extEnd <= extStart) continue;

      if (extStart > cursor) unused += extStart - cursor;

      if (includeClusterTips && ex.Kind == DefragBlockKind.Used
          && ex.FileName != null && fileSizeLookup != null) {
        var actualSize = fileSizeLookup(ex.FileName);
        if (actualSize >= 0 && actualSize < ex.Length) {
          var tipLen = extEnd - (ex.Offset + actualSize);
          if (tipLen > 0) unused += tipLen;
        }
      }

      cursor = Math.Max(cursor, extEnd);
    }

    if (cursor < imageSize) unused += imageSize - cursor;
    return unused;
  }

  /// <summary>
  /// Writes zeros to <paramref name="stream"/> at
  /// [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>).
  /// Only writes bytes that are not already zero, to minimize I/O on already-clean images.
  /// Returns the number of bytes actually written.
  /// </summary>
  private static long ZeroRange(Stream stream, long offset, long length, byte[] zeroBuf) {
    if (length <= 0) return 0;
    stream.Position = offset;
    var totalWritten = 0L;
    var remaining = length;

    // Read first to check if already zero; write only non-zero chunks.
    var readBuf = new byte[zeroBuf.Length];
    while (remaining > 0) {
      var chunk = (int)Math.Min(readBuf.Length, remaining);
      var bytesRead = stream.Read(readBuf, 0, chunk);
      if (bytesRead == 0) break;

      // Check if this chunk is already all-zero.
      var hasNonZero = false;
      for (var i = 0; i < bytesRead; i++) {
        if (readBuf[i] != 0) {
          hasNonZero = true;
          break;
        }
      }

      if (hasNonZero) {
        // Seek back and write zeros.
        stream.Position -= bytesRead;
        stream.Write(zeroBuf, 0, bytesRead);
        totalWritten += bytesRead;
      }

      remaining -= bytesRead;
    }

    return totalWritten;
  }
}
