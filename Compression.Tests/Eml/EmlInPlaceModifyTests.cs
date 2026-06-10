using System.Text;
using Compression.Registry;
using FileFormat.Eml;

namespace Compression.Tests.Eml;

/// <summary>
/// In-place R/W coverage for RFC 822 / MIME messages: <see cref="EmlInPlaceModifier"/>
/// against the existing multipart body. The companion read-only tests live in
/// <see cref="EmlTests"/>.
///
/// Boundaries exercised:
/// <list type="bullet">
///   <item>Add appends fresh part before closing boundary; surviving bytes preserved</item>
///   <item>Reader round-trip after Add picks up the new attachment with decoded content</item>
///   <item>Single-part messages reject Add (no multipart container to splice into)</item>
///   <item>Remove deletes the part's byte range between two boundary markers</item>
///   <item>Removed payload bytes are physically wiped — no forensic trace</item>
///   <item>Sequence: Add then Remove returns to the original attachment list</item>
///   <item>Remove accepts both "attachments/&lt;name&gt;" and bare filename forms</item>
/// </list>
/// </summary>
[TestFixture]
public class EmlInPlaceModifyTests {

  // ── Fixtures ─────────────────────────────────────────────────────────────

  private const string Boundary = "BNDRY";

  private static byte[] MultipartMessage(params (string FileName, byte[] Content)[] extraAttachments) {
    var sb = new StringBuilder();
    sb.Append("From: alice@example.org\r\n");
    sb.Append("To: bob@example.net\r\n");
    sb.Append("Subject: In-place test\r\n");
    sb.Append("Content-Type: multipart/mixed; boundary=\"").Append(Boundary).Append("\"\r\n");
    sb.Append("\r\n");
    sb.Append("--").Append(Boundary).Append("\r\n");
    sb.Append("Content-Type: text/plain\r\n");
    sb.Append("\r\n");
    sb.Append("Intro body.\r\n");
    foreach (var (name, content) in extraAttachments) {
      sb.Append("--").Append(Boundary).Append("\r\n");
      sb.Append("Content-Type: application/octet-stream; name=\"").Append(name).Append("\"\r\n");
      sb.Append("Content-Disposition: attachment; filename=\"").Append(name).Append("\"\r\n");
      sb.Append("Content-Transfer-Encoding: base64\r\n");
      sb.Append("\r\n");
      sb.Append(Convert.ToBase64String(content)).Append("\r\n");
    }
    sb.Append("--").Append(Boundary).Append("--\r\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  private static byte[] SinglePartMessage() => Encoding.ASCII.GetBytes(
    "From: alice@example.org\r\n" +
    "Subject: Plain\r\n" +
    "Content-Type: text/plain\r\n" +
    "\r\n" +
    "Just text.\r\n");

  private static MemoryStream FreshTwoPart() {
    var ms = new MemoryStream();
    ms.Write(MultipartMessage(("data.bin", [1, 2, 3, 4, 5])));
    ms.Position = 0;
    return ms;
  }

  // ── Add ──────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_AppendsAttachmentBeforeClosingBoundary() {
    using var msg = FreshTwoPart();

    var added = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
    EmlInPlaceModifier.AddAttachment(msg, "added.bin", added);

    var blob = msg.ToArray();
    // Closing delimiter still present at end.
    var asString = Encoding.ASCII.GetString(blob);
    Assert.That(asString, Does.EndWith("--" + Boundary + "--\r\n"));
    // New attachment headers present before the closing delimiter.
    var closingIdx = asString.LastIndexOf("--" + Boundary + "--", StringComparison.Ordinal);
    var beforeClosing = asString[..closingIdx];
    Assert.That(beforeClosing, Does.Contain("filename=\"added.bin\""));
    Assert.That(beforeClosing, Does.Contain("base64"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Add_NewAttachment_DecodableThroughReader() {
    using var msg = FreshTwoPart();
    var added = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11 };
    EmlInPlaceModifier.AddAttachment(msg, "added.bin", added);

    msg.Position = 0;
    var desc = new EmlFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      desc.Extract(msg, tmp, null, null);
      var path = Path.Combine(tmp, "attachments", "added.bin");
      Assert.That(File.Exists(path), Is.True);
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(added));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("RoundTrip")]
  public void Add_PreservesOriginalAttachmentBytes() {
    var originalAttachment = new byte[] { 1, 2, 3, 4, 5 };
    using var msg = FreshTwoPart();
    EmlInPlaceModifier.AddAttachment(msg, "added.bin", [9, 9, 9]);

    msg.Position = 0;
    var desc = new EmlFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      desc.Extract(msg, tmp, null, null);
      var path = Path.Combine(tmp, "attachments", "data.bin");
      Assert.That(File.Exists(path), Is.True);
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(originalAttachment),
        "original attachment payload drifted after Add");
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("RoundTrip")]
  public void Add_BytesBeforeSplicePoint_ByteIdentical() {
    using var msg = FreshTwoPart();
    var preBlob = msg.ToArray();

    EmlInPlaceModifier.AddAttachment(msg, "added.bin", [0xAA, 0xBB]);

    var postBlob = msg.ToArray();
    // Find where the closing boundary used to sit in pre-blob; the bytes before
    // it must be byte-identical in post-blob (Add inserts before that index,
    // not before earlier parts).
    var closingDelim = Encoding.ASCII.GetBytes("--" + Boundary + "--");
    var preClosing = IndexOf(preBlob, closingDelim);
    Assert.That(preClosing, Is.GreaterThan(0));
    Assert.That(postBlob.AsSpan(0, preClosing).SequenceEqual(preBlob.AsSpan(0, preClosing)),
      Is.True, "bytes before the splice point must remain byte-identical");
  }

