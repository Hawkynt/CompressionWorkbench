#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Core;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// CompressionWorkbench metadata-placement policy over PNGCrushCS' PNG chunk rewriter.
/// Chunk parsing, ordering validation, CRC handling and byte emission live in
/// <c>Hawkynt.FileFormats.Images</c>; this class only translates Workbench profiles.
/// </summary>
public static class PngOptimizer {

  private static readonly HashSet<string> DefaultAfterData = new(StringComparer.Ordinal) {
    "tIME", "iTXt", "zTXt",
  };

  public static void Optimize(Stream stream, MetadataPlacementProfile? profile = null) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
      throw new ArgumentException("Stream must be readable, writable, and seekable.", nameof(stream));

    stream.Position = 0;
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    var data = copy.ToArray();

    var chunks = Hawkynt.FileFormats.Images.FormatRegistry.EnumerateChunks(data);
    if (chunks.Count == 0)
      return;

    var rules = new Dictionary<string, ChunkRewriteRule>(StringComparer.Ordinal);
    foreach (var chunk in chunks) {
      if (chunk.Name is "SIGNATURE" or "IHDR" or "IDAT" or "IEND")
        continue;

      var requested = profile?.GetZone(chunk.Name);
      var placement = requested switch {
        PlacementZone.BeforeData => ChunkPlacement.BeforeData,
        PlacementZone.AfterData => ChunkPlacement.AfterData,
        PlacementZone.Remove => ChunkPlacement.Remove,
        null when DefaultAfterData.Contains(chunk.Name) => ChunkPlacement.AfterData,
        _ => ChunkPlacement.BeforeData,
      };
      rules[chunk.Name] = new ChunkRewriteRule(chunk.Name, placement);
    }

    var rewritten = Hawkynt.FileFormats.Images.FormatRegistry.RewriteChunks(data, rules.Values.ToArray());
    if (rewritten.AsSpan().SequenceEqual(data))
      return;

    stream.Position = 0;
    stream.Write(rewritten);
    stream.SetLength(rewritten.Length);
    stream.Flush();
  }
}
