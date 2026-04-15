#pragma warning disable CS1591

using Compression.Registry;

namespace Compression.Analysis.ReverseEngineering;

/// <summary>
/// Analyzes archive files with known original content (no tool needed).
/// Given pairs of (original content, archive file), finds where the content is stored,
/// what compression is used, and infers the format structure.
/// </summary>
public sealed class StaticFormatAnalyzer {

  /// <summary>A sample: known original content paired with the archive that contains it.</summary>
  public sealed class Sample {
    public required string Name { get; init; }
    public required byte[] OriginalContent { get; init; }
    public required byte[] ArchiveBytes { get; init; }
    /// <summary>Original filename (if the archive might store it).</summary>
    public string? OriginalFileName { get; init; }
  }

  /// <summary>Where original content was found inside the archive.</summary>
  public sealed record ContentLocation {
    public required string SampleName { get; init; }
    public required int ArchiveOffset { get; init; }
    public required int Length { get; init; }
    public required string StorageMethod { get; init; } // "verbatim", building block name, or "not found"
    public required double Confidence { get; init; }
    public byte[]? CompressedForm { get; init; }
  }

  /// <summary>A region of the archive that's not content — likely header/metadata.</summary>
  public sealed class MetadataRegion {
    public required int Offset { get; init; }
    public required int Length { get; init; }
    public required string Classification { get; init; } // "header", "footer", "inter-file", "filename", "size-field"
    public required byte[] Bytes { get; init; }
    public string? Detail { get; init; }
  }

  /// <summary>Full static analysis report.</summary>
  public sealed class StaticAnalysisReport {
    public required int SampleCount { get; init; }
    public required List<ContentLocation> ContentLocations { get; init; }
    public required List<MetadataRegion> MetadataRegions { get; init; }
    public required OutputCorrelator.FixedRegion? CommonHeader { get; init; }
    public required OutputCorrelator.FixedRegion? CommonFooter { get; init; }
    public required List<OutputCorrelator.SizeField> SizeFields { get; init; }
    public required CompressionIdentifier.IdentificationResult? CompressionAnalysis { get; init; }
    public required List<string> FilenameLocations { get; init; }
    public required string Summary { get; init; }
  }

