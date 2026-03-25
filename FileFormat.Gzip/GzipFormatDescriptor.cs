#pragma warning disable CS1591 // Missing XML comment

using Compression.Registry;

namespace FileFormat.Gzip;

public sealed class GzipFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatValidator {
  public string Id => "Gzip";
  public string DisplayName => "GZIP";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsBenchmark | FormatCapabilities.SupportsOptimize | FormatCapabilities.CanCompoundWithTar;
  public string DefaultExtension => ".gz";
  public IReadOnlyList<string> Extensions => [".gz", ".gzip"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x1F, 0x8B], Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Deflate with CRC32, the ubiquitous HTTP/file compression standard";

  public void Decompress(Stream input, Stream output) {
    using var ds = new GzipStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }

  public void Compress(Stream input, Stream output) {
    using var cs = new GzipStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }

  public void CompressOptimal(Stream input, Stream output) {
    using var cs = new GzipStream(output, Compression.Core.Streams.CompressionStreamMode.Compress,
      Compression.Core.Deflate.DeflateCompressionLevel.Maximum, leaveOpen: true);
    input.CopyTo(cs);
  }

  public Stream? WrapDecompress(Stream input) =>
    new GzipStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);

  public Stream? WrapCompress(Stream output) =>
    new GzipStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);

  // ── IFormatValidator ─────────────────────────────────────────────

  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var issues = new List<ValidationIssue>();
    if (header.Length < 10) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "GZIP_TOO_SHORT",
        "File too short for GZIP header (need 10 bytes minimum)"));
      return new() { IsValid = false, Confidence = 0.3, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    // Magic already checked by scanner
    var cm = header[2];
    if (cm != GzipConstants.MethodDeflate) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "GZIP_BAD_METHOD",
        $"Unknown compression method {cm} (expected 8=Deflate)", 2));
      return new() { IsValid = false, Confidence = 0.4, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    var flags = header[3];
    if ((flags & 0xE0) != 0) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "GZIP_RESERVED_FLAGS",
        $"Reserved flag bits set: 0x{flags:X2}", 3));
    }
    var xfl = header[8];
    if (xfl != 0 && xfl != 2 && xfl != 4) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Info, "GZIP_UNUSUAL_XFL",
        $"Unusual extra flags value: {xfl}", 8));
    }
    if (fileSize < 18) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "GZIP_NO_TRAILER",
        "File too short to contain GZIP trailer (CRC32 + ISIZE)"));
    }
    var confidence = issues.Any(i => i.Severity == IssueSeverity.Warning) ? 0.75 : 0.85;
    var health = issues.Any(i => i.Severity >= IssueSeverity.Warning) ? FormatHealth.Degraded : FormatHealth.Good;
    return new() { IsValid = true, Confidence = confidence, Health = health,
      Level = ValidationLevel.Header, Issues = issues };
  }

  public ValidationResult ValidateStructure(Stream stream) {
    var issues = new List<ValidationIssue>();
    // GZIP is a stream format — structure check verifies trailer is present
    if (stream.Length < 18) {
      issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "GZIP_TRUNCATED",
        "Stream too short for header + trailer"));
      return new() { IsValid = false, Confidence = 0.6, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Structure, Issues = issues };
    }
    // Read trailer (last 8 bytes: CRC32 LE + ISIZE LE)
    stream.Seek(-8, SeekOrigin.End);
    var trailer = new byte[8];
    stream.ReadExactly(trailer);
    var isize = BitConverter.ToUInt32(trailer, 4);
    // ISIZE is original size mod 2^32 — plausibility check
    if (isize == 0 && stream.Length > 20) {
      issues.Add(new(ValidationLevel.Structure, IssueSeverity.Info, "GZIP_ISIZE_ZERO",
        "ISIZE is 0 (empty original or size is exact multiple of 4GB)"));
    }
    return new() { IsValid = true, Confidence = 0.88, Health = issues.Count > 0 ? FormatHealth.Good : FormatHealth.Good,
      Level = ValidationLevel.Structure, Issues = issues };
  }

  public ValidationResult ValidateIntegrity(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      stream.Seek(0, SeekOrigin.Begin);
      using var decompressed = new MemoryStream();
      using (var gz = new GzipStream(stream, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true))
        gz.CopyTo(decompressed);
      // Read stored CRC from trailer
      stream.Seek(-8, SeekOrigin.End);
      var trailer = new byte[8];
      stream.ReadExactly(trailer);
      var storedCrc = BitConverter.ToUInt32(trailer, 0);
      var storedSize = BitConverter.ToUInt32(trailer, 4);
      var actualCrc = Compression.Core.Checksums.Crc32.Compute(decompressed.ToArray());
      var actualSize = (uint)(decompressed.Length & 0xFFFFFFFF);
      if (storedCrc != actualCrc) {
        issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "GZIP_CRC_MISMATCH",
          $"CRC-32 mismatch: stored=0x{storedCrc:X8}, computed=0x{actualCrc:X8}"));
        return new() { IsValid = false, Confidence = 0.95, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Integrity, Issues = issues };
      }
      if (storedSize != actualSize) {
        issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Warning, "GZIP_SIZE_MISMATCH",
          $"ISIZE mismatch: stored={storedSize}, actual={actualSize}"));
      }
      return new() { IsValid = true, Confidence = 0.99, Health = issues.Count > 0 ? FormatHealth.Degraded : FormatHealth.Perfect,
        Level = ValidationLevel.Integrity, Issues = issues };
    } catch (Exception ex) {
      issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "GZIP_DECOMPRESS_FAILED",
        $"Decompression failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.85, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Integrity, Issues = issues };
    }
  }
}
