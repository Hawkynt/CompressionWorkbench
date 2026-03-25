#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.SevenZip;

public sealed class SevenZipFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IFormatValidator {
  public string Id => "SevenZip";
  public string DisplayName => "7z";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".7z";
  public IReadOnlyList<string> Extensions => [".7z"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("lzma2", "LZMA2"), new("lzma", "LZMA"), new("ppmd", "PPMd"),
    new("bzip2", "BZip2"), new("deflate", "Deflate"), new("copy", "Store")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "7-Zip archive with LZMA2, high compression ratio";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SevenZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      string.IsNullOrEmpty(e.Method) ? "7z" : e.Method, e.IsDirectory, false, e.LastWriteTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SevenZipReader(stream, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      WriteFile(outputDir, e.Name, r.Extract(i));
    }
  }

  // ── IFormatValidator ─────────────────────────────────────────────

  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var issues = new List<ValidationIssue>();
    if (header.Length < SevenZipConstants.SignatureHeaderSize) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "7Z_TOO_SHORT",
        $"File too short for 7z signature header (need {SevenZipConstants.SignatureHeaderSize} bytes)"));
      return new() { IsValid = false, Confidence = 0.3, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    var majorVersion = header[6];
    if (majorVersion > 0) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "7Z_UNKNOWN_MAJOR_VERSION",
        $"Unknown major version: {majorVersion} (expected 0)", 6));
    }
    // Verify StartHeaderCRC (CRC of bytes 12..31 = 20 bytes)
    var storedStartCrc = BitConverter.ToUInt32(header[8..]);
    var computedStartCrc = Compression.Core.Checksums.Crc32.Compute(header.Slice(12, 20));
    if (storedStartCrc != computedStartCrc) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "7Z_START_HEADER_CRC",
        $"Start header CRC mismatch: stored=0x{storedStartCrc:X8}, computed=0x{computedStartCrc:X8}", 8));
      return new() { IsValid = false, Confidence = 0.4, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    var nextHeaderOffset = BitConverter.ToInt64(header[12..]);
    var nextHeaderSize = BitConverter.ToInt64(header[20..]);
    if (nextHeaderOffset < 0 || nextHeaderSize < 0) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "7Z_NEGATIVE_OFFSET",
        $"Negative next header offset ({nextHeaderOffset}) or size ({nextHeaderSize})"));
      return new() { IsValid = false, Confidence = 0.4, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    var nextHeaderEnd = SevenZipConstants.SignatureHeaderSize + nextHeaderOffset + nextHeaderSize;
    if (nextHeaderEnd > fileSize) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "7Z_HEADER_BEYOND_EOF",
        $"Next header extends beyond file (offset={nextHeaderOffset}, size={nextHeaderSize}, fileSize={fileSize})"));
    }
    var confidence = issues.Count == 0 ? 0.92 : 0.75;
    var health = issues.Any(i => i.Severity >= IssueSeverity.Warning) ? FormatHealth.Degraded : FormatHealth.Good;
    return new() { IsValid = true, Confidence = confidence, Health = health,
      Level = ValidationLevel.Header, Issues = issues };
  }

  public ValidationResult ValidateStructure(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var headerBytes = new byte[SevenZipConstants.SignatureHeaderSize];
      stream.ReadExactly(headerBytes);
      var nextHeaderOffset = BitConverter.ToInt64(headerBytes, 12);
      var nextHeaderSize = BitConverter.ToInt64(headerBytes, 20);
      var nextHeaderCrc = BitConverter.ToUInt32(headerBytes, 28);
      var nextHeaderPos = SevenZipConstants.SignatureHeaderSize + nextHeaderOffset;
      if (nextHeaderPos + nextHeaderSize > stream.Length) {
        issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "7Z_NEXT_HEADER_TRUNCATED",
          "Next header extends beyond stream"));
        return new() { IsValid = false, Confidence = 0.6, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Structure, Issues = issues };
      }
      stream.Seek(nextHeaderPos, SeekOrigin.Begin);
      var nextHeaderData = new byte[nextHeaderSize];
      stream.ReadExactly(nextHeaderData);
      var actualCrc = Compression.Core.Checksums.Crc32.Compute(nextHeaderData);
      if (actualCrc != nextHeaderCrc) {
        issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "7Z_NEXT_HEADER_CRC",
          $"Next header CRC mismatch: stored=0x{nextHeaderCrc:X8}, computed=0x{actualCrc:X8}"));
        return new() { IsValid = false, Confidence = 0.7, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Structure, Issues = issues };
      }
      return new() { IsValid = true, Confidence = 0.93, Health = FormatHealth.Good,
        Level = ValidationLevel.Structure, Issues = issues };
    } catch (Exception ex) {
      issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "7Z_STRUCTURE_FAILED",
        $"Structure validation failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Structure, Issues = issues };
    }
  }

  public ValidationResult ValidateIntegrity(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var r = new SevenZipReader(stream);
      var validEntries = 0;
      var totalEntries = r.Entries.Count;
      for (var i = 0; i < totalEntries; ++i) {
        var e = r.Entries[i];
        if (e.IsDirectory) { ++validEntries; continue; }
        try {
          _ = r.Extract(i);
          ++validEntries;
        } catch (Exception ex) {
          issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "7Z_ENTRY_EXTRACT_FAILED",
            $"Entry '{e.Name}': {ex.Message}"));
        }
      }
      if (validEntries == totalEntries && issues.Count == 0) {
        return new() { IsValid = true, Confidence = 0.99, Health = FormatHealth.Perfect,
          Level = ValidationLevel.Integrity, Issues = issues,
          ValidEntries = validEntries, TotalEntries = totalEntries };
      }
      var health = validEntries == 0 ? FormatHealth.Damaged
        : validEntries < totalEntries ? FormatHealth.Degraded : FormatHealth.Good;
      return new() { IsValid = validEntries > 0, Confidence = validEntries > 0 ? 0.90 : 0.5,
        Health = health, Level = ValidationLevel.Integrity, Issues = issues,
        ValidEntries = validEntries, TotalEntries = totalEntries };
    } catch (Exception ex) {
      issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "7Z_INTEGRITY_FAILED",
        $"Integrity check failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Integrity, Issues = issues };
    }
  }
}
