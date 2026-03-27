using System.Text;
using FileFormat.Warc;

namespace Compression.Tests.Warc;

[TestFixture]
public class WarcTests {
  // ── Builder helpers ──────────────────────────────────────────────────────

  private static byte[] BuildRecord(string type, string recordId, string? targetUri,
      string? date, byte[] payload) {
    var sb = new StringBuilder();
    sb.Append("WARC/1.1\r\n");
    sb.Append($"WARC-Type: {type}\r\n");
    sb.Append($"WARC-Record-ID: {recordId}\r\n");
    if (targetUri != null) sb.Append($"WARC-Target-URI: {targetUri}\r\n");
    if (date != null) sb.Append($"WARC-Date: {date}\r\n");
    sb.Append($"Content-Length: {payload.Length}\r\n");
    sb.Append("\r\n");
    var header = Encoding.ASCII.GetBytes(sb.ToString());
    var ms = new MemoryStream();
    ms.Write(header);
    ms.Write(payload);
    ms.Write("\r\n\r\n"u8);
    return ms.ToArray();
  }

  private static byte[] ConcatRecords(params byte[][] records) {
    var ms = new MemoryStream();
    foreach (var r in records)
      ms.Write(r);
    return ms.ToArray();
  }

  private static List<(WarcEntry Entry, byte[] Payload)> ReadAll(byte[] data) {
    using var ms = new MemoryStream(data);
    using var r = new WarcReader(ms);
    return r.ReadAll();
  }

