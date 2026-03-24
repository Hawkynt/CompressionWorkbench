using Compression.Analysis.Scanning;
using Compression.Registry;

namespace Compression.Tests.Analysis;

public class FormatProberTests {

  [SetUp]
  public void EnsureRegistered() => Compression.Lib.FormatRegistration.EnsureInitialized();

  // ── Gzip ───────────────────────────────────────────────────────

  [Test]
  public void Gzip_ValidFile_ProbesWithHighConfidence() {
    var data = CreateGzip([1, 2, 3, 4, 5]);
    using var stream = new MemoryStream(data);
    var prober = new FormatProber();
    var result = prober.ProbeFormat(stream, "Gzip");
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Health, Is.EqualTo(FormatHealth.Perfect));
    Assert.That(result.Confidence, Is.GreaterThanOrEqualTo(0.95));
    Assert.That(result.HighestLevel, Is.EqualTo(ValidationLevel.Integrity));
    Assert.That(result.Issues, Is.Empty);
  }

  [Test]
  public void Gzip_TruncatedFile_DetectsDamage() {
    var data = CreateGzip([1, 2, 3]);
    var truncated = data[..^4]; // Remove trailer
    using var stream = new MemoryStream(truncated);
    var prober = new FormatProber(ValidationLevel.Structure);
    var result = prober.ProbeFormat(stream, "Gzip");
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Health, Is.Not.EqualTo(FormatHealth.Perfect));
  }

  [Test]
  public void Gzip_HeaderOnly_FailsIntegrity() {
    // Valid header but garbage payload
    byte[] bad = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03,
      0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    using var stream = new MemoryStream(bad);
    var prober = new FormatProber();
    var result = prober.ProbeFormat(stream, "Gzip");
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Health, Is.EqualTo(FormatHealth.Damaged));
    Assert.That(result.Issues, Has.Some.Matches<ValidationIssue>(i => i.Severity == IssueSeverity.Error));
  }

  // ── Zip ────────────────────────────────────────────────────────

  [Test]
  public void Zip_ValidFile_ProbesWithHighConfidence() {
    var data = CreateZip("hello.txt", [72, 101, 108, 108, 111]);
    using var stream = new MemoryStream(data);
    var prober = new FormatProber();
    var result = prober.ProbeFormat(stream, "Zip");
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Health, Is.EqualTo(FormatHealth.Perfect));
    Assert.That(result.Confidence, Is.GreaterThanOrEqualTo(0.95));
    Assert.That(result.HighestLevel, Is.EqualTo(ValidationLevel.Integrity));
    Assert.That(result.ValidEntries, Is.EqualTo(1));
    Assert.That(result.TotalEntries, Is.EqualTo(1));
  }

  [Test]
  public void Zip_HeaderValidation_ChecksVersionAndMethod() {
    var data = CreateZip("test.txt", [1, 2, 3]);
    var desc = FormatRegistry.GetById("Zip") as IFormatValidator;
    Assert.That(desc, Is.Not.Null);
    var result = desc!.ValidateHeader(data, data.Length);
    Assert.That(result.IsValid, Is.True);
    Assert.That(result.Confidence, Is.GreaterThanOrEqualTo(0.85));
  }

  [Test]
  public void Zip_StructureValidation_ParsesCentralDirectory() {
    var data = CreateZip("test.txt", [1, 2, 3]);
    using var stream = new MemoryStream(data);
    var desc = FormatRegistry.GetById("Zip") as IFormatValidator;
    Assert.That(desc, Is.Not.Null);
    var result = desc!.ValidateStructure(stream);
    Assert.That(result.IsValid, Is.True);
    Assert.That(result.ValidEntries, Is.EqualTo(1));
  }

  // ── 7z ─────────────────────────────────────────────────────────

  [Test]
  public void SevenZip_ValidFile_ProbesWithHighConfidence() {
    var data = CreateSevenZip("test.txt", [1, 2, 3, 4, 5]);
    using var stream = new MemoryStream(data);
    var prober = new FormatProber();
    var result = prober.ProbeFormat(stream, "SevenZip");
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Health, Is.EqualTo(FormatHealth.Perfect));
    Assert.That(result.Confidence, Is.GreaterThanOrEqualTo(0.95));
    Assert.That(result.HighestLevel, Is.EqualTo(ValidationLevel.Integrity));
  }

  [Test]
  public void SevenZip_HeaderValidation_VerifiesStartCrc() {
    var data = CreateSevenZip("test.txt", [1, 2, 3]);
    var desc = FormatRegistry.GetById("SevenZip") as IFormatValidator;
    Assert.That(desc, Is.Not.Null);
    var result = desc!.ValidateHeader(data, data.Length);
    Assert.That(result.IsValid, Is.True);
    Assert.That(result.Confidence, Is.GreaterThanOrEqualTo(0.90));
  }

  [Test]
  public void SevenZip_CorruptStartCrc_FailsHeaderValidation() {
    var data = CreateSevenZip("test.txt", [1, 2, 3]);
    data[8] ^= 0xFF; // Corrupt start header CRC
    var desc = FormatRegistry.GetById("SevenZip") as IFormatValidator;
    Assert.That(desc, Is.Not.Null);
    var result = desc!.ValidateHeader(data, data.Length);
    Assert.That(result.IsValid, Is.False);
    Assert.That(result.Issues, Has.Some.Matches<ValidationIssue>(i => i.Code == "7Z_START_HEADER_CRC"));
  }

  // ── Bzip2 ──────────────────────────────────────────────────────

  [Test]
  public void Bzip2_ValidFile_ProbesWithHighConfidence() {
    var data = CreateBzip2([1, 2, 3, 4, 5]);
    using var stream = new MemoryStream(data);
    var prober = new FormatProber();
    var result = prober.ProbeFormat(stream, "Bzip2");
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Health, Is.EqualTo(FormatHealth.Perfect));
    Assert.That(result.Confidence, Is.GreaterThanOrEqualTo(0.95));
  }

  [Test]
  public void Bzip2_HeaderValidation_ChecksBlockSize() {
    var data = CreateBzip2([1, 2, 3]);
    var desc = FormatRegistry.GetById("Bzip2") as IFormatValidator;
    Assert.That(desc, Is.Not.Null);
    var result = desc!.ValidateHeader(data, data.Length);
    Assert.That(result.IsValid, Is.True);
    Assert.That(result.Health, Is.EqualTo(FormatHealth.Good));
  }

  [Test]
  public void Bzip2_BadBlockSize_FailsHeader() {
    byte[] bad = [(byte)'B', (byte)'Z', (byte)'h', (byte)'0', 0x31, 0x41, 0x59, 0x26, 0x53, 0x59];
    var desc = FormatRegistry.GetById("Bzip2") as IFormatValidator;
    Assert.That(desc, Is.Not.Null);
    var result = desc!.ValidateHeader(bad, bad.Length);
    Assert.That(result.IsValid, Is.False);
    Assert.That(result.Issues, Has.Some.Matches<ValidationIssue>(i => i.Code == "BZ2_BAD_BLOCKSIZE"));
  }

  // ── Tar ────────────────────────────────────────────────────────

  [Test]
  public void Tar_ValidFile_ProbesWithHighConfidence() {
    var data = CreateTar("test.txt", [1, 2, 3, 4, 5]);
    using var stream = new MemoryStream(data);
    var prober = new FormatProber();
    var result = prober.ProbeFormat(stream, "Tar");
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Health, Is.EqualTo(FormatHealth.Perfect));
    Assert.That(result.Confidence, Is.GreaterThanOrEqualTo(0.95));
  }

  [Test]
  public void Tar_HeaderValidation_VerifiesChecksum() {
    var data = CreateTar("test.txt", [1, 2, 3]);
    var desc = FormatRegistry.GetById("Tar") as IFormatValidator;
    Assert.That(desc, Is.Not.Null);
    var result = desc!.ValidateHeader(data, data.Length);
    Assert.That(result.IsValid, Is.True);
    Assert.That(result.Health, Is.EqualTo(FormatHealth.Good));
  }

  [Test]
  public void Tar_CorruptChecksum_FailsHeader() {
    var data = CreateTar("test.txt", [1, 2, 3]);
    data[0] ^= 0xFF; // Corrupt name field, breaks checksum
    var desc = FormatRegistry.GetById("Tar") as IFormatValidator;
    Assert.That(desc, Is.Not.Null);
    var result = desc!.ValidateHeader(data, data.Length);
    Assert.That(result.IsValid, Is.False);
    Assert.That(result.Issues, Has.Some.Matches<ValidationIssue>(i => i.Code == "TAR_BAD_CHECKSUM"));
  }

  // ── Span-based probing ────────────────────────────────────────

  [Test]
  public void Probe_SpanBased_RunsHeaderValidation() {
    var gzData = CreateGzip([1, 2, 3]);
    var scans = new List<ScanResult> {
      new(0, "Gzip", 0.80, 2, "1F 8B")
    };
    var prober = new FormatProber(ValidationLevel.Header);
    var results = prober.Probe(gzData, scans);
    Assert.That(results, Has.Count.EqualTo(1));
    Assert.That(results[0].HighestLevel, Is.EqualTo(ValidationLevel.Header));
    Assert.That(results[0].Confidence, Is.GreaterThan(0.80));
  }

  [Test]
  public void Probe_UnknownFormat_ReturnsPassThrough() {
    var scans = new List<ScanResult> {
      new(0, "SomeUnknown", 0.5, 2, "FF FF")
    };
    var prober = new FormatProber();
    var results = prober.Probe(new byte[100], scans);
    Assert.That(results, Has.Count.EqualTo(1));
    Assert.That(results[0].Health, Is.EqualTo(FormatHealth.Unknown));
    Assert.That(results[0].HighestLevel, Is.EqualTo(ValidationLevel.Magic));
  }

  [Test]
  public void Probe_NoValidator_ReturnsNull() {
    var prober = new FormatProber();
    var result = prober.ProbeFormat(new MemoryStream([0]), "SomeUnknown");
    Assert.That(result, Is.Null);
  }

  // ── Helpers ────────────────────────────────────────────────────

  private static byte[] CreateGzip(byte[] input) {
    using var ms = new MemoryStream();
    using (var gz = new FileFormat.Gzip.GzipStream(ms,
      Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true))
      gz.Write(input);
    return ms.ToArray();
  }

  private static byte[] CreateZip(string name, byte[] content) {
    using var ms = new MemoryStream();
    var writer = new FileFormat.Zip.ZipWriter(ms);
    writer.AddEntry(name, content);
    writer.Finish();
    return ms.ToArray();
  }

  private static byte[] CreateSevenZip(string name, byte[] content) {
    using var ms = new MemoryStream();
    var writer = new FileFormat.SevenZip.SevenZipWriter(ms);
    writer.AddEntry(new FileFormat.SevenZip.SevenZipEntry { Name = name }, content);
    writer.Finish();
    return ms.ToArray();
  }

  private static byte[] CreateBzip2(byte[] input) {
    using var ms = new MemoryStream();
    using (var bz = new FileFormat.Bzip2.Bzip2Stream(ms,
      Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true))
      bz.Write(input);
    return ms.ToArray();
  }

  private static byte[] CreateTar(string name, byte[] content) {
    using var ms = new MemoryStream();
    var writer = new FileFormat.Tar.TarWriter(ms);
    writer.AddEntry(new FileFormat.Tar.TarEntry { Name = name, Size = content.Length }, content);
    writer.Finish();
    return ms.ToArray();
  }
}
