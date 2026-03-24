using Compression.Analysis.Scanning;

namespace Compression.Tests.Analysis;

[TestFixture]
public class SignatureScannerTests {

  [Test, Category("HappyPath")]
  public void Scan_GzipMagic_FindsGzip() {
    // Gzip header at offset 0
    var data = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    var results = SignatureScanner.Scan(data);
    Assert.That(results, Has.Count.GreaterThan(0));
    Assert.That(results[0].FormatName, Is.EqualTo("Gzip"));
    Assert.That(results[0].Offset, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Scan_ZipAtOffset_FindsCorrectOffset() {
    // Random padding + ZIP magic
    var data = new byte[32];
    new Random(42).NextBytes(data);
    data[10] = 0x50; data[11] = 0x4B; data[12] = 0x03; data[13] = 0x04;
    var results = SignatureScanner.Scan(data);
    var zip = results.FirstOrDefault(r => r.FormatName == "Zip");
    Assert.That(zip, Is.Not.Null);
    Assert.That(zip!.Offset, Is.EqualTo(10));
  }

  [Test, Category("HappyPath")]
  public void Scan_MultipleFormats_FindsAll() {
    // Gzip at 0, ZIP at 16
    var data = new byte[32];
    data[0] = 0x1F; data[1] = 0x8B; // Gzip
    data[16] = 0x50; data[17] = 0x4B; data[18] = 0x03; data[19] = 0x04; // ZIP
    var results = SignatureScanner.Scan(data);
    Assert.That(results.Any(r => r.FormatName == "Gzip"), Is.True);
    Assert.That(results.Any(r => r.FormatName == "Zip"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Scan_7zMagic_HighConfidence() {
    var data = new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x00 };
    var results = SignatureScanner.Scan(data);
    Assert.That(results, Has.Count.GreaterThan(0));
    Assert.That(results[0].FormatName, Is.EqualTo("SevenZip"));
    Assert.That(results[0].Confidence, Is.GreaterThan(0.9));
  }

  [Test, Category("EdgeCase")]
  public void Scan_EmptyData_ReturnsEmpty() {
    var results = SignatureScanner.Scan(ReadOnlySpan<byte>.Empty);
    Assert.That(results, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Scan_XzMagic_FindsXz() {
    var data = new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x00, 0x00 };
    var results = SignatureScanner.Scan(data);
    Assert.That(results.Any(r => r.FormatName == "Xz"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void SignatureDatabase_HasEntries() {
    Assert.That(SignatureDatabase.Entries.Count, Is.GreaterThan(30));
  }
}
