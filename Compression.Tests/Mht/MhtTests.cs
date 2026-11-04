using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Mht;

namespace Compression.Tests.Mht;

/// <summary>
/// MHTML web archives. Third-party material: <c>chrome-blink.mhtml</c> was saved by Chromium 155,
/// <c>python-email.mht</c> built by CPython's <c>email</c> package; <c>python-email-listing.txt</c> is
/// that package's decoding of both (location, decoded size, SHA-256 per leaf part). See
/// ReferenceVectors/README.md; <see cref="MhtOracleTests"/> asks Python and Chromium about our output.
/// </summary>
[TestFixture]
public class MhtTests {

  internal static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"MhtVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded MHTML vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly MhtFormatDescriptor Descriptor = new();

  private static List<ArchiveEntryInfo> List(byte[] data) => Descriptor.List(new MemoryStream(data), null);

  private static byte[] Extract(byte[] data, string name) {
    var r = new MhtReader(data);
    return r.Extract(r.Parts.Single(p => p.Name == name));
  }

  private static byte[] Create(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [.. files.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data))], new FormatCreateOptions());
    return ms.ToArray();
  }

  [TestCase("chrome-blink.mhtml", new[] { "index.html", "img/dot.png", "style.css", "frame.html" })]
  [TestCase("python-email.mht", new[] { "part_00.txt", "site/index.html", "site/style.css", "site/img/my dot.png", "cid/dot2@example.org", "cdn.example.net_8443/lib/a.txt_v=2" })]
  [Category("ThirdPartyVector")]
  public void Given_ThirdPartyMhtml_When_Read_Then_PartsMatchPythonEmailDecoding(string vector, string[] names) {
    var expected = Encoding.ASCII.GetString(Vector("python-email-listing.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(l => l.Split(' '))
      .Where(f => f[0] == vector)
      .ToList();
    var data = Vector(vector);
    var r = new MhtReader(data);
    Assert.That(r.Parts.Select(p => p.Name), Is.EqualTo(names));
    Assert.That(r.Parts, Has.Count.EqualTo(expected.Count));
    for (var i = 0; i < expected.Count; ++i) {
      var bytes = r.Extract(r.Parts[i]);
      // CPython's quopri hands a quoted-printable hard line break back as a local "\n"; RFC 2045 §6.7
      // (and our decoder) keep it as the CRLF it encodes. Compare in Python's convention.
      if (r.Parts[i].TransferEncoding == "quoted-printable")
        bytes = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace("\r\n", "\n"));
      Assert.That(bytes.Length, Is.EqualTo(int.Parse(expected[i][2])), names[i]);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(expected[i][3]), names[i]);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_ChromeSnapshot_When_Listed_Then_MethodsAndSizesComeFromTheParts() {
    var list = List(Vector("chrome-blink.mhtml"));
    Assert.That(list.Select(e => e.Method), Is.EqualTo(new[] { "quoted-printable", "base64", "quoted-printable", "quoted-printable" }));
    Assert.That(list[1].OriginalSize, Is.EqualTo(73));
    Assert.That(Extract(Vector("chrome-blink.mhtml"), "img/dot.png")[..4], Is.EqualTo(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }));
  }

  [Test, Category("RoundTrip")]
  public void Given_PageWithResources_When_Created_Then_ReadBackWithRootFirst() {
    var css = "p{color:red}"u8.ToArray();
    var html = "<link rel=stylesheet href=css/a.css><p>ä</p>"u8.ToArray();
    var png = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
    var data = Create(("css/a.css", css), ("index.html", html), ("img/b c.png", png));
    var r = new MhtReader(data);
    Assert.That(r.Parts.Select(p => p.Name), Is.EqualTo(new[] { "index.html", "css/a.css", "img/b c.png" }));
    Assert.That(r.Parts[2].Location, Is.EqualTo("https://mhtml.invalid/img/b%20c.png"));
    Assert.That(r.Parts[0].MediaType, Is.EqualTo("text/html"));
    Assert.That(Extract(data, "index.html"), Is.EqualTo(html));
    Assert.That(Extract(data, "css/a.css"), Is.EqualTo(css));
    Assert.That(Extract(data, "img/b c.png"), Is.EqualTo(png));
  }

  [Test, Category("RoundTrip")]
  public void Given_RootOption_When_Created_Then_ThatPageComesFirst() {
    using var ms = new MemoryStream();
    var options = new FormatCreateOptions { FormatSpecific = new() { ["root"] = "b.html", ["subject"] = "Grüße" } };
    Descriptor.Create(ms, [ArchiveInputInfo.InMemory("a.html", [1]), ArchiveInputInfo.InMemory("b.html", [2])], options);
    var r = new MhtReader(ms.ToArray());
    Assert.That(r.Parts.Select(p => p.Name), Is.EqualTo(new[] { "b.html", "a.html" }));
    Assert.That(r.Headers["Subject"], Does.StartWith("=?utf-8?B?"));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnknownRootOption_When_Created_Then_Throws()
    => Assert.Throws<ArgumentException>(() => Descriptor.Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a.html", [1])],
      new FormatCreateOptions { FormatSpecific = new() { ["root"] = "missing.html" } }));

  [Test, Category("EdgeCase")]
  public void Given_NoFiles_When_Created_Then_EmptyListing()
    => Assert.That(List(Create()), Is.Empty);

  [Test, Category("EdgeCase")]
  public void Given_EmptyFile_When_Created_Then_EmptyEntry() {
    var data = Create(("index.html", []));
    Assert.That(List(data).Single().OriginalSize, Is.Zero);
  }

  [Test, Category("RoundTrip")]
  public void Given_ChromeSnapshot_When_AddedThenRemoved_Then_ByteIdenticalToTheOriginal() {
    var original = Vector("chrome-blink.mhtml");
    using var ms = new MemoryStream();
    ms.Write(original);
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("img/new.png", [1, 2, 3])]);
    var added = ms.ToArray();
    var r = new MhtReader(added);
    Assert.That(r.Parts.Select(p => p.Name), Is.EqualTo(new[] { "index.html", "img/dot.png", "style.css", "frame.html", "img/new.png" }));
    Assert.That(r.Parts[^1].Location, Is.EqualTo("http://127.0.0.1:8731/img/new.png"));
    Assert.That(r.Extract(r.Parts[^1]), Is.EqualTo(new byte[] { 1, 2, 3 }));
    Descriptor.Remove(ms, ["img/new.png"]);
    Assert.That(ms.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("RoundTrip")]
  public void Given_ChromeSnapshot_When_PartRemoved_Then_OthersDecodeUnchanged() {
    var original = Vector("chrome-blink.mhtml");
    using var ms = new MemoryStream();
    ms.Write(original);
    Descriptor.Remove(ms, ["style.css"]);
    var data = ms.ToArray();
    Assert.That(new MhtReader(data).Parts.Select(p => p.Name), Is.EqualTo(new[] { "index.html", "img/dot.png", "frame.html" }));
    foreach (var name in new[] { "index.html", "img/dot.png", "frame.html" })
      Assert.That(Extract(data, name), Is.EqualTo(Extract(original, name)), name);
  }

  [Test, Category("RoundTrip")]
  public void Given_ExistingName_When_Added_Then_PartReplaced() {
    using var ms = new MemoryStream();
    ms.Write(Create(("index.html", [1]), ("a.css", [2])));
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("a.css", [9, 9])]);
    var data = ms.ToArray();
    Assert.That(new MhtReader(data).Parts.Select(p => p.Name), Is.EqualTo(new[] { "index.html", "a.css" }));
    Assert.That(Extract(data, "a.css"), Is.EqualTo(new byte[] { 9, 9 }));
  }

  [Test, Category("EdgeCase")]
  public void Given_PartInsideNestedMultipart_When_Removed_Then_RefusedAndUntouched() {
    var original = Vector("python-email.mht");
    using var ms = new MemoryStream();
    ms.Write(original);
    Assert.Throws<NotSupportedException>(() => Descriptor.Remove(ms, ["site/index.html"]));
    Assert.That(ms.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("RoundTrip")]
  public void Given_LfDocument_When_PartAdded_Then_LfLineBreaksKept() {
    using var ms = new MemoryStream();
    ms.Write(Vector("python-email.mht"));
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("site/x.js", "1"u8.ToArray())]);
    var data = ms.ToArray();
    Assert.That(data, Does.Not.Contain((byte)'\r'));
    var r = new MhtReader(data);
    Assert.That(r.Parts[^1].Name, Is.EqualTo("site/x.js"));
    Assert.That(r.Parts[^1].Location, Is.EqualTo("http://example.org/site/x.js"));
    Assert.That(r.Extract(r.Parts[^1]), Is.EqualTo("1"u8.ToArray()));
  }

  [Test, Category("EdgeCase")]
  public void Given_SingleResourceDocument_When_Read_Then_OneEntryAndEditsRefused() {
    var doc = "MIME-Version: 1.0\r\nContent-Type: text/html\r\nContent-Location: http://h/p.html\r\n\r\n<p>x</p>"u8.ToArray();
    Assert.That(List(doc).Select(e => e.Name), Is.EqualTo(new[] { "p.html" }));
    Assert.That(Extract(doc, "p.html"), Is.EqualTo("<p>x</p>"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(doc);
    Assert.Throws<NotSupportedException>(() => Descriptor.Add(ms, [ArchiveInputInfo.InMemory("a", [1])]));
    Assert.That(ms.ToArray(), Is.EqualTo(doc));
  }

  [Test, Category("EdgeCase")]
  public void Given_DuplicateLocations_When_Read_Then_NamesAreMadeUnique() {
    var doc = Mime("--b\r\nContent-Location: http://h/a.txt\r\n\r\none\r\n--b\r\nContent-Location: http://h/a.txt\r\n\r\ntwo\r\n--b--\r\n");
    Assert.That(List(doc).Select(e => e.Name), Is.EqualTo(new[] { "a.txt", "a~2.txt" }));
    Assert.That(Extract(doc, "a~2.txt"), Is.EqualTo("two"u8.ToArray()));
  }

  [Test, Category("EdgeCase")]
  public void Given_LineThatOnlyStartsWithTheBoundary_When_Read_Then_ItIsContent() {
    var doc = Mime("--b\r\nContent-Location: http://h/a.txt\r\n\r\n--bx\r\n--b \r\nContent-Location: http://h/c.txt\r\n\r\nc\r\n--b--\r\n");
    Assert.That(Extract(doc, "a.txt"), Is.EqualTo("--bx"u8.ToArray()));
    Assert.That(Extract(doc, "c.txt"), Is.EqualTo("c"u8.ToArray()));
  }

  [Test, Category("EdgeCase")]
  public void Given_DotSegmentsInLocation_When_Read_Then_TheyCannotEscapeTheArchive()
    => Assert.That(List(Mime("--b\r\nContent-Location: ../../etc/passwd\r\n\r\nx\r\n--b--\r\n")).Single().Name, Is.EqualTo("etc/passwd"));

  [Test, Category("EdgeCase")]
  public void Given_MissingCloseDelimiter_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new MhtReader(Mime("--b\r\nContent-Location: http://h/a\r\n\r\nx\r\n")));

  [Test, Category("EdgeCase")]
  public void Given_MultipartWithoutBoundary_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new MhtReader("MIME-Version: 1.0\r\nContent-Type: multipart/related\r\n\r\nx"u8.ToArray()));

  [Test, Category("EdgeCase")]
  public void Given_NonMimeBytes_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new MhtReader([0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0]));

  [Test, Category("EdgeCase")]
  public void Given_TruncatedChromeSnapshot_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new MhtReader(Vector("chrome-blink.mhtml")[..1500]));

  [Test, Category("EdgeCase")]
  public void Given_CorruptBase64_When_Listed_Then_NoThrowButExtractRefuses() {
    var doc = Mime("--b\r\nContent-Transfer-Encoding: base64\r\nContent-Location: http://h/a.bin\r\n\r\nQUJD!!\r\n--b--\r\n");
    Assert.That(List(doc), Has.Count.EqualTo(1));
    Assert.Throws<InvalidDataException>(() => Extract(doc, "a.bin"));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnknownTransferEncoding_When_Extracted_Then_NotSupported() {
    var doc = Mime("--b\r\nContent-Transfer-Encoding: x-uuencode\r\nContent-Location: http://h/a.bin\r\n\r\nbegin\r\n--b--\r\n");
    Assert.That(List(doc).Single().Method, Is.EqualTo("x-uuencode"));
    Assert.Throws<NotSupportedException>(() => Extract(doc, "a.bin"));
  }

  [Test, Category("EdgeCase")]
  public void Given_QuotedPrintableSoftBreaksAndPadding_When_Extracted_Then_Rfc2045Decoding() {
    var doc = Mime("--b\r\nContent-Transfer-Encoding: quoted-printable\r\nContent-Location: http://h/a.txt\r\n\r\nab=\r\ncd =3D=c3=a4  \r\nx=4\r\n--b--\r\n");
    Assert.That(Extract(doc, "a.txt"), Is.EqualTo("abcd =ä\r\nx=4"u8.ToArray()));
  }

  private static byte[] Mime(string body)
    => Encoding.UTF8.GetBytes("MIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=b\r\n\r\n" + body);
}
