#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Tar;

public sealed class TarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IFormatValidator {
  public string Id => "Tar";
  public string DisplayName => "TAR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".tar";
  public IReadOnlyList<string> Extensions => [".tar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x75, 0x73, 0x74, 0x61, 0x72], Offset: 257, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("tar", "TAR")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unix tape archive, no compression, container only";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new TarReader(stream);
    var entries = new List<ArchiveEntryInfo>();
    var i = 0;
    while (r.GetNextEntry() is { } e) {
      entries.Add(new(i++, e.Name, e.Size, e.Size, "tar", e.IsDirectory, false, e.ModifiedTime.DateTime));
      r.Skip();
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TarReader(stream);
    while (r.GetNextEntry() is { } e) {
      if (files != null && !MatchesFilter(e.Name, files)) { r.Skip(); continue; }
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); r.Skip(); continue; }
      using var es = r.GetEntryStream();
      var data = new byte[e.Size];
      es.ReadExactly(data);
      WriteFile(outputDir, e.Name, data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new TarWriter(output);
    foreach (var i in inputs) {
      if (i.IsDirectory) {
        w.AddEntry(new TarEntry { Name = i.ArchiveName, Size = 0, TypeFlag = (byte)'5' }, []);
      } else {
        var data = File.ReadAllBytes(i.FullPath);
        w.AddEntry(new TarEntry { Name = i.ArchiveName, Size = data.Length }, data);
      }
    }
    w.Finish();
  }

  // ── IFormatValidator ─────────────────────────────────────────────

  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var issues = new List<ValidationIssue>();
    if (header.Length < TarConstants.BlockSize) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "TAR_TOO_SHORT",
        "File too short for TAR header (need 512 bytes minimum)"));
      return new() { IsValid = false, Confidence = 0.3, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    // Verify ustar magic at offset 257
    if (header.Length > 262) {
      var magic = System.Text.Encoding.ASCII.GetString(header.Slice(257, 5));
      if (magic != TarConstants.UstarMagic) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "TAR_NO_USTAR",
          $"No ustar magic at offset 257 (got '{magic}')"));
      }
    }
    // Verify header checksum
    var storedChecksum = ParseOctal(header.Slice(148, 8));
    if (storedChecksum >= 0) {
      // Compute checksum: sum of all bytes with checksum field treated as spaces (0x20)
      var computed = 0;
      for (var i = 0; i < TarConstants.BlockSize; ++i) {
        computed += (i >= 148 && i < 156) ? (byte)' ' : header[i];
      }
      if (computed != storedChecksum) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "TAR_BAD_CHECKSUM",
          $"Header checksum mismatch: stored={storedChecksum}, computed={computed}"));
        return new() { IsValid = false, Confidence = 0.4, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Header, Issues = issues };
      }
    }
    // Check type flag is known
    var typeFlag = header[156];
    if (typeFlag != 0 && typeFlag != (byte)'0' && typeFlag != (byte)'1' && typeFlag != (byte)'2' &&
        typeFlag != (byte)'3' && typeFlag != (byte)'4' && typeFlag != (byte)'5' && typeFlag != (byte)'6' &&
        typeFlag != (byte)'7' && typeFlag != (byte)'L' && typeFlag != (byte)'K' &&
        typeFlag != (byte)'x' && typeFlag != (byte)'g' && typeFlag != (byte)'M' && typeFlag != (byte)'S') {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Info, "TAR_UNUSUAL_TYPEFLAG",
        $"Unusual type flag: '{(char)typeFlag}' (0x{typeFlag:X2})", 156));
    }
    if (fileSize % TarConstants.BlockSize != 0) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Info, "TAR_UNALIGNED",
        $"File size {fileSize} is not a multiple of 512 bytes"));
    }
    var confidence = issues.Any(i => i.Severity == IssueSeverity.Warning) ? 0.80 :
      issues.Any(i => i.Severity == IssueSeverity.Info) ? 0.88 : 0.92;
    var health = issues.Any(i => i.Severity >= IssueSeverity.Warning) ? FormatHealth.Degraded : FormatHealth.Good;
    return new() { IsValid = true, Confidence = confidence, Health = health,
      Level = ValidationLevel.Header, Issues = issues };
  }

  public ValidationResult ValidateStructure(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var r = new TarReader(stream);
      var entryCount = 0;
      while (r.GetNextEntry() is { } e) {
        ++entryCount;
        r.Skip();
      }
      return new() { IsValid = true, Confidence = 0.93, Health = FormatHealth.Good,
        Level = ValidationLevel.Structure, Issues = issues,
        ValidEntries = entryCount, TotalEntries = entryCount };
    } catch (Exception ex) {
      issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "TAR_STRUCTURE_FAILED",
        $"TAR structure parse failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.6, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Structure, Issues = issues };
    }
  }

  public ValidationResult ValidateIntegrity(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var r = new TarReader(stream);
      var validEntries = 0;
      var totalEntries = 0;
      while (r.GetNextEntry() is { } e) {
        ++totalEntries;
        try {
          if (e.IsDirectory) { r.Skip(); ++validEntries; continue; }
          using var es = r.GetEntryStream();
          // Read all data to verify no truncation
          var remaining = e.Size;
          var buf = new byte[8192];
          while (remaining > 0) {
            var toRead = (int)Math.Min(buf.Length, remaining);
            var read = es.Read(buf, 0, toRead);
            if (read == 0) {
              issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "TAR_ENTRY_TRUNCATED",
                $"Entry '{e.Name}': premature end of data (expected {e.Size} bytes)"));
              break;
            }
            remaining -= read;
          }
          if (remaining == 0) ++validEntries;
        } catch (Exception ex) {
          issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "TAR_ENTRY_FAILED",
            $"Entry '{e.Name}': {ex.Message}"));
        }
      }
      if (validEntries == totalEntries && issues.Count == 0) {
        return new() { IsValid = true, Confidence = 0.97, Health = FormatHealth.Perfect,
          Level = ValidationLevel.Integrity, Issues = issues,
          ValidEntries = validEntries, TotalEntries = totalEntries };
      }
      var health = validEntries == 0 ? FormatHealth.Damaged
        : validEntries < totalEntries ? FormatHealth.Degraded : FormatHealth.Good;
      return new() { IsValid = validEntries > 0, Confidence = validEntries > 0 ? 0.88 : 0.5,
        Health = health, Level = ValidationLevel.Integrity, Issues = issues,
        ValidEntries = validEntries, TotalEntries = totalEntries };
    } catch (Exception ex) {
      issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "TAR_INTEGRITY_FAILED",
        $"Integrity check failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Integrity, Issues = issues };
    }
  }

  private static long ParseOctal(ReadOnlySpan<byte> data) {
    long result = 0;
    foreach (var b in data) {
      if (b == 0 || b == (byte)' ') break;
      if (b < (byte)'0' || b > (byte)'7') return -1;
      result = (result << 3) | (long)(b - (byte)'0');
    }
    return result;
  }
}
