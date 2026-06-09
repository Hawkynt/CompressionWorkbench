using System.Text;
using Compression.Registry;
using FileFormat.Mbox;

namespace Compression.Tests.Mbox;

[TestFixture]
public class MboxWriterTests {

  private static byte[] BuildEml(string subject, string body) {
    var sb = new StringBuilder();
    sb.Append("From: a@example.org\n");
    sb.Append("To: b@example.org\n");
    sb.Append("Subject: ").Append(subject).Append('\n');
    sb.Append('\n');
    sb.Append(body);
    if (body.Length == 0 || body[^1] != '\n') sb.Append('\n');
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleMessage() {
    var eml = BuildEml("Hello", "Body one.");
    using var ms = new MemoryStream();
    using (var w = new MboxWriter(ms, leaveOpen: true))
      w.AddMessage(eml, "alice@example.org", DateTimeOffset.UnixEpoch);
    ms.Position = 0;

    var entries = MboxReader.ReadAll(ms.ToArray());
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Subject, Is.EqualTo("Hello"));
    Assert.That(Encoding.ASCII.GetString(entries[0].EmlBytes), Does.Contain("Body one."));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleMessages() {
    var emls = new[] {
      BuildEml("First", "Body one."),
      BuildEml("Second", "Body two."),
      BuildEml("Third", "Body three."),
    };
    using var ms = new MemoryStream();
    using (var w = new MboxWriter(ms, leaveOpen: true))
      foreach (var e in emls)
        w.AddMessage(e, "sender@example.org", DateTimeOffset.UnixEpoch);
    ms.Position = 0;

    var entries = MboxReader.ReadAll(ms.ToArray());
    Assert.That(entries, Has.Count.EqualTo(3));
    Assert.That(entries[0].Subject, Is.EqualTo("First"));
    Assert.That(entries[1].Subject, Is.EqualTo("Second"));
    Assert.That(entries[2].Subject, Is.EqualTo("Third"));
  }

  [Test, Category("EdgeCase")]
  public void FromLineInBody_IsByteStuffed() {
    // A body containing a literal "From " at column 0 must NOT confuse the reader.
    var eml = BuildEml("Trick", "From the desk of the CEO\nNormal body line\n");
    using var ms = new MemoryStream();
    using (var w = new MboxWriter(ms, leaveOpen: true))
      w.AddMessage(eml);
    ms.Position = 0;

    var bytes = ms.ToArray();
    var text = Encoding.ASCII.GetString(bytes);
    Assert.That(text, Does.Contain(">From the desk"));

    // Round-trip — only one message should be recovered, not two.
    var entries = MboxReader.ReadAll(bytes);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Subject, Is.EqualTo("Trick"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughList() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("01.eml", BuildEml("Hello", "Body 1\n")),
      ArchiveInputInfo.InMemory("02.eml", BuildEml("World", "Body 2\n")),
    };

    using var ms = new MemoryStream();
    var d = new MboxFormatDescriptor();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var list = d.List(ms, null);
    Assert.That(list, Has.Count.EqualTo(2));
    Assert.That(list[0].Name, Does.Contain("Hello"));
    Assert.That(list[1].Name, Does.Contain("World"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new MboxFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("EdgeCase")]
  public void EmptyMessage_StillEmitsEnvelopeLine() {
    using var ms = new MemoryStream();
    using (var w = new MboxWriter(ms, leaveOpen: true))
      w.AddMessage(ReadOnlySpan<byte>.Empty);
    ms.Position = 0;
    var text = Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(text, Does.StartWith("From "));
    Assert.That(text, Does.EndWith("\n"));
  }
}
