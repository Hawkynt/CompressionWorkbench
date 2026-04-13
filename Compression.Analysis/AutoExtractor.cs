using Compression.Registry;

namespace Compression.Analysis;

/// <summary>
/// Automatically detects and extracts archive contents, with recursive nested archive support.
/// </summary>
public sealed class AutoExtractor {
  private const int MaxDepth = 5;
  private const int MaxFileSize = 256 * 1024 * 1024; // 256 MB

  /// <summary>
  /// Result of an auto-extraction operation.
  /// </summary>
  public sealed class ExtractionResult {
    /// <summary>Detected format Id.</summary>
    public required string FormatId { get; init; }

    /// <summary>Display name of the detected format.</summary>
    public required string FormatName { get; init; }

    /// <summary>Extracted entries.</summary>
    public required List<ExtractedEntry> Entries { get; init; }

    /// <summary>Nested extraction results (for entries that are themselves archives).</summary>
    public required List<NestedResult> NestedResults { get; init; }
  }

  /// <summary>
  /// A single extracted entry.
  /// </summary>
  public sealed class ExtractedEntry {
    /// <summary>Entry name/path within the archive.</summary>
    public required string Name { get; init; }

    /// <summary>Extracted data.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Whether this is a directory entry.</summary>
    public bool IsDirectory { get; init; }
  }

  /// <summary>
  /// A nested archive extraction result.
  /// </summary>
  public sealed class NestedResult {
    /// <summary>Name of the entry that contained the nested archive.</summary>
    public required string EntryName { get; init; }

    /// <summary>Extraction result for the nested archive.</summary>
    public required ExtractionResult Result { get; init; }
  }

  /// <summary>
  /// Detects the format of the given stream and extracts all entries.
  /// If entries are themselves archives, recursively extracts them up to <see cref="MaxDepth"/> levels.
  /// </summary>
  public ExtractionResult? Extract(Stream stream, int depth = 0) {
    if (depth >= MaxDepth) return null;

    Compression.Lib.FormatRegistration.EnsureInitialized();

    // Read header for detection.
    var headerBuf = new byte[Math.Min(4096, stream.Length)];
    var origPos = stream.Position;
    var headerLen = stream.Read(headerBuf, 0, headerBuf.Length);
    stream.Position = origPos;

    // Detect format.
    var header = headerBuf.AsSpan(0, headerLen);
    IFormatDescriptor? bestDesc = null;
    var bestConf = 0.0;

    foreach (var desc in FormatRegistry.All) {
      foreach (var sig in desc.MagicSignatures) {
        if (sig.Offset + sig.Bytes.Length > header.Length) continue;
        var match = true;
        for (var j = 0; j < sig.Bytes.Length; j++) {
          var mask = sig.Mask != null && j < sig.Mask.Length ? sig.Mask[j] : (byte)0xFF;
          if ((header[sig.Offset + j] & mask) != (sig.Bytes[j] & mask)) { match = false; break; }
        }
        if (match && sig.Confidence > bestConf) {
          bestConf = sig.Confidence;
          bestDesc = desc;
        }
      }
    }

    if (bestDesc == null) return null;

    // Try archive extraction.
    var archiveOps = FormatRegistry.GetArchiveOps(bestDesc.Id);
    if (archiveOps != null) {
      return ExtractArchive(archiveOps, bestDesc, stream, depth);
    }

    // Try stream decompression.
    var streamOps = FormatRegistry.GetStreamOps(bestDesc.Id);
    if (streamOps != null) {
      return ExtractStream(streamOps, bestDesc, stream, depth);
    }

    return null;
  }

  private ExtractionResult ExtractArchive(IArchiveFormatOperations ops, IFormatDescriptor desc, Stream stream, int depth) {
    var entries = new List<ExtractedEntry>();
    var nested = new List<NestedResult>();

    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_autoextract_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      stream.Position = 0;
      ops.Extract(stream, tmpDir, null, null);

      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var relPath = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        var data = File.ReadAllBytes(file);
        entries.Add(new ExtractedEntry { Name = relPath, Data = data });

        // Try recursive extraction.
        if (data.Length > 0 && data.Length <= MaxFileSize) {
          using var nestedStream = new MemoryStream(data);
          var nestedResult = Extract(nestedStream, depth + 1);
          if (nestedResult != null)
            nested.Add(new NestedResult { EntryName = relPath, Result = nestedResult });
        }
      }
    } finally {
      try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
    }

    return new ExtractionResult {
      FormatId = desc.Id,
      FormatName = desc.DisplayName,
      Entries = entries,
      NestedResults = nested
    };
  }

  private ExtractionResult ExtractStream(IStreamFormatOperations ops, IFormatDescriptor desc, Stream stream, int depth) {
    stream.Position = 0;
    using var output = new MemoryStream();
    ops.Decompress(stream, output);
    var data = output.ToArray();

    var entries = new List<ExtractedEntry> {
      new() { Name = "decompressed", Data = data }
    };

    var nested = new List<NestedResult>();
    if (data.Length > 0 && data.Length <= MaxFileSize) {
      using var nestedStream = new MemoryStream(data);
      var nestedResult = Extract(nestedStream, depth + 1);
      if (nestedResult != null)
        nested.Add(new NestedResult { EntryName = "decompressed", Result = nestedResult });
    }

    return new ExtractionResult {
      FormatId = desc.Id,
      FormatName = desc.DisplayName,
      Entries = entries,
      NestedResults = nested
    };
  }
}