  [Test, Category("Exceptional")]
  public void Add_SinglePartMessage_ThrowsNotSupported() {
    using var msg = new MemoryStream();
    msg.Write(SinglePartMessage());
    msg.Position = 0;
    Assert.That(
      () => EmlInPlaceModifier.AddAttachment(msg, "x.bin", [1]),
      Throws.InstanceOf<NotSupportedException>());
  }

  // ── Remove ───────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_NamedAttachment_DisappearsFromExtraction() {
    using var msg = FreshTwoPart();
    EmlInPlaceModifier.RemoveAttachment(msg, "data.bin");

    msg.Position = 0;
    var desc = new EmlFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      desc.Extract(msg, tmp, null, null);
      var path = Path.Combine(tmp, "attachments", "data.bin");
      Assert.That(File.Exists(path), Is.False);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("RoundTrip")]
  public void Remove_OtherPartContent_Preserved() {
    using var msg = FreshTwoPart();
    EmlInPlaceModifier.RemoveAttachment(msg, "data.bin");

    msg.Position = 0;
    var blob = new byte[msg.Length];
    msg.Read(blob, 0, blob.Length);
    var asText = Encoding.ASCII.GetString(blob);
    // The intro text/plain body must still be present.
    Assert.That(asText, Does.Contain("Intro body."));
    // The closing delimiter must still be present.
    Assert.That(asText, Does.EndWith("--" + Boundary + "--\r\n"));
    // The removed attachment headers must NOT survive.
    Assert.That(asText, Does.Not.Contain("filename=\"data.bin\""));
  }

  [Test, Category("Exceptional")]
  public void Remove_UnknownName_ThrowsFileNotFound() {
    using var msg = FreshTwoPart();
    Assert.That(
      () => EmlInPlaceModifier.RemoveAttachment(msg, "nope.bin"),
      Throws.InstanceOf<FileNotFoundException>());
  }

  [Test, Category("RoundTrip")]
  public void RemovedPayload_BytesWipedFromImage() {
    using var msg = FreshTwoPart();
    // The attachment content is [1,2,3,4,5] base64-encoded. The base64 form
    // appears in the pre-blob and must vanish after removal.
    var b64 = Convert.ToBase64String([1, 2, 3, 4, 5]);
    var preBlob = msg.ToArray();
    Assert.That(Encoding.ASCII.GetString(preBlob), Does.Contain(b64));

    EmlInPlaceModifier.RemoveAttachment(msg, "data.bin");

    var postBlob = msg.ToArray();
    Assert.That(Encoding.ASCII.GetString(postBlob), Does.Not.Contain(b64),
      "removed base64 payload bytes should not survive anywhere in the message");
  }

  // ── Sequence ─────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddThenRemove_RestoresOriginalAttachmentSet() {
    using var msg = FreshTwoPart();
    var preBlob = msg.ToArray();

    EmlInPlaceModifier.AddAttachment(msg, "tmp.bin", [0x42, 0x43]);
    EmlInPlaceModifier.RemoveAttachment(msg, "tmp.bin");

    // The byte content after Add+Remove won't be bit-identical to pre (we wrote
    // base64-encoded headers that then got deleted, and the deleted byte ranges
    // may include encoded line-breaks). What we DO guarantee is the original
    // attachment is still cleanly extractable.
    msg.Position = 0;
    var desc = new EmlFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      desc.Extract(msg, tmp, null, null);
      var path = Path.Combine(tmp, "attachments", "data.bin");
      Assert.That(File.Exists(path), Is.True);
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
      Assert.That(File.Exists(Path.Combine(tmp, "attachments", "tmp.bin")), Is.False);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
    // sanity: post-blob and pre-blob have the same length (Add added 16+payload+headers
    // bytes; Remove deleted exactly those bytes back).
    var postLen = msg.Length;
    Assert.That(postLen, Is.EqualTo(preBlob.Length));
  }

  // ── Descriptor wiring ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify() {
    var desc = new EmlFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(desc, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_RemoveAcceptsAttachmentsPrefix() {
    using var msg = FreshTwoPart();
    var desc = (IArchiveModifiable)new EmlFormatDescriptor();

    desc.Remove(msg, ["attachments/data.bin"]);

    msg.Position = 0;
    var d = new EmlFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      d.Extract(msg, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "attachments", "data.bin")), Is.False);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  // ── helpers ──────────────────────────────────────────────────────────────

  private static int IndexOf(byte[] haystack, byte[] needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; i++) {
      if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
    }
    return -1;
  }
}
