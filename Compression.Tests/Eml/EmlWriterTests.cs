#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Eml;

namespace Compression.Tests.Eml;

[TestFixture]
public class EmlWriterTests {

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new EmlFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d is IArchiveCreatable, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Write_SingleAttachment_IsMultipartMixed() {
    using var ms = new MemoryStream();
    EmlWriter.Write(ms, [
      ("first.txt", Encoding.UTF8.GetBytes("hello"), "text/plain; charset=utf-8"),
      ("second.bin", new byte[] { 1, 2, 3, 4 }, "application/octet-stream"),
    ]);
    var text = Encoding.UTF8.GetString(ms.ToArray());
    Assert.That(text, Does.Contain("Content-Type: multipart/mixed; boundary="));
    Assert.That(text, Does.Contain("MIME-Version: 1.0"));
    Assert.That(text, Does.Contain("Content-Disposition: attachment; filename=\"first.txt\""));
    Assert.That(text, Does.Contain("Content-Disposition: attachment; filename=\"second.bin\""));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_TwoAttachments_DecodeBackThroughParser() {
    var payload1 = "alpha-content"u8.ToArray();
    var payload2 = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0xFF };
    using var ms = new MemoryStream();
    EmlWriter.Write(ms, [
      ("alpha.txt", payload1, "text/plain"),
      ("beta.bin", payload2, "application/octet-stream"),
    ]);

    var parsed = EmlParser.Parse(ms.ToArray());
    Assert.That(parsed.SubParts, Is.Not.Null);
    Assert.That(parsed.SubParts!.Count, Is.EqualTo(2));

    var leaves = parsed.SubParts.ToList();
    Assert.That(leaves[0].FileName, Is.EqualTo("alpha.txt"));
    Assert.That(leaves[0].DecodedBody, Is.EqualTo(payload1));
    Assert.That(leaves[1].FileName, Is.EqualTo("beta.bin"));
    Assert.That(leaves[1].DecodedBody, Is.EqualTo(payload2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughDescriptor() {
    var d = new EmlFormatDescriptor();
    var inputs = new[] {
      ArchiveInputInfo.InMemory("doc.txt", "Document body"u8.ToArray()),
      ArchiveInputInfo.InMemory("data.bin", new byte[] { 5, 4, 3, 2, 1 }),
    };
    using var outStream = new MemoryStream();
    d.Create(outStream, inputs, new FormatCreateOptions());

    outStream.Position = 0;
    var entries = d.List(outStream, null);
    Assert.That(entries.Any(e => e.Name == "attachments/doc.txt"), Is.True);
    Assert.That(entries.Any(e => e.Name == "attachments/data.bin"), Is.True);

    outStream.Position = 0;
    var binBytes = d.ExtractEntryToMemory(outStream, "attachments/data.bin", null);
    Assert.That(binBytes, Is.EqualTo(new byte[] { 5, 4, 3, 2, 1 }));
  }

  // Equivalence: header override via FormatCreateOptions.FormatSpecific.
  [Test, Category("HappyPath")]
  public void Create_HeaderOverride_AppearsInOutput() {
    var d = new EmlFormatDescriptor();
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["From"] = "test-sender@example.org",
        ["Subject"] = "Custom subject line",
      },
    };
    using var outStream = new MemoryStream();
    d.Create(outStream, [ArchiveInputInfo.InMemory("x.txt", "x"u8.ToArray())], options);

    var text = Encoding.UTF8.GetString(outStream.ToArray());
    Assert.That(text, Does.Contain("From: test-sender@example.org"));
    Assert.That(text, Does.Contain("Subject: Custom subject line"));
  }

  // Boundary: deterministic boundary identifier so the same inputs produce
  // byte-identical messages across runs.
  [Test, Category("Boundary")]
  public void Write_DeterministicOutput_ForSameInputs() {
    var inputs = new[] {
      ("file.txt", "fixed content"u8.ToArray(), (string?)"text/plain"),
    };
    using var ms1 = new MemoryStream();
    EmlWriter.Write(ms1, inputs);
    using var ms2 = new MemoryStream();
    EmlWriter.Write(ms2, inputs);
    Assert.That(ms1.ToArray(), Is.EqualTo(ms2.ToArray()));
  }
}
