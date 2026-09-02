#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;

namespace FileFormat.Gif;

/// <summary>
/// Adapts PNGCrushCS' GIF chunk map to CompressionWorkbench's generic block-map view.
/// GIF byte walking lives in <c>Hawkynt.FileFormats.Images</c>; this layer only
/// translates semantic chunk roles to <see cref="DefragBlockInfo"/>.
/// </summary>
public static class GifLayoutMap {

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream file) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.CanRead)
      yield break;

    using var copy = new MemoryStream();
    if (file.CanSeek) file.Position = 0;
    file.CopyTo(copy);
    var chunks = Hawkynt.FileFormats.Images.FormatRegistry.EnumerateChunks(copy.ToArray()).ToList();

    var frameIndex = 0;
    for (var i = 0; i < chunks.Count; ++i) {
      var chunk = chunks[i];

      switch (chunk.Kind) {
        case ChunkKind.Signature: {
          // the screen descriptor and the global colour table are what the header
          // means to a defragmenter: one immovable run at the front of the file
          var length = chunk.Length;
          while (i + 1 < chunks.Count
                 && chunks[i + 1].Kind == ChunkKind.Palette
                 && chunks[i + 1].Offset == chunk.Offset + length) {
            length += chunks[i + 1].Length;
            ++i;
          }

          yield return new(chunk.Offset, length,
            DefragBlockKind.MetadataReserved, "GIF header + LSD + GCT", DefragBlockClass.Hot);
          break;
        }

        case ChunkKind.PixelData:
          yield return new(chunk.Offset, chunk.Length,
            DefragBlockKind.Used, $"Frame {frameIndex++}", DefragBlockClass.Normal);
          break;

        case ChunkKind.Footer:
          yield return new(chunk.Offset, chunk.Length,
            DefragBlockKind.MetadataReserved, "Trailer", DefragBlockClass.Normal);
          break;

        default:
          yield return new(chunk.Offset, chunk.Length,
            DefragBlockKind.MetadataReserved, "Extension", DefragBlockClass.Normal);
          break;
      }
    }
  }
}
