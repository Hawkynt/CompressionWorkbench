#pragma warning disable CS1591

using System.Buffers;
using Compression.Registry;

namespace Compression.Analysis.Visualization;

/// <summary>
/// Computes 16x16 heatmap tiles for any region of a seekable stream.
/// Designed for speed on multi-GB files: reads only what's needed,
/// uses pooled buffers, and samples large blocks instead of reading fully.
/// </summary>
public static class HeatmapComputer {

  /// <summary>Max bytes to read per tile for entropy computation. Larger tiles are sampled.</summary>
  private const int MaxSampleSize = 64 * 1024; // 64KB sample per tile

  /// <summary>Number of tiles per row/column.</summary>
  public const int GridSize = 16;

  /// <summary>Total tiles per level.</summary>
  public const int TotalTiles = GridSize * GridSize; // 256

  /// <summary>
  /// Computes a 16x16 grid of heatmap tiles for a region of the stream.
  /// Each tile covers (regionLength / 256) bytes.
  /// </summary>
  /// <param name="stream">Seekable stream (file, disk image, etc.).</param>
  /// <param name="regionOffset">Start offset of the region to visualize.</param>
  /// <param name="regionLength">Length of the region. Use stream.Length for the whole file.</param>
  /// <param name="ct">Cancellation token for responsiveness.</param>
  /// <returns>256 tiles in row-major order (row 0 col 0, row 0 col 1, ..., row 15 col 15).</returns>
  public static HeatmapTile[] ComputeGrid(
    Stream stream,
    long regionOffset,
    long regionLength,
    CancellationToken ct = default
  ) {
    var tiles = new HeatmapTile[TotalTiles];
    var tileSize = regionLength / TotalTiles;
    var remainder = regionLength % TotalTiles;

    // Pre-load magic signatures for fast detection.
    var magics = LoadMagicDatabase();

    // Pooled read buffer.
    var bufferSize = (int)Math.Min(tileSize, MaxSampleSize);
    if (bufferSize == 0) bufferSize = 1;
    var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

    // Frequency table reused across all tiles (avoid stackalloc in loop).
    var freq = new int[256];

    try {
      var currentOffset = regionOffset;

      for (var i = 0; i < TotalTiles; i++) {
        ct.ThrowIfCancellationRequested();

        var thisTileSize = tileSize + (i < remainder ? 1 : 0);

        if (thisTileSize == 0) {
          tiles[i] = new HeatmapTile {
            Offset = currentOffset, Length = 0,
            Row = i / GridSize, Col = i % GridSize
          };
          continue;
        }

        // Read sample from the start of the tile.
        var sampleSize = (int)Math.Min(thisTileSize, MaxSampleSize);
        stream.Position = currentOffset;
        var bytesRead = stream.Read(buffer, 0, sampleSize);

        if (bytesRead == 0) {
          tiles[i] = new HeatmapTile {
            Offset = currentOffset, Length = thisTileSize,
            Row = i / GridSize, Col = i % GridSize
          };
          currentOffset += thisTileSize;
          continue;
        }

        var sample = buffer.AsSpan(0, bytesRead);

        // Compute stats.
        Array.Clear(freq);
        var zeros = 0;
        var ascii = 0;
        var maxFreq = 0;
        var dominantByte = (byte)0;

        foreach (var b in sample) {
          freq[b]++;
          if (b == 0) zeros++;
          if (b is >= 0x20 and < 0x7F or 0x0A or 0x0D or 0x09) ascii++;
        }

        for (var j = 0; j < 256; j++) {
          if (freq[j] > maxFreq) {
            maxFreq = freq[j];
            dominantByte = (byte)j;
          }
        }

        // Shannon entropy.
        var entropy = 0.0;
        var len = (double)bytesRead;
        for (var j = 0; j < 256; j++) {
          if (freq[j] == 0) continue;
          var p = freq[j] / len;
          entropy -= p * Math.Log2(p);
        }

        // Check for known format signature in the first 16 bytes.
        string? detectedFormat = null;
        if (bytesRead >= 4) {
          foreach (var (name, sig) in magics) {
            if (sig.Length <= bytesRead && sample[..sig.Length].SequenceEqual(sig)) {
              detectedFormat = name;
              break;
            }
          }
        }

        tiles[i] = new HeatmapTile {
          Offset = currentOffset,
          Length = thisTileSize,
          Entropy = entropy,
          DominantByte = dominantByte,
          ZeroFraction = zeros / len,
          AsciiFraction = ascii / len,
          DetectedFormat = detectedFormat,
          Row = i / GridSize,
          Col = i % GridSize
        };

        currentOffset += thisTileSize;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }

    return tiles;
  }

  /// <summary>
  /// Reads raw bytes from a region for detail display (hex view, extraction).
  /// </summary>
  public static byte[] ReadRegion(Stream stream, long offset, int length) {
    var buffer = new byte[length];
    stream.Position = offset;
    var read = stream.Read(buffer, 0, length);
    return read == length ? buffer : buffer[..read];
  }

  /// <summary>
  /// Extracts a region of the stream to a file.
  /// </summary>
  public static void ExtractRegion(Stream stream, long offset, long length, string outputPath) {
    using var output = File.Create(outputPath);
    stream.Position = offset;

    var buffer = ArrayPool<byte>.Shared.Rent(81920);
    try {
      var remaining = length;
      while (remaining > 0) {
        var toRead = (int)Math.Min(remaining, buffer.Length);
        var read = stream.Read(buffer, 0, toRead);
        if (read == 0) break;
        output.Write(buffer, 0, read);
        remaining -= read;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  private static List<(string Name, byte[] Signature)> LoadMagicDatabase() {
    var result = new List<(string, byte[])>();

    try {
      foreach (var desc in FormatRegistry.All) {
        foreach (var magic in desc.MagicSignatures) {
          if (magic.Bytes is { Length: >= 2 })
            result.Add((desc.DisplayName, magic.Bytes));
        }
      }
    } catch {
      // Registry not initialized — use hardcoded essentials.
    }

    // Always include some essentials in case registry isn't loaded.
    result.Add(("MBR", [0x55, 0xAA])); // at offset 510, but useful hint
    result.Add(("ELF", [0x7F, 0x45, 0x4C, 0x46]));
    result.Add(("PE/MZ", [0x4D, 0x5A]));

    return result;
  }
}
