#pragma warning disable CS1591
using Compression.Lib;

namespace Compression.Tests.Operations;

/// <summary>
/// The <c>test</c> verb answers "is this archive intact?". A single bool made it answer "no" to
/// two very different files: one that is damaged, and one that is perfectly healthy but in a
/// format with no verifier. The second is not a failed integrity check, and reporting it as one
/// tells the user their file is broken when it is not.
/// </summary>
[TestFixture]
public sealed class ArchiveTestVerbTests {

  private static string MakeTempDir() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_testverb_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static string WriteProbe(string dir) {
    var src = Path.Combine(dir, "probe.txt");
    File.WriteAllText(src, string.Concat(Enumerable.Repeat("integrity-probe-line\n", 64)));
    return src;
  }

  [Test, Category("HappyPath")]
  public void AnIntactArchivePasses() {
    var dir = MakeTempDir();
    try {
      var src = WriteProbe(dir);
      var zip = Path.Combine(dir, "good.zip");
      ArchiveOperations.Create(zip, [new ArchiveInput(src, "probe.txt")], new CompressionOptions());

      Assert.That(ArchiveOperations.TestDetailed(zip, null), Is.EqualTo(ArchiveTestResult.Ok));
      Assert.That(ArchiveOperations.Test(zip, null), Is.True);
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("Sad")]
  public void ACorruptedPayloadIsReportedCorrupt() {
    var dir = MakeTempDir();
    try {
      var src = WriteProbe(dir);
      var zip = Path.Combine(dir, "good.zip");
      ArchiveOperations.Create(zip, [new ArchiveInput(src, "probe.txt")], new CompressionOptions());

      var bytes = File.ReadAllBytes(zip);
      for (var i = 40; i < Math.Min(90, bytes.Length); ++i) bytes[i] ^= 0xFF;
      var bad = Path.Combine(dir, "bad.zip");
      File.WriteAllBytes(bad, bytes);

      Assert.That(ArchiveOperations.TestDetailed(bad, null), Is.EqualTo(ArchiveTestResult.Corrupt));
      Assert.That(ArchiveOperations.Test(bad, null), Is.False);
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("Sad")]
  public void ATruncatedArchiveIsReportedCorrupt() {
    var dir = MakeTempDir();
    try {
      var src = WriteProbe(dir);
      var tar = Path.Combine(dir, "good.tar");
      ArchiveOperations.Create(tar, [new ArchiveInput(src, "probe.txt")], new CompressionOptions());

      var trunc = Path.Combine(dir, "trunc.tar");
      File.WriteAllBytes(trunc, File.ReadAllBytes(tar).Take(1024).ToArray());

      Assert.That(ArchiveOperations.TestDetailed(trunc, null), Is.EqualTo(ArchiveTestResult.Corrupt));
    } finally { Directory.Delete(dir, true); }
  }

  /// <summary>
  /// The case the bool answer got wrong: a healthy file that is not an archive at all came back
  /// false, and the CLI printed FAILED.
  /// </summary>
  [Test, Category("EdgeCase")]
  public void AHealthyNonArchiveIsNotReportedAsCorrupt() {
    var dir = MakeTempDir();
    try {
      var src = WriteProbe(dir);
      Assert.That(ArchiveOperations.TestDetailed(src, null), Is.EqualTo(ArchiveTestResult.NotTestable),
        "a plain text file is not a damaged archive");
    } finally { Directory.Delete(dir, true); }
  }

  /// <summary>
  /// An empty file whose extension nothing claims. Note the deliberate lack of a <c>.dat</c>-style
  /// extension: a format that claims the extension is entitled to call a zero-byte instance of
  /// itself malformed, so that case really is Corrupt and testing it here would prove nothing.
  /// </summary>
  [Test, Category("EdgeCase")]
  public void AnEmptyUnclaimedFileIsNotReportedAsCorrupt() {
    var dir = MakeTempDir();
    try {
      var empty = Path.Combine(dir, "empty.notaformat");
      File.WriteAllBytes(empty, []);
      Assert.That(FormatDetector.Detect(empty), Is.EqualTo(FormatDetector.Format.Unknown),
        "the probe file has to be genuinely unrecognised for this test to mean anything");
      Assert.That(ArchiveOperations.TestDetailed(empty, null), Is.EqualTo(ArchiveTestResult.NotTestable));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("HappyPath")]
  public void AStreamFormatIsVerifiedByItsOwnChecksum() {
    var dir = MakeTempDir();
    try {
      var src = WriteProbe(dir);
      var gz = Path.Combine(dir, "probe.gz");
      ArchiveOperations.Create(gz, [new ArchiveInput(src, "probe.txt")], new CompressionOptions());
      Assert.That(ArchiveOperations.TestDetailed(gz, null), Is.EqualTo(ArchiveTestResult.Ok));

      // Flip a byte well inside the deflate payload; gzip's trailing CRC-32 must catch it.
      var bytes = File.ReadAllBytes(gz);
      bytes[bytes.Length / 2] ^= 0xFF;
      var bad = Path.Combine(dir, "bad.gz");
      File.WriteAllBytes(bad, bytes);
      Assert.That(ArchiveOperations.TestDetailed(bad, null), Is.EqualTo(ArchiveTestResult.Corrupt));
    } finally { Directory.Delete(dir, true); }
  }
}
