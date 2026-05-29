#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// PNG chunk reordering optimizer. Reorders ancillary chunks to the PNG spec's
/// recommended order while preserving the IHDR-first and IEND-last invariants.
/// Accepts an optional <see cref="MetadataPlacementProfile"/> to control whether
/// ancillary metadata chunks go before or after IDAT.
/// </summary>
public static class PngOptimizer {

  /// <summary>The canonical 8-byte PNG signature.</summary>
  private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

  /// <summary>
  /// Critical chunks that must appear between IHDR and IDAT (order matters).
  /// </summary>
  private static readonly HashSet<string> CriticalBeforeIdat = new(StringComparer.Ordinal) {
    "PLTE", "cHRM", "gAMA", "iCCP", "sRGB", "sBIT",
  };

  /// <summary>
  /// Ancillary chunks that default to before-IDAT placement.
  /// </summary>
  private static readonly HashSet<string> DefaultBeforeIdat = new(StringComparer.Ordinal) {
    "eXIf", "tEXt", "bKGD", "tRNS", "hIST", "sPLT", "pHYs",
  };

  /// <summary>
  /// Ancillary chunks that default to after-IDAT placement.
  /// </summary>
  private static readonly HashSet<string> DefaultAfterIdat = new(StringComparer.Ordinal) {
    "tIME", "iTXt", "zTXt",
  };

  /// <summary>
  /// Optimizes a PNG by reordering chunks to the spec's recommended order.
  /// IHDR is always first, IEND is always last, regardless of profile rules.
  /// </summary>
  public static void Optimize(Stream stream, MetadataPlacementProfile? profile = null) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
      throw new ArgumentException("Stream must be readable, writable, and seekable.", nameof(stream));

    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var data = ms.ToArray();

    if (data.Length < 8) return;
    for (var i = 0; i < 8; i++)
      if (data[i] != PngSignature[i])
        return;

    // Parse all chunks.
    var chunks = new List<ChunkInfo>();
    var pos = 8;
    while (pos + 12 <= data.Length) {
      var dataLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      var type = Encoding.ASCII.GetString(data, pos + 4, 4);
      var chunkTotalSize = 4 + 4 + dataLength + 4;
      if (pos + chunkTotalSize > data.Length) break;

      chunks.Add(new ChunkInfo(type, pos, chunkTotalSize));
      pos += chunkTotalSize;

      if (type == "IEND") break;
    }

    if (chunks.Count == 0) return;

    // Partition chunks into zones.
    ChunkInfo? ihdr = null;
    ChunkInfo? iend = null;
    var criticalBefore = new List<ChunkInfo>();  // PLTE, cHRM, gAMA, etc.
    var idatChunks = new List<ChunkInfo>();
    var beforeIdat = new List<ChunkInfo>();       // ancillary before data
    var afterIdat = new List<ChunkInfo>();         // ancillary after data
    var removed = new List<ChunkInfo>();

    foreach (var chunk in chunks) {
      switch (chunk.Type) {
        case "IHDR":
          ihdr = chunk;
          break;
        case "IEND":
          iend = chunk;
          break;
        case "IDAT":
          idatChunks.Add(chunk);
          break;
        default:
          if (CriticalBeforeIdat.Contains(chunk.Type)) {
            criticalBefore.Add(chunk);
          } else {
            var zone = profile?.GetZone(chunk.Type);
            if (zone == PlacementZone.Remove) {
              removed.Add(chunk);
            } else if (zone == PlacementZone.BeforeData) {
              beforeIdat.Add(chunk);
            } else if (zone == PlacementZone.AfterData) {
              afterIdat.Add(chunk);
            } else {
              // No profile rule: use format-specific default
              if (DefaultBeforeIdat.Contains(chunk.Type))
                beforeIdat.Add(chunk);
              else if (DefaultAfterIdat.Contains(chunk.Type))
                afterIdat.Add(chunk);
              else
                beforeIdat.Add(chunk); // unknown ancillary → before IDAT by default
            }
          }
          break;
      }
    }

    // IHDR and IEND must exist for a valid PNG.
    if (ihdr == null || iend == null) return;

    // Build the reordered chunk list.
    var ordered = new List<ChunkInfo>();
    ordered.Add(ihdr.Value);
    ordered.AddRange(criticalBefore);
    ordered.AddRange(beforeIdat);
    ordered.AddRange(idatChunks);
    ordered.AddRange(afterIdat);
    ordered.Add(iend.Value);

    // Check if order is already optimal (same sequence).
    if (ordered.Count == chunks.Count && removed.Count == 0) {
      var alreadyOptimal = true;
      for (var i = 0; i < ordered.Count; i++) {
        if (ordered[i].Start != chunks[i].Start) {
          alreadyOptimal = false;
          break;
        }
      }
      if (alreadyOptimal) return;
    }

    // Write the reordered file.
    stream.Position = 0;
    stream.Write(PngSignature, 0, 8);
    foreach (var chunk in ordered)
      stream.Write(data, chunk.Start, chunk.TotalSize);
    stream.SetLength(stream.Position);
    stream.Flush();
  }

  private readonly record struct ChunkInfo(string Type, int Start, int TotalSize);
}
