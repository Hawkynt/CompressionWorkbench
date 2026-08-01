#pragma warning disable CS1591
using System.Buffers;

namespace Compression.Core.DiskImage;

/// <summary>
/// Copies a run of bytes from one place in an image to another, including when
/// the two overlap.
/// </summary>
/// <remarks>
/// A defragmenter shifts a run towards where it should be, and "towards" is
/// frequently a few blocks along from where it is — a file that starts at 200 K
/// and is 300 K long, moved to 400 K, overwrites its own tail. Copying that
/// front to back reads bytes that the same copy has already replaced, so the
/// file ends up holding a repeating fragment of itself at the right length.
/// Copying back to front when the destination is ahead of the source reads
/// every byte before anything overwrites it.
/// </remarks>
public static class ExtentCopy {

  private const int ChunkSize = 64 * 1024;

  /// <summary>
  /// Copies <paramref name="length" /> bytes from <paramref name="srcOffset" />
  /// to <paramref name="dstOffset" /> within <paramref name="image" />, then
  /// flushes. Overlapping ranges are handled.
  /// </summary>
  public static void Move(Stream image, long srcOffset, long dstOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    if (length <= 0 || srcOffset == dstOffset) return;

    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, ChunkSize));
    try {
      // Back to front when the destination overlaps the source from above;
      // front to back otherwise. Non-overlapping runs are correct either way.
      var backwards = dstOffset > srcOffset && dstOffset < srcOffset + length;
      var remaining = length;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        var offset = backwards ? remaining - chunk : length - remaining;

        image.Position = srcOffset + offset;
        image.ReadExactly(buffer, 0, chunk);
        image.Position = dstOffset + offset;
        image.Write(buffer, 0, chunk);

        remaining -= chunk;
      }

      // The bytes must reach the image before any structure starts pointing at
      // them: a reordered write would leave a crash window in which the
      // filesystem references a destination that has not been written yet.
      image.Flush();
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  /// <summary>Zeros <paramref name="length" /> bytes at <paramref name="offset" />.</summary>
  public static void Zero(Stream image, long offset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    if (length <= 0) return;

    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, ChunkSize));
    try {
      Array.Clear(buffer, 0, buffer.Length);
      var remaining = length;
      var at = offset;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        image.Position = at;
        image.Write(buffer, 0, chunk);
        at += chunk;
        remaining -= chunk;
      }
      image.Flush();
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }
}
