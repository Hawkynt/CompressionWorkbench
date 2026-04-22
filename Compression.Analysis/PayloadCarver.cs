using Compression.Analysis.Scanning;

namespace Compression.Analysis;

/// <summary>
/// Finds and optionally extracts embedded payloads from arbitrary binary data
/// (executables, firmware dumps, corrupt disk images, …). Combines
/// <see cref="SignatureScanner"/> for offset detection with
/// <see cref="PayloadLengthProbe"/> for format-specific end-of-payload inference.
/// <para>
/// This is the binwalk-style extraction path: users who ask "is there a WAV
/// hiding inside this .exe?" get an answer plus the carved WAV bytes.
/// </para>
/// </summary>
public static class PayloadCarver {

  /// <summary>One carved payload discovered inside a larger buffer.</summary>
  public sealed record CarvedPayload(
    long Offset,
    long Length,
    string FormatId,
    string FormatName,
    double Confidence,
    byte[]? Data);

  /// <summary>Options controlling carver behaviour.</summary>
  public sealed record CarveOptions(
    double MinConfidence = 0.5,
    IReadOnlyList<string>? FormatFilter = null,
    bool IncludeData = true,
    int MaxResults = 100);

  /// <summary>
  /// Runs <see cref="SignatureScanner"/>, applies length inference to each hit,
  /// deduplicates overlapping detections, and returns the list of carved payloads.
  /// When <see cref="CarveOptions.IncludeData"/> is true (default), each result's
  /// <see cref="CarvedPayload.Data"/> contains the exact bytes of the payload.
  /// </summary>
  public static IReadOnlyList<CarvedPayload> Carve(byte[] buffer, CarveOptions? options = null) {
    options ??= new CarveOptions();

    var scanResults = SignatureScanner.Scan(buffer, options.MaxResults * 3);
    var filter = options.FormatFilter is { Count: > 0 } f
      ? new HashSet<string>(f, StringComparer.OrdinalIgnoreCase)
      : null;

    var raw = new List<CarvedPayload>();
    foreach (var hit in scanResults) {
      if (hit.Confidence < options.MinConfidence) continue;
      if (filter != null && !filter.Contains(hit.FormatName)) continue;

      var probedLen = PayloadLengthProbe.TryProbe(buffer, hit.Offset, hit.FormatName);
      var length = probedLen ?? EstimateLengthFallback(hit.Offset, scanResults, buffer.Length);
      if (length <= 0) continue;
      if (hit.Offset + length > buffer.Length) length = buffer.Length - hit.Offset;

      byte[]? data = null;
      if (options.IncludeData) {
        data = new byte[length];
        Array.Copy(buffer, hit.Offset, data, 0, length);
      }
      raw.Add(new CarvedPayload(hit.Offset, length, hit.FormatName, hit.FormatName,
        hit.Confidence, data));
    }

    return DeduplicateOverlapping(raw, options.MaxResults);
  }

  /// <summary>
  /// Writes each carved payload to <paramref name="outputDir"/>, named
  /// <c>offset_XXXXXX_&lt;format&gt;.&lt;ext&gt;</c>. Returns the file paths written.
  /// </summary>
  public static IReadOnlyList<string> Extract(
      IReadOnlyList<CarvedPayload> payloads, string outputDir) {
    Directory.CreateDirectory(outputDir);
    var files = new List<string>(payloads.Count);
    foreach (var p in payloads) {
      var ext = DefaultExtension(p.FormatId);
      var name = $"offset_{p.Offset:X8}_{p.FormatId}{ext}";
      var full = Path.Combine(outputDir, name);
      File.WriteAllBytes(full, p.Data ?? []);
      files.Add(full);
    }
    return files;
  }

  /// <summary>
  /// Fallback length estimate when no format-specific probe is available: bytes
  /// until the next scanner hit of any format, capped at the buffer end.
  /// </summary>
  private static long EstimateLengthFallback(long start, List<ScanResult> hits, int bufferLen) {
    long nextHit = bufferLen;
    foreach (var h in hits)
      if (h.Offset > start && h.Offset < nextHit) nextHit = h.Offset;
    return nextHit - start;
  }

  /// <summary>
  /// Drops hits wholly contained inside other hits (e.g. a JPEG thumbnail inside
  /// a larger JPEG). Keeps the outermost payload per region.
  /// </summary>
  private static List<CarvedPayload> DeduplicateOverlapping(
      List<CarvedPayload> raw, int maxResults) {
    var sorted = raw.OrderBy(p => p.Offset).ThenByDescending(p => p.Length).ToList();
    var kept = new List<CarvedPayload>();
    foreach (var p in sorted) {
      var containedBySomething = false;
      foreach (var k in kept) {
        if (k.Offset <= p.Offset && k.Offset + k.Length >= p.Offset + p.Length) {
          containedBySomething = true;
          break;
        }
      }
      if (!containedBySomething) kept.Add(p);
      if (kept.Count >= maxResults) break;
    }
    return kept;
  }

  private static string DefaultExtension(string formatId) => formatId switch {
    "Zip" or "Apk" or "Jar" or "Docx" or "Xlsx" or "Pptx" or "Odt" or "Ods" or "Odp"
      or "Epub" or "Cbz" or "Appx" or "NuPkg" or "Kmz" or "Maff" or "Crx"
      or "Xpi" or "Ipa" or "Ear" or "War" => ".zip",
    "Png" => ".png",
    "Jpeg" or "JpegArchive" or "Mpo" => ".jpg",
    "Gif" => ".gif",
    "Wav" => ".wav",
    "Mp4" => ".mp4",
    "Mkv" => ".mkv",
    "Gzip" => ".gz",
    "Bzip2" => ".bz2",
    "Xz" => ".xz",
    "Tar" => ".tar",
    "Ogg" => ".ogg",
    "Flac" => ".flac",
    "Pdf" => ".pdf",
    _ => ".bin",
  };
}
