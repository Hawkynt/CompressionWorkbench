using System.Buffers.Binary;
using System.Diagnostics;
using Compression.Analysis.ExternalTools;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>
/// PKWARE Strong Encryption (password-based AES). Oracle: 7-Zip, whose ZipStrong decoder must
/// extract what we write, and must report a wrong password as such.
/// </summary>
[TestFixture]
public class ZipStrongEncryptionTests {

  private const string Password = "strong-secret";
  private static readonly byte[] Text = "PKWARE strong encryption, read back by 7-Zip\n"u8.ToArray();
  private static readonly byte[] Pattern = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 % 251))];

  private static MemoryStream Write(params (string Name, byte[] Data, ZipCompressionMethod Method)[] files) {
    var ms = new MemoryStream();
    using (var w = new ZipWriter(ms, leaveOpen: true, password: Password, encryptionMethod: ZipEncryptionMethod.PkwareStrongAes256))
      foreach (var (name, data, method) in files)
        w.AddEntry(name, data, method);
    ms.Position = 0;
    return ms;
  }

  [TestCase(ZipCompressionMethod.Store)]
  [TestCase(ZipCompressionMethod.Deflate)]
  [Category("RoundTrip")]
  public void Given_StrongEncryptedEntry_When_ReadWithThePassword_Then_ContentMatches(ZipCompressionMethod method) {
    using var ms = Write(("a.txt", Text, method), ("b.bin", Pattern, method));
    using var r = new ZipReader(ms, password: Password);
    Assert.That(r.Entries.All(e => e.IsEncrypted), Is.True);
    Assert.That(r.ExtractEntry(r.Entries[0]), Is.EqualTo(Text));
    Assert.That(r.ExtractEntry(r.Entries[1]), Is.EqualTo(Pattern));
  }

  [TestCase(0)]
  [TestCase(15)]
  [TestCase(16)]
  [TestCase(17)]
  [Category("Boundary")]
  public void Given_PayloadAroundTheAesBlockSize_When_RoundTripped_Then_PaddingIsStrippedExactly(int length) {
    var data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
    using var ms = Write(("x", data, ZipCompressionMethod.Store));
    using var r = new ZipReader(ms, password: Password);
    Assert.That(r.ExtractEntry(r.Entries[0]), Is.EqualTo(data));
  }

  [TestCase((ushort)0x660E)]
  [TestCase((ushort)0x660F)]
  [TestCase((ushort)0x6610)]
  [Category("RoundTrip")]
  public void Given_EachAesKeySize_When_PayloadRoundTrips_Then_DataComesBack(ushort algId) {
    var payload = ZipStrongEncryption.Encrypt(Pattern, Password, algId);
    Assert.That(ZipStrongEncryption.Decrypt(payload, Password, 0, Pattern.Length), Is.EqualTo(Pattern));
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongPassword_When_Extracted_Then_InvalidData() {
    using var ms = Write(("a.txt", Text, ZipCompressionMethod.Deflate));
    using var r = new ZipReader(ms, password: "Strong-secret");
    Assert.Throws<InvalidDataException>(() => r.ExtractEntry(r.Entries[0]));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoPassword_When_Extracted_Then_Refused() {
    using var ms = Write(("a.txt", Text, ZipCompressionMethod.Deflate));
    using var r = new ZipReader(ms, password: null);
    Assert.Throws<InvalidOperationException>(() => r.ExtractEntry(r.Entries[0]));
  }

  [Test, Category("EdgeCase")]
  public void Given_ThreeDesAlgorithm_When_Decrypted_Then_NotSupported() {
    var payload = ZipStrongEncryption.Encrypt(Text, Password);
    // AlgID sits after IVSize(2) + IV(16) + Size(4) + Format(2)
    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(24), 0x6603);
    Assert.Throws<NotSupportedException>(() => ZipStrongEncryption.Decrypt(payload, Password, 0, Text.Length));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedCiphertext_When_Decrypted_Then_InvalidData() {
    var payload = ZipStrongEncryption.Encrypt(Text, Password);
    Assert.Throws<InvalidDataException>(() => ZipStrongEncryption.Decrypt(payload.AsSpan(0, payload.Length - 5), Password, 0, Text.Length));
    Assert.Throws<InvalidDataException>(() => ZipStrongEncryption.Decrypt(payload.AsSpan(0, 4), Password, 0, Text.Length));
  }

  [Test, Category("HappyPath")]
  public void Given_StrongEncryptedArchive_When_HeadersInspected_Then_Bit6AndThe0x0017FieldAreSet() {
    using var ms = Write(("a.txt", Text, ZipCompressionMethod.Store));
    var bytes = ms.ToArray();
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
    Assert.That(flags & 0x41, Is.EqualTo(0x41));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)), Is.GreaterThanOrEqualTo(51));
    using var r = new ZipReader(new MemoryStream(bytes), password: Password);
    Assert.That(r.Entries[0].ExtraField, Is.Not.Null);
  }

  [TestCase(ZipCompressionMethod.Store)]
  [TestCase(ZipCompressionMethod.Deflate)]
  [Category("ExternalTool")]
  public void Given_OurStrongEncryptedArchive_When_SevenZipExtractsIt_Then_ContentMatches(ZipCompressionMethod method) {
    var sevenZip = ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
    if (sevenZip == null) Assert.Ignore("7-Zip not found.");
    var dir = Path.Combine(Path.GetTempPath(), "zipstrong_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "ours.zip");
      using (var ms = Write(("a.txt", Text, method), ("b.bin", Pattern, method)))
        File.WriteAllBytes(path, ms.ToArray());
      var (code, output) = Run(sevenZip, "x", "-y", "-p" + Password, "-o" + Path.Combine(dir, "out"), path);
      Assert.That(code, Is.Zero, output);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "out", "a.txt")), Is.EqualTo(Text));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "out", "b.bin")), Is.EqualTo(Pattern));
      var (_, listing) = Run(sevenZip, "l", "-slt", "-p" + Password, path);
      Assert.That(listing, Does.Contain("pkAES-256"));
      var (badCode, badOutput) = Run(sevenZip, "t", "-pwrong", path);
      Assert.That(badCode, Is.Not.Zero);
      Assert.That(badOutput, Does.Contain("Wrong password"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  private static (int Code, string Output) Run(string exe, params string[] args) {
    var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, output);
  }
}
