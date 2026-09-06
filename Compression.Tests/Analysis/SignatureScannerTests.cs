using Compression.Analysis.Scanning;
using Compression.Registry;

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

  [Test, Category("Regression")]
  public void Scan_OneByteMagic_FindsArc() {
    var data = new byte[64];
    data[41] = 0x1A;

    var results = SignatureScanner.Scan(data, headerProbeAlignment: 0);

    Assert.That(results.Any(result => result.FormatName == "Arc" && result.Offset == 41), Is.True,
      "One-byte signatures used to be indexed under a key the two-byte scanner could never request.");
  }

  [Test, Category("Regression")]
  public void Scan_LowConfidenceFlood_DoesNotHideLaterStrongMatch() {
    const int zipOffset = 4096;
    var data = new byte[zipOffset + 64];
    Array.Fill(data, (byte)0x1A); // ARC's one-byte magic: deliberately floods the bounded candidate set.
    data[zipOffset] = 0x50;
    data[zipOffset + 1] = 0x4B;
    data[zipOffset + 2] = 0x03;
    data[zipOffset + 3] = 0x04;

    var results = SignatureScanner.Scan(data, maxResults: 10, headerProbeAlignment: 0);

    Assert.That(results.Any(result => result.FormatName == "Zip" && result.Offset == zipOffset), Is.True,
      "Weak early signatures must not stop scanning before stronger evidence later in the same window.");
  }

  [Test, Category("Regression")]
  public void Scan_MaskedMagic_FindsAviWithVariableRiffSize() {
    var data = new byte[64];
    "RIFF"u8.CopyTo(data);
    data[4] = 0x31;
    data[5] = 0x32;
    data[6] = 0x33;
    data[7] = 0x34;
    "AVI "u8.CopyTo(data.AsSpan(8));

    var results = SignatureScanner.Scan(data, headerProbeAlignment: 0);

    Assert.That(results.Any(result => result.FormatName == "Avi" && result.Offset == 0), Is.True,
      "Masked bytes must not be compared as literal zeroes.");
  }

  [Test, Category("Regression")]
  public void Scan_OffsetMagic_ReconstructsFilesystemStart() {
    const int filesystemStart = 73;
    const int extMagicOffset = 1080;
    var data = new byte[filesystemStart + extMagicOffset + 128];
    data[filesystemStart + extMagicOffset] = 0x53;
    data[filesystemStart + extMagicOffset + 1] = 0xEF;

    var results = SignatureScanner.Scan(data, maxResults: 500, headerProbeAlignment: 0);

    Assert.That(results.Any(result => result.FormatName == "Ext" && result.Offset == filesystemStart), Is.True);
  }

  [Test, Category("Integration")]
  public void Scan_PackageOnlyImageMagic_FindsFarbfeldAtArbitraryOffset() {
    const int imageStart = 37;
    var data = new byte[256];
    "farbfeld"u8.CopyTo(data.AsSpan(imageStart));

    Assert.That(FormatRegistry.GetById("Farbfeld"), Is.Null,
      "This regression is meaningful only while Farbfeld is supplied by the image package rather than a Workbench descriptor.");

    var results = SignatureScanner.Scan(data, maxResults: 500, headerProbeAlignment: 0);

    Assert.That(results.Any(result => result.FormatName == "Farbfeld" && result.Offset == imageStart), Is.True);
    Assert.That(SignatureDatabase.GetDefaultExtension("Farbfeld"), Is.Not.EqualTo(".bin"));
  }

  [Test, Category("Integration")]
  public void SignatureDatabase_ContainsEveryRequestedForensicDomain() {
    Assert.Multiple(() => {
      Assert.That(SignatureDatabase.Entries.Any(entry => entry.Category == FormatCategory.Image), Is.True, "image");
      Assert.That(SignatureDatabase.Entries.Any(entry => entry.Category == FormatCategory.Video), Is.True, "video");
      Assert.That(SignatureDatabase.Entries.Any(entry => entry.Category == FormatCategory.Audio), Is.True, "audio");
      Assert.That(SignatureDatabase.Entries.Any(entry => entry.Category == FormatCategory.Archive), Is.True, "archive/filesystem");
      Assert.That(FormatRegistry.FilesystemFormatIds.Any(id =>
        SignatureDatabase.Entries.Any(entry => string.Equals(entry.FormatName, id, StringComparison.OrdinalIgnoreCase))), Is.True,
        "filesystem raw signatures");
    });
  }

  [Test, Category("HappyPath")]
  public void SignatureDatabase_HasEntries() {
    Assert.That(SignatureDatabase.Entries.Count, Is.GreaterThan(30));
  }
}
