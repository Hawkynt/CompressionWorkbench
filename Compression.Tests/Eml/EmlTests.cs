using System.Text;
using FileFormat.Eml;

namespace Compression.Tests.Eml;

[TestFixture]
public class EmlTests {

  // ── Fixtures ─────────────────────────────────────────────────────────────

  private static byte[] SimpleTextMessage() => Encoding.ASCII.GetBytes(
    "From: alice@example.org\r\n" +
    "To: bob@example.net\r\n" +
    "Subject: Hello\r\n" +
    "Date: Mon, 01 Jan 2024 00:00:00 +0000\r\n" +
    "Message-ID: <abc123@example.org>\r\n" +
    "Content-Type: text/plain; charset=utf-8\r\n" +
    "\r\n" +
    "Hello, EML world.\r\n");

  private static byte[] MultipartMessage() => Encoding.ASCII.GetBytes(
    "From: alice@example.org\r\n" +
    "Subject: Multipart\r\n" +
    "Content-Type: multipart/mixed; boundary=\"XXX\"\r\n" +
    "\r\n" +
    "--XXX\r\n" +
    "Content-Type: text/plain\r\n" +
    "\r\n" +
    "Intro body.\r\n" +
    "--XXX\r\n" +
    "Content-Type: application/octet-stream\r\n" +
    "Content-Disposition: attachment; filename=\"data.bin\"\r\n" +
    "Content-Transfer-Encoding: base64\r\n" +
    "\r\n" +
    Convert.ToBase64String([1, 2, 3, 4, 5]) + "\r\n" +
    "--XXX--\r\n");

  // ── Tests ────────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void List_SimpleMessage_EmitsFullAndMetadataAndOnePart() {
    using var ms = new MemoryStream(SimpleTextMessage());
    var desc = new EmlFormatDescriptor();
    var entries = desc.List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.eml"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("part_00_")), Is.True);
  }

  [Category("HappyPath"), Category("RoundTrip")]
  [Test]
  public void Extract_Multipart_DecodesAttachmentBase64() {
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(MultipartMessage());
      var desc = new EmlFormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      var attachPath = Path.Combine(tmp, "attachments", "data.bin");
      Assert.That(File.Exists(attachPath), Is.True);
      var bytes = File.ReadAllBytes(attachPath);
      Assert.That(bytes, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void Metadata_IncludesFromToSubject() {
    using var ms = new MemoryStream(SimpleTextMessage());
    var desc = new EmlFormatDescriptor();
    var outStream = new MemoryStream();
    ((Compression.Registry.IArchiveInMemoryExtract)desc).ExtractEntry(ms, "metadata.ini", outStream, null);
    var text = Encoding.UTF8.GetString(outStream.ToArray());
    Assert.That(text, Does.Contain("From = alice@example.org"));
    Assert.That(text, Does.Contain("Subject = Hello"));
    Assert.That(text, Does.Contain("Message-ID = <abc123@example.org>"));
  }
}
