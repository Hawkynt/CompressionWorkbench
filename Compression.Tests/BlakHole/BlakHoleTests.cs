using System.Reflection;
using System.Security.Cryptography;
using FileFormat.BlakHole;

namespace Compression.Tests.BlakHole;

/// <summary>
/// The BlakHole sample from the Archive Team corpus. Each member carries a CRC-32, which the reader
/// verifies, so a member that extracts is byte-exact. See docs/BLAKHOLE-ON-DISK.md.
/// </summary>
[TestFixture]
public class BlakHoleTests {
  private static byte[] Sample() {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BlakHoleVectors.test.bh")
      ?? throw new FileNotFoundException("Embedded BlakHole sample is missing.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_Sample_When_Listed_Then_FiveDeflateMembers() {
    var entries = BlakHoleArchive.Read(Sample(), out var truncated);
    Assert.That(truncated, Is.False);
    Assert.That(entries.Select(e => (e.Name, e.OriginalSize)), Is.EqualTo(new[] {
      ("test.doc", 26624u), ("test.docx", 10436u), ("test.odt", 4649u), ("test.pdf", 142970u), ("test.rtf", 31789u),
    }));
    Assert.That(entries.Select(e => e.Method), Is.All.EqualTo(8));
    Assert.That(entries[0].LastModified, Is.EqualTo(new DateTime(2012, 12, 1, 18, 24, 0)));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_Sample_When_Extracted_Then_EveryCrcHoldsAndContentIsTheDocument() {
    var data = Sample();
    var entries = BlakHoleArchive.Read(data, out _);
    foreach (var e in entries)
      Assert.That(BlakHoleArchive.Extract(data, e), Has.Length.EqualTo(e.OriginalSize), e.Name);
    var doc = BlakHoleArchive.Extract(data, entries[0]);
    Assert.That(doc.AsSpan(0, 8).ToArray(), Is.EqualTo(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }), "an OLE2 .doc");
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(BlakHoleArchive.Extract(data, entries[1]))),
      Is.EqualTo("cf4cde90d8c35e8008a3a84c69ab408104f23f8f9b992f909736aba221f60390"));
  }

  [Test, Category("EdgeCase")]
  public void Given_FlippedDataByte_When_Extracted_Then_Refused() {
    var data = Sample();
    var entries = BlakHoleArchive.Read(data, out _);
    data[entries[2].DataOffset + 100] ^= 0x20;
    Assert.Catch(() => BlakHoleArchive.Extract(data, entries[2]));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnknownConstantChanged_When_Read_Then_RecordRefused() {
    var data = Sample();
    data[5591 + 7] ^= 1; // the unexplained constant of the second record
    var entries = BlakHoleArchive.Read(data, out var truncated);
    Assert.That(truncated, Is.True);
    Assert.That(entries, Has.Count.EqualTo(1));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnobservedMethod_When_Extracted_Then_NotSupported() {
    var data = Sample();
    data[10] = 9;
    var entry = BlakHoleArchive.Read(data, out _)[0];
    Assert.Throws<NotSupportedException>(() => BlakHoleArchive.Extract(data, entry));
  }

  [Test, Category("EdgeCase")]
  public void Given_NotBlakHole_When_Read_Then_InvalidData() =>
    Assert.Throws<InvalidDataException>(() => BlakHoleArchive.Read(new byte[64], out _));
}
