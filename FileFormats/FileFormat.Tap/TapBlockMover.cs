#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Tap;

/// <summary>
/// In-place TAP block mover. TAP is a purely sequential format with no
/// directory pointing into data, so "moving" an extent means physically
/// relocating block bytes and updating the stream. The rebuild fallback
/// is preferred for defragmentation.
/// </summary>
public sealed class TapBlockMover : IFilesystemBlockMover {

  /// <inheritdoc />
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;
    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      var src = srcOffset;
      var dst = dstOffset;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        image.Position = src;
        image.ReadExactly(buffer, 0, chunk);
        image.Position = dst;
        image.Write(buffer, 0, chunk);
        src += chunk;
        dst += chunk;
        remaining -= chunk;
      }
      if (zeroSource) {
        Array.Clear(buffer, 0, buffer.Length);
        remaining = length;
        src = srcOffset;
        while (remaining > 0) {
          var chunk = (int)Math.Min(remaining, buffer.Length);
          image.Position = src;
          image.Write(buffer, 0, chunk);
          src += chunk;
          remaining -= chunk;
        }
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  /// <inheritdoc />
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    // TAP has no directory with offset pointers — blocks are parsed
    // sequentially via length words. Allocation metadata is implicit in
    // the block chain structure, so there's nothing to patch. The rebuild
    // fallback handles TAP defragmentation correctly.
  }
}
