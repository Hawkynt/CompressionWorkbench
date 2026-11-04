using System.Reflection;
using System.Text;
using Compression.Core.Crypto;
using Compression.Registry;
using FileFormat.Pdf;

namespace Compression.Tests.Pdf;

/// <summary>
/// PDF standard security handler (RC4 40/128, AES-128, AES-256 revisions 5 and 6). Fixtures were
/// written by pypdf, which also read each back as the oracle; see ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class PdfEncryptionTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PdfVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded PDF vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly byte[] Pixels = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];
  private static readonly byte[] Attachment = "attached to an encrypted PDF\n"u8.ToArray();

  [TestCase("pypdf-rc4-40.pdf", "RC4", 40, 2)]
  [TestCase("pypdf-rc4-128.pdf", "RC4", 128, 3)]
  [TestCase("pypdf-aes-128.pdf", "AES", 128, 4)]
  [TestCase("pypdf-aes-256-r5.pdf", "AES", 256, 5)]
  [TestCase("pypdf-aes-256.pdf", "AES", 256, 6)]
  [Category("ThirdPartyVector")]
  public void Given_PypdfEncryptedPdf_When_ReadWithTheEmptyUserPassword_Then_ImageAndAttachmentDecrypt(string file, string cipher, int bits, int revision) {
    var r = new PdfReader(new MemoryStream(Vector(file)));
    Assert.That(r.Security, Is.Not.Null);
    Assert.That((r.Security!.Cipher, r.Security.KeyBits, r.Security.Revision), Is.EqualTo((cipher, bits, revision)));
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "image_5.raw", "hello.txt" }));
    Assert.That(r.PageEntries, Is.Empty, "page slices of an encrypted PDF would carry ciphertext");
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "image_5.raw")), Is.EqualTo(Pixels));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "hello.txt")), Is.EqualTo(Attachment));
  }

  [TestCase("pypdf-rc4-128.pdf")]
  [TestCase("pypdf-aes-128.pdf")]
  [TestCase("pypdf-aes-256.pdf")]
  [Category("ThirdPartyVector")]
  public void Given_OwnerPassword_When_Read_Then_ItOpensToo(string file) {
    var r = new PdfReader(new MemoryStream(Vector(file)), password: "owner");
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "hello.txt")), Is.EqualTo(Attachment));
  }

  [TestCase(null)]
  [TestCase("User")]
  [Category("EdgeCase")]
  public void Given_UserPasswordProtectedPdf_When_OpenedWithoutTheRightPassword_Then_Refused(string? password) {
    Assert.Throws<UnauthorizedAccessException>(() => _ = new PdfReader(new MemoryStream(Vector("pypdf-rc4-128-userpw.pdf")), password: password));
  }

  [TestCase("user")]
  [TestCase("owner")]
  [Category("ThirdPartyVector")]
  public void Given_UserPasswordProtectedPdf_When_OpenedThroughTheDescriptor_Then_Extracts(string password) {
    var d = new PdfFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "pdfenc_" + Guid.NewGuid().ToString("N"));
    try {
      d.Extract(new MemoryStream(Vector("pypdf-rc4-128-userpw.pdf")), dir, password, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "hello.txt")), Is.EqualTo(Attachment));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "image_5.raw")), Is.EqualTo(Pixels));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Given_EncryptedPdf_When_AnAttachmentIsAdded_Then_RefusedAndUntouched() {
    var original = Vector("pypdf-aes-128.pdf");
    using var ms = new MemoryStream();
    ms.Write(original);
    Assert.Throws<NotSupportedException>(() =>
      ((IArchiveModifiable)new PdfFormatDescriptor()).Add(ms, [ArchiveInputInfo.InMemory("x.txt", [1, 2, 3])]));
    Assert.That(ms.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase")]
  public void Given_NonStandardSecurityHandler_When_Opened_Then_NotSupported() {
    var text = Encoding.Latin1.GetString(Vector("pypdf-rc4-128.pdf")).Replace("/Filter /Standard", "/Filter /PubSecXY");
    Assert.Throws<NotSupportedException>(() => _ = new PdfReader(new MemoryStream(Encoding.Latin1.GetBytes(text))));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnencryptedPdf_When_Read_Then_NoSecurityHandler() {
    using var ms = new MemoryStream();
    var w = new PdfWriter();
    w.AddFile("a.txt", "plain"u8.ToArray());
    w.WriteTo(ms);
    ms.Position = 0;
    Assert.That(new PdfReader(ms).Security, Is.Null);
  }

  // RFC 6229 §2, offset 0 of the 40-, 128- and 256-bit keys 0x0102...; also produced by pyca/cryptography's ARC4.
  [TestCase("0102030405", "b2396305f03dc027ccc3524a0a1118a8")]
  [TestCase("0102030405060708090a0b0c0d0e0f10", "9ac7cc9a609d1ef7b2932899cde41b97")]
  [TestCase("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20", "eaa6bd25880bf93d3f5d1e4ca2611d91")]
  [Category("ThirdPartyVector")]
  public void Given_Rfc6229Key_When_KeystreamGenerated_Then_MatchesTheVector(string key, string keystream) {
    var output = Rc4.Apply(Convert.FromHexString(key), new byte[16]);
    Assert.That(Convert.ToHexStringLower(output), Is.EqualTo(keystream));
  }

  [Test, Category("Boundary")]
  public void Given_InvalidRc4KeyLength_When_Keyed_Then_Refused() {
    Assert.Throws<ArgumentException>(() => _ = new Rc4([]));
    Assert.Throws<ArgumentException>(() => _ = new Rc4(new byte[257]));
    Assert.DoesNotThrow(() => _ = new Rc4(new byte[256]));
  }
}
