#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zip;

public sealed class ZipFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IFormatValidator {
  public string Id => "Zip";
  public string DisplayName => "ZIP";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsOptimize;
  public string DefaultExtension => ".zip";
  public IReadOnlyList<string> Extensions => [".zip", ".zipx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'P', (byte)'K', 0x03, 0x04], Confidence: 0.95),
    new([(byte)'P', (byte)'K', 0x05, 0x06], Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("deflate", "Deflate", SupportsOptimize: true),
    new("store", "Store"), new("deflate64", "Deflate64"),
    new("bzip2", "BZip2"), new("lzma", "LZMA"), new("zstd", "Zstandard"), new("ppmd", "PPMd")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Universal archive with multiple compression methods";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZipReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.FileName)); continue; }
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  // ── IFormatValidator ─────────────────────────────────────────────

  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var issues = new List<ValidationIssue>();
    if (header.Length < 30) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "ZIP_TOO_SHORT",
        "File too short for local file header (need 30 bytes minimum)"));
      return new() { IsValid = false, Confidence = 0.3, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    var sig = BitConverter.ToUInt32(header);
    if (sig != ZipConstants.LocalFileHeaderSignature && sig != ZipConstants.EndOfCentralDirectorySignature) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "ZIP_BAD_SIGNATURE",
        $"Invalid ZIP signature: 0x{sig:X8}", 0));
      return new() { IsValid = false, Confidence = 0.2, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    if (sig == ZipConstants.LocalFileHeaderSignature) {
      var versionNeeded = BitConverter.ToUInt16(header[4..]);
      if (versionNeeded > 63) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "ZIP_HIGH_VERSION",
          $"Version needed to extract is unusually high: {versionNeeded / 10}.{versionNeeded % 10}", 4));
      }
      var method = BitConverter.ToUInt16(header[8..]);
      if (method != 0 && method != 8 && method != 9 && method != 12 && method != 14 &&
          method != 93 && method != 98 && method != 99) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "ZIP_UNKNOWN_METHOD",
          $"Unknown compression method: {method}", 8));
      }
      var fnLen = BitConverter.ToUInt16(header[26..]);
      var exLen = BitConverter.ToUInt16(header[28..]);
      if (30 + fnLen + exLen > fileSize) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "ZIP_HEADER_OVERFLOW",
          "Local file header extends beyond file size"));
        return new() { IsValid = false, Confidence = 0.4, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Header, Issues = issues };
      }
    }
    if (fileSize < 22) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "ZIP_NO_EOCD",
        "File too short to contain end of central directory record (min 22 bytes)"));
    }
    var confidence = issues.Any(i => i.Severity == IssueSeverity.Warning) ? 0.80 : 0.90;
    var health = issues.Any(i => i.Severity >= IssueSeverity.Warning) ? FormatHealth.Degraded : FormatHealth.Good;
    return new() { IsValid = true, Confidence = confidence, Health = health,
      Level = ValidationLevel.Header, Issues = issues };
  }

  public ValidationResult ValidateStructure(Stream stream) {
    var issues = new List<ValidationIssue>();
    int entryCount;
    try {
      var (cdOffset, cdSize, cdCount, _) = ZipEndOfCentralDirectory.Read(stream);
      entryCount = cdCount;
      if (cdOffset < 0 || cdOffset > stream.Length) {
        issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_CD_OFFSET_OOB",
          $"Central directory offset {cdOffset} is outside file bounds"));
        return new() { IsValid = false, Confidence = 0.6, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Structure, Issues = issues };
      }
      if (cdOffset + cdSize > stream.Length) {
        issues.Add(new(ValidationLevel.Structure, IssueSeverity.Warning, "ZIP_CD_TRUNCATED",
          $"Central directory extends beyond file (offset={cdOffset}, size={cdSize}, fileLen={stream.Length})"));
      }
      // Walk central directory entries
      stream.Position = cdOffset;
      var reader = new BinaryReader(stream, System.Text.Encoding.Latin1, leaveOpen: true);
      var validEntries = 0;
      for (var i = 0; i < cdCount; ++i) {
        if (stream.Position + 46 > stream.Length) {
          issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_CD_ENTRY_TRUNCATED",
            $"Central directory entry {i} truncated", stream.Position));
          break;
        }
        var entrySig = reader.ReadUInt32();
        if (entrySig != ZipConstants.CentralDirectorySignature) {
          issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_CD_BAD_SIG",
            $"Central directory entry {i}: bad signature 0x{entrySig:X8}", stream.Position - 4));
          break;
        }
        // Skip past the fixed fields to get name/extra/comment lengths
        stream.Position += 24; // skip to fnLen (offset 46-4-24 = fields after sig)
        var fnLen = reader.ReadUInt16();
        var exLen = reader.ReadUInt16();
        var cmtLen = reader.ReadUInt16();
        stream.Position += 12; // skip diskStart(2)+internalAttr(2)+externalAttr(4)+localHeaderOffset(4)
        stream.Position += fnLen + exLen + cmtLen;
        ++validEntries;
      }
      var confidence = issues.Count == 0 ? 0.92 : 0.75;
      var health = issues.Any(i => i.Severity >= IssueSeverity.Error) ? FormatHealth.Damaged
        : issues.Any(i => i.Severity >= IssueSeverity.Warning) ? FormatHealth.Degraded : FormatHealth.Good;
      return new() { IsValid = health != FormatHealth.Damaged, Confidence = confidence, Health = health,
        Level = ValidationLevel.Structure, Issues = issues,
        ValidEntries = validEntries, TotalEntries = entryCount };
    } catch (InvalidDataException ex) {
      issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_STRUCTURE_FAILED",
        $"Structure parse failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Structure, Issues = issues };
    }
  }

  public ValidationResult ValidateIntegrity(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      var r = new ZipReader(stream);
      var validEntries = 0;
      var totalEntries = r.Entries.Count;
      foreach (var e in r.Entries) {
        if (e.IsDirectory) { ++validEntries; continue; }
        try {
          _ = r.ExtractEntry(e);
          ++validEntries;
        } catch (Exception ex) {
          issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "ZIP_ENTRY_EXTRACT_FAILED",
            $"Entry '{e.FileName}': {ex.Message}"));
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
      issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "ZIP_INTEGRITY_FAILED",
        $"Integrity check failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Integrity, Issues = issues };
    }
  }
}