  // ── Magic / version line ─────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Magic_StartsWithWarcSlash() {
    var record = BuildRecord("warcinfo", "<urn:uuid:1>", null, null, []);
    Assert.That(Encoding.ASCII.GetString(record, 0, 5), Is.EqualTo("WARC/"));
  }

  [Category("HappyPath")]
  [Test]
  public void VersionLine_10_IsAccepted() {
    var sb = new StringBuilder();
    sb.Append("WARC/1.0\r\n");
    sb.Append("WARC-Type: warcinfo\r\n");
    sb.Append("WARC-Record-ID: <urn:uuid:v10>\r\n");
    sb.Append("Content-Length: 0\r\n");
    sb.Append("\r\n");
    sb.Append("\r\n\r\n");
    var data = Encoding.ASCII.GetBytes(sb.ToString());

    var records = ReadAll(data);
    Assert.That(records, Has.Count.EqualTo(1));
    Assert.That(records[0].Entry.RecordId, Is.EqualTo("<urn:uuid:v10>"));
  }

  // ── Single record ────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void SingleRecord_HeadersParsed() {
    var payload = "Hello, WARC!"u8.ToArray();
    var data = BuildRecord("response", "<urn:uuid:abc>", "https://example.com/", "2024-01-15T12:00:00Z", payload);

    var records = ReadAll(data);
    Assert.That(records, Has.Count.EqualTo(1));

    var entry = records[0].Entry;
    Assert.That(entry.Type, Is.EqualTo("response"));
    Assert.That(entry.RecordId, Is.EqualTo("<urn:uuid:abc>"));
    Assert.That(entry.TargetUri, Is.EqualTo("https://example.com/"));
    Assert.That(entry.Date, Is.EqualTo("2024-01-15T12:00:00Z"));
    Assert.That(entry.ContentLength, Is.EqualTo(payload.Length));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void SingleRecord_PayloadPreserved() {
    var payload = "Hello, WARC!"u8.ToArray();
    var data = BuildRecord("resource", "<urn:uuid:pay>", "https://example.com/file.html", null, payload);

    var records = ReadAll(data);
    Assert.That(records[0].Payload, Is.EqualTo(payload));
  }

  [Category("EdgeCase")]
  [Test]
  public void SingleRecord_EmptyPayload() {
    var data = BuildRecord("warcinfo", "<urn:uuid:empty>", null, null, []);
    var records = ReadAll(data);

    Assert.That(records, Has.Count.EqualTo(1));
    Assert.That(records[0].Entry.ContentLength, Is.EqualTo(0));
    Assert.That(records[0].Payload, Is.Empty);
  }

  // ── Multiple records ─────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleRecords_AllParsed() {
    var p1 = "First payload"u8.ToArray();
    var p2 = "Second payload"u8.ToArray();
    var p3 = "Third payload"u8.ToArray();

    var data = ConcatRecords(
      BuildRecord("warcinfo",  "<urn:uuid:1>", null,                    "2024-01-01T00:00:00Z", p1),
      BuildRecord("response",  "<urn:uuid:2>", "https://a.example/",    "2024-01-01T00:00:01Z", p2),
      BuildRecord("resource",  "<urn:uuid:3>", "https://b.example/img", "2024-01-01T00:00:02Z", p3));

    var records = ReadAll(data);
    Assert.That(records, Has.Count.EqualTo(3));

    Assert.That(records[0].Entry.Type, Is.EqualTo("warcinfo"));
    Assert.That(records[0].Payload, Is.EqualTo(p1));

    Assert.That(records[1].Entry.Type, Is.EqualTo("response"));
    Assert.That(records[1].Entry.TargetUri, Is.EqualTo("https://a.example/"));
    Assert.That(records[1].Payload, Is.EqualTo(p2));

    Assert.That(records[2].Entry.Type, Is.EqualTo("resource"));
    Assert.That(records[2].Payload, Is.EqualTo(p3));
  }

  [Category("HappyPath")]
  [Test]
  public void MultipleRecords_RecordIds_AreDistinct() {
    var data = ConcatRecords(
      BuildRecord("request",  "<urn:uuid:r1>", "https://x.example/", null, []),
      BuildRecord("response", "<urn:uuid:r2>", "https://x.example/", null, "body"u8.ToArray()));

    var records = ReadAll(data);
    Assert.That(records[0].Entry.RecordId, Is.EqualTo("<urn:uuid:r1>"));
    Assert.That(records[1].Entry.RecordId, Is.EqualTo("<urn:uuid:r2>"));
  }

  // ── Record types ─────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [TestCase("warcinfo")]
  [TestCase("response")]
  [TestCase("resource")]
  [TestCase("request")]
  [TestCase("metadata")]
  [TestCase("revisit")]
  [TestCase("continuation")]
  [TestCase("conversion")]
  public void RecordType_IsPreserved(string type) {
    var data = BuildRecord(type, "<urn:uuid:t>", null, null, []);
    var records = ReadAll(data);
    Assert.That(records[0].Entry.Type, Is.EqualTo(type));
  }

  // ── FormatDescriptor ─────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_List_ReturnsEntries() {
    var p = "page body"u8.ToArray();
    var data = ConcatRecords(
      BuildRecord("response", "<urn:uuid:l1>", "https://list.example/", "2024-06-01T00:00:00Z", p),
      BuildRecord("resource", "<urn:uuid:l2>", "https://list.example/img.png", null, p));

    using var ms = new MemoryStream(data);
    var desc = new WarcFormatDescriptor();
    var entries = desc.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Does.Contain("response"));
    Assert.That(entries[1].Name, Does.Contain("resource"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_List_OriginalSize_EqualsContentLength() {
    var payload = new byte[512];
    var data = BuildRecord("response", "<urn:uuid:s>", "https://size.example/", null, payload);

    using var ms = new MemoryStream(data);
    var desc = new WarcFormatDescriptor();
    var entries = desc.List(ms, null);

    Assert.That(entries[0].OriginalSize, Is.EqualTo(512));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Extract_WritesPayloads() {
    var p1 = "page content"u8.ToArray();
    var p2 = "image data"u8.ToArray();
    var data = ConcatRecords(
      BuildRecord("response", "<urn:uuid:e1>", "https://extract.example/page.html", null, p1),
      BuildRecord("resource", "<urn:uuid:e2>", "https://extract.example/img.png",   null, p2));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      var desc = new WarcFormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      var extracted = Directory.GetFiles(tmp, "*", SearchOption.AllDirectories);
      Assert.That(extracted.Length, Is.EqualTo(2));
    } finally {
      Directory.Delete(tmp, true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Extract_FallbackName_UsedWhenNoUri() {
    var data = BuildRecord("warcinfo", "<urn:uuid:noUri>", null, null, "info"u8.ToArray());

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      var desc = new WarcFormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      var extracted = Directory.GetFiles(tmp, "record-*", SearchOption.AllDirectories);
      Assert.That(extracted.Length, Is.EqualTo(1));
    } finally {
      Directory.Delete(tmp, true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_MagicSignature_MatchesWarcSlash() {
    var desc = new WarcFormatDescriptor();
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    var magic = System.Text.Encoding.ASCII.GetString(desc.MagicSignatures[0].Bytes);
    Assert.That(magic, Is.EqualTo("WARC/"));
  }

  // ── Binary payload ───────────────────────────────────────────────────────

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void BinaryPayload_IsPreservedExactly() {
    var rng = new Random(42);
    var payload = new byte[4096];
    rng.NextBytes(payload);

    var data = BuildRecord("resource", "<urn:uuid:bin>", "https://bin.example/data.bin", null, payload);
    var records = ReadAll(data);

    Assert.That(records[0].Payload, Is.EqualTo(payload));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void PayloadWithEmbeddedCrLf_IsPreservedExactly() {
    // Ensure the reader does not stop reading at \r\n sequences inside the payload
    var payload = "line1\r\nline2\r\nWARC/1.1\r\nfake header\r\n"u8.ToArray();
    var data = BuildRecord("response", "<urn:uuid:crlf>", "https://crlf.example/", null, payload);

    var records = ReadAll(data);
    Assert.That(records[0].Payload, Is.EqualTo(payload));
  }
}
