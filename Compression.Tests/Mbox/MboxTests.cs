using System.Text;
using FileFormat.Mbox;

namespace Compression.Tests.Mbox;

[TestFixture]
public class MboxTests {

  private static byte[] BuildMessage(string fromLine, string subject, string body) {
    var sb = new StringBuilder();
    sb.Append("From ").Append(fromLine).Append('\n');
    sb.Append("From: sender@example.org\n");
    sb.Append("To: recipient@example.net\n");
    sb.Append("Subject: ").Append(subject).Append('\n');
    sb.Append("Date: Mon, 01 Jan 2024 00:00:00 +0000\n");
    sb.Append('\n');
    sb.Append(body);
    if (!body.EndsWith('\n')) sb.Append('\n');
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  [Category("HappyPath")]
  [Test]
  public void List_ReturnsOneEntryPerMessage() {
    var mbox = ConcatBytes(
      BuildMessage("alice@x.net Mon Jan  1 00:00:00 2024", "First", "Body one\n"),
      BuildMessage("bob@x.net Mon Jan  1 00:01:00 2024",   "Second", "Body two\n"),
      BuildMessage("carol@x.net Mon Jan  1 00:02:00 2024", "Third",  "Body three\n"));

    using var stream = new MemoryStream(mbox);
    var desc = new MboxFormatDescriptor();
    var entries = desc.List(stream, null);

    Assert.That(entries, Has.Count.EqualTo(3));
    Assert.That(entries[0].Name, Does.Contain("First"));
    Assert.That(entries[1].Name, Does.Contain("Second"));
    Assert.That(entries[2].Name, Does.Contain("Third"));
    Assert.That(entries.All(e => e.Name.EndsWith(".eml")), Is.True);
  }

  [Category("HappyPath"), Category("RoundTrip")]
  [Test]
  public void Extract_WritesOneEmlPerMessage() {
    var mbox = ConcatBytes(
      BuildMessage("a@x.net Mon Jan  1 00:00:00 2024", "Hello", "Hi there.\n"),
      BuildMessage("b@x.net Mon Jan  1 00:01:00 2024", "World", "Goodbye.\n"));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var stream = new MemoryStream(mbox);
      var desc = new MboxFormatDescriptor();
      desc.Extract(stream, tmp, null, null);

      var files = Directory.GetFiles(tmp, "*.eml");
      Assert.That(files.Length, Is.EqualTo(2));
      var text0 = File.ReadAllText(files[0]);
      Assert.That(text0, Does.Contain("Subject:"));
      Assert.That(text0, Does.Not.StartWith("From ")); // separator stripped
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_MagicAndExtensions() {
    var d = new MboxFormatDescriptor();
    Assert.That(d.Extensions, Does.Contain(".mbox"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(Encoding.ASCII.GetString(d.MagicSignatures[0].Bytes), Is.EqualTo("From "));
  }

  private static byte[] ConcatBytes(params byte[][] chunks) {
    var ms = new MemoryStream();
    foreach (var c in chunks) ms.Write(c);
    return ms.ToArray();
  }
}