  /// <summary>
  /// Analyzes archive files with known content to infer format structure.
  /// </summary>
  /// <param name="samples">One or more (original, archive) pairs.</param>
  /// <param name="progress">Optional progress callback (step description, current, total).</param>
  public static StaticAnalysisReport Analyze(
    IReadOnlyList<Sample> samples,
    Action<string, int, int>? progress = null
  ) {
    var contentLocations = new List<ContentLocation>();
    var filenameLocations = new List<string>();
    var step = 0;
    var totalSteps = samples.Count * 3; // locate + compress-match + filename for each

    // Phase 1: For each sample, find where the content lives in the archive.
    foreach (var sample in samples) {
      progress?.Invoke($"Locating content: {sample.Name}", ++step, totalSteps);

      // Try verbatim (uncompressed) match.
      var verbatimOffset = FindSubsequence(sample.ArchiveBytes, sample.OriginalContent);
      if (verbatimOffset >= 0) {
        contentLocations.Add(new() {
          SampleName = sample.Name, ArchiveOffset = verbatimOffset,
          Length = sample.OriginalContent.Length, StorageMethod = "verbatim",
          Confidence = 1.0
        });
        step++; // skip compress-match
      } else {
        progress?.Invoke($"Trying compressed matches: {sample.Name}", ++step, totalSteps);

        // Try compressing with each building block and searching for the compressed form.
        var bestMatch = TryCompressedMatch(sample.OriginalContent, sample.ArchiveBytes);
        if (bestMatch != null)
          contentLocations.Add(bestMatch with { SampleName = sample.Name });
        else
          contentLocations.Add(new() {
            SampleName = sample.Name, ArchiveOffset = -1, Length = 0,
            StorageMethod = "not found", Confidence = 0
          });
      }

      // Phase 2: Search for filename in the archive.
      progress?.Invoke($"Searching for filename: {sample.Name}", ++step, totalSteps);
      if (sample.OriginalFileName != null) {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(sample.OriginalFileName);
        var nameOffset = FindSubsequence(sample.ArchiveBytes, nameBytes);
        if (nameOffset >= 0)
          filenameLocations.Add($"{sample.OriginalFileName} found at offset {nameOffset} in {sample.Name}");

        // Also try UTF-16LE.
        var nameUtf16 = System.Text.Encoding.Unicode.GetBytes(sample.OriginalFileName);
        var nameOffset16 = FindSubsequence(sample.ArchiveBytes, nameUtf16);
        if (nameOffset16 >= 0 && nameOffset16 != nameOffset)
          filenameLocations.Add($"{sample.OriginalFileName} (UTF-16LE) found at offset {nameOffset16} in {sample.Name}");
      }
    }

    // Phase 3: Cross-sample correlation (if multiple samples).
    OutputCorrelator.FixedRegion? commonHeader = null;
    OutputCorrelator.FixedRegion? commonFooter = null;
    var sizeFields = new List<OutputCorrelator.SizeField>();

    if (samples.Count >= 2) {
      // Reuse OutputCorrelator by treating archives as probe outputs.
      var runs = samples.Select(s => new OutputCorrelator.ProbeRun {
        Input = new ProbeGenerator.Probe { Name = s.Name, Data = s.OriginalContent, Description = s.Name },
        Output = s.ArchiveBytes
      }).ToList();

      commonHeader = OutputCorrelator.FindCommonHeader(runs);
      commonFooter = OutputCorrelator.FindCommonFooter(runs);
      sizeFields = OutputCorrelator.FindSizeFields(runs);
    }

    // Phase 4: Identify compression from located content.
    CompressionIdentifier.IdentificationResult? compressionAnalysis = null;
    var foundContent = contentLocations.FirstOrDefault(c => c.StorageMethod != "verbatim" && c.StorageMethod != "not found");
    if (foundContent is { CompressedForm: not null }) {
      compressionAnalysis = CompressionIdentifier.Identify(
        foundContent.CompressedForm,
        samples.First(s => s.Name == foundContent.SampleName).OriginalContent.Length
      );
    } else if (samples.Count > 0) {
      // Try identifying from the payload region between header and footer.
      var headerLen = commonHeader?.Length ?? 0;
      var footerLen = commonFooter?.Length ?? 0;
      var archive = samples[0].ArchiveBytes;
      if (archive.Length > headerLen + footerLen + 4) {
        var payload = archive.AsSpan(headerLen, archive.Length - headerLen - footerLen);
        compressionAnalysis = CompressionIdentifier.Identify(payload, samples[0].OriginalContent.Length);
      }
    }

    // Phase 5: Build metadata region map.
    var metadataRegions = BuildMetadataMap(samples, contentLocations, commonHeader, commonFooter, filenameLocations);

    // Build summary.
    var summary = BuildSummary(samples.Count, contentLocations, commonHeader, commonFooter, sizeFields, compressionAnalysis, filenameLocations);

    return new() {
      SampleCount = samples.Count,
      ContentLocations = contentLocations,
      MetadataRegions = metadataRegions,
      CommonHeader = commonHeader,
      CommonFooter = commonFooter,
      SizeFields = sizeFields,
      CompressionAnalysis = compressionAnalysis,
      FilenameLocations = filenameLocations,
      Summary = summary
    };
  }

  private static ContentLocation? TryCompressedMatch(byte[] original, byte[] archive) {
    if (original.Length == 0) return null;

    foreach (var block in BuildingBlockRegistry.All) {
      try {
        var compressed = block.Compress(original);
        if (compressed.Length < 4) continue;

        // Search for the compressed form in the archive.
        var offset = FindSubsequence(archive, compressed);
        if (offset >= 0) {
          return new() {
            SampleName = "", ArchiveOffset = offset, Length = compressed.Length,
            StorageMethod = block.DisplayName, Confidence = 0.95,
            CompressedForm = compressed
          };
        }

        // Also try searching for the compressed form without the building block's own header.
        // Many building blocks prepend a size header (4-5 bytes). Try skipping it.
        foreach (var skip in new[] { 4, 5, 8 }) {
          if (compressed.Length <= skip + 8) continue;
          var stripped = compressed[skip..];
          offset = FindSubsequence(archive, stripped);
          if (offset >= 0) {
            return new() {
              SampleName = "", ArchiveOffset = offset, Length = stripped.Length,
              StorageMethod = $"{block.DisplayName} (raw, no BB header)", Confidence = 0.85,
              CompressedForm = stripped
            };
          }
        }
      } catch {
        // Block can't compress this data.
      }
    }

    // Try partial matches: search for first 16+ bytes of each compressed form.
    foreach (var block in BuildingBlockRegistry.All) {
      try {
        var compressed = block.Compress(original);
        if (compressed.Length < 20) continue;

        // Search for first 16 bytes as a fingerprint.
        var fingerprint = compressed[..16];
        var offset = FindSubsequence(archive, fingerprint);
        if (offset >= 0) {
          return new() {
            SampleName = "", ArchiveOffset = offset, Length = -1,
            StorageMethod = $"{block.DisplayName} (partial fingerprint match)", Confidence = 0.5,
            CompressedForm = compressed
          };
        }
      } catch { /* skip */ }
    }

    return null;
  }

  private static int FindSubsequence(byte[] haystack, byte[] needle) {
    if (needle.Length == 0 || needle.Length > haystack.Length) return -1;

    var span = haystack.AsSpan();
    var needleSpan = needle.AsSpan();

    for (var i = 0; i <= span.Length - needleSpan.Length; i++) {
      if (span.Slice(i, needleSpan.Length).SequenceEqual(needleSpan))
        return i;
    }
    return -1;
  }

  private static List<MetadataRegion> BuildMetadataMap(
    IReadOnlyList<Sample> samples,
    List<ContentLocation> locations,
    OutputCorrelator.FixedRegion? header,
    OutputCorrelator.FixedRegion? footer,
    List<string> filenameLocations
  ) {
    var regions = new List<MetadataRegion>();

    if (header != null)
      regions.Add(new() { Offset = 0, Length = header.Length, Classification = "header", Bytes = header.Bytes });

    if (footer != null && samples.Count > 0) {
      var archiveLen = samples[0].ArchiveBytes.Length;
      var footerOffset = archiveLen - footer.Length;
      regions.Add(new() { Offset = footerOffset, Length = footer.Length, Classification = "footer", Bytes = footer.Bytes });
    }

    return regions;
  }

  private static string BuildSummary(
    int sampleCount,
    List<ContentLocation> locations,
    OutputCorrelator.FixedRegion? header,
    OutputCorrelator.FixedRegion? footer,
    List<OutputCorrelator.SizeField> sizeFields,
    CompressionIdentifier.IdentificationResult? compression,
    List<string> filenameLocations
  ) {
    var lines = new List<string> { "=== Static Format Analysis Report ===" };
    lines.Add($"Samples analyzed: {sampleCount}");

    if (header != null)
      lines.Add($"Common header: {header.Length} bytes — [{string.Join(" ", header.Bytes.Select(b => $"0x{b:X2}"))}]");

    if (footer != null)
      lines.Add($"Common footer: {footer.Length} bytes — [{string.Join(" ", footer.Bytes.Select(b => $"0x{b:X2}"))}]");

    foreach (var sf in sizeFields)
      lines.Add($"Size field at offset {sf.Offset}: {sf.Width}-byte {sf.Endianness}, meaning: {sf.Meaning}");

    foreach (var loc in locations) {
      if (loc.StorageMethod == "verbatim")
        lines.Add($"  [{loc.SampleName}] stored verbatim at offset {loc.ArchiveOffset} ({loc.Length} bytes)");
      else if (loc.StorageMethod == "not found")
        lines.Add($"  [{loc.SampleName}] content not found in archive");
      else
        lines.Add($"  [{loc.SampleName}] compressed with {loc.StorageMethod} at offset {loc.ArchiveOffset} ({loc.Length} bytes, confidence: {loc.Confidence:P0})");
    }

    if (filenameLocations.Count > 0) {
      lines.Add("Filename storage:");
      foreach (var fl in filenameLocations)
        lines.Add($"  {fl}");
    }

    if (compression?.BestGuess != null)
      lines.Add($"Compression algorithm: {compression.BestGuess}");

    return string.Join(Environment.NewLine, lines);
  }
}
