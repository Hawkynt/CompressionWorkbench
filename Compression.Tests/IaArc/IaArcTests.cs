using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.IaArc;

namespace Compression.Tests.IaArc;

/// <summary>
/// Internet Archive ARC. Fixtures: warcio's ARC test files and the first 23 records of a Heritrix
/// crawl (IAH-20080430204825-00000-blackbook, via richardlehane/webarchive); <c>warcio-listing.txt</c>
/// is warcio's reading of each. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class IaArcTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"IaArcVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded ARC vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>The plain ARC stream: .gz fixtures are a gzip member per record, decompressed by our gzip.</summary>
  private static byte[] Plain(string name) {
    var raw = Vector(name);
    if (!name.EndsWith(".gz", StringComparison.Ordinal))
      return raw;
    using var output = new MemoryStream();
    new FileFormat.Gzip.GzipFormatDescriptor().Decompress(new MemoryStream(raw), output);
    return output.ToArray();
  }

  [TestCase("example.arc")]
  [TestCase("example.arc.gz")]
  [TestCase("blackbook-head.arc.gz")]
  [TestCase("ours-v1.arc")]
  [Category("ThirdPartyVector")]
  public void Given_RealArc_When_Read_Then_MatchesWarcio(string vector) {
    var expected = Encoding.UTF8.GetString(Vector("warcio-listing.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split(' ', 5))
      .Where(p => p[0] == vector)
      .ToList();
    var r = new IaArcReader(new MemoryStream(Plain(vector)));

    var header = expected.Single(p => p[1] == "arc_header");
    Assert.That(r.FileDescription.Url, Is.EqualTo(header[4]));
    Assert.That(r.Version, Is.EqualTo(1));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(r.VersionBlock)), Is.EqualTo(header[3]));

    var records = expected.Where(p => p[1] != "arc_header").ToList();
    Assert.That(r.Records.Select(x => x.Url), Is.EqualTo(records.Select(p => p[4])));
    for (var i = 0; i < records.Count; ++i) {
      Assert.That(r.Records[i].BodyLength, Is.EqualTo(long.Parse(records[i][2])), records[i][4]);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(r.ReadBody(r.Records[i]))), Is.EqualTo(records[i][3]), records[i][4]);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_RealArc_When_Listed_Then_NamesAreSanitizedAndExtractable() {
    var d = new IaArcFormatDescriptor();
    using var ms = new MemoryStream(Vector("example.arc"));
    var e = d.List(ms, null).Single();
    Assert.That(e.Name, Is.EqualTo("example.com"));
    Assert.That(e.Method, Is.EqualTo("text/html"));
    Assert.That(e.LastModified, Is.EqualTo(new DateTime(2014, 2, 16, 5, 2, 21, DateTimeKind.Utc)));
    ms.Position = 0;
    Assert.That(((IArchiveFormatOperations)d).ExtractEntryToMemory(ms, e.Name, null), Has.Length.EqualTo(1591));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedLastRecord_When_Read_Then_InvalidDataRatherThanShortBody() {
    // warcio's example-space-in-url.arc declares 1591 body bytes but holds 1579; warcio returns the
    // short body, we refuse instead of handing out a capture that is not what was archived.
    Assert.Throws<InvalidDataException>(() => _ = new IaArcReader(new MemoryStream(Vector("example-space-in-url.arc"))));
  }

  /// <summary>The deterministic archive behind ours-v1.arc (written once, then read by warcio).</summary>
  internal static void WriteOursV1(Stream output) {
    var t = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    IaArcWriter.Write(output, "ours-v1.arc", t, [
      new IaArcWriter.Record("http://example.org/", "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\nhello"u8.ToArray(), t, "text/plain", "192.0.2.1"),
      new IaArcWriter.Record("dir/b.bin", [.. Enumerable.Range(0, 300).Select(i => (byte)i)], t),
      new IaArcWriter.Record("http://example.org/a b?c", "x\n"u8.ToArray(), t, "text/plain"),
    ]);
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_SameRecords_When_Written_Then_ByteIdenticalToTheFileWarcioRead() {
    using var ms = new MemoryStream();
    WriteOursV1(ms);
    Assert.That(ms.ToArray(), Is.EqualTo(Vector("ours-v1.arc")));
  }

  [Test, Category("HappyPath")]
  public void Given_DotArcCrawlFile_When_Detected_Then_IaArcNotSeaArc() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var path = Path.Combine(Path.GetTempPath(), $"cwb_iaarc_{Guid.NewGuid():N}.arc");
    try {
      File.WriteAllBytes(path, Vector("example.arc"));
      Assert.That(Compression.Lib.FormatDetector.Detect(path), Is.EqualTo(Compression.Lib.FormatDetector.Format.IaArc));
    } finally {
      File.Delete(path);
    }
  }

  [Test, Category("EdgeCase")]
  public void Given_WarciosBadArc_When_Read_Then_InvalidData() {
    Assert.Throws<InvalidDataException>(() => _ = new IaArcReader(new MemoryStream(Vector("bad.arc"))));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoFiledesc_When_Read_Then_InvalidData() {
    Assert.Throws<InvalidDataException>(() => _ = new IaArcReader(new MemoryStream("WARC/1.0\r\n"u8.ToArray())));
  }

  [Test, Category("EdgeCase")]
  public void Given_Empty_When_Read_Then_InvalidData() {
    Assert.Throws<InvalidDataException>(() => _ = new IaArcReader(new MemoryStream()));
  }

  [Test, Category("EdgeCase")]
  public void Given_VersionTwoBlock_When_Read_Then_TenFieldLinesParse() {
    var body = "HTTP/1.0 200 OK\r\n\r\nhi"u8.ToArray();
    var block = "2 0 Test\nURL IP-address Archive-date Content-type Result-code Checksum Location Offset Filename Archive-length\n";
    var text = $"filedesc://t.arc 0.0.0.0 19961104142103 text/plain 200 - - 0 t.arc {block.Length}\n{block}\n" +
               $"http://a b/ 1.2.3.4 19961104142103 text/html 200 abcdef - 0 t.arc {body.Length}\n";
    var bytes = Encoding.ASCII.GetBytes(text).Concat(body).Concat("\n"u8.ToArray()).ToArray();
    var r = new IaArcReader(new MemoryStream(bytes));
    Assert.That(r.Version, Is.EqualTo(2));
    Assert.That(r.FileDescription.Url, Is.EqualTo("filedesc://t.arc"));
    Assert.That(r.Records.Single().Url, Is.EqualTo("http://a b/"));
    Assert.That(r.Records.Single().ContentType, Is.EqualTo("text/html"));
    Assert.That(r.ReadBody(r.Records.Single()), Is.EqualTo(body));
    Assert.Throws<NotSupportedException>(() =>
      ((IArchiveModifiable)new IaArcFormatDescriptor()).Add(new MemoryStream(bytes), [ArchiveInputInfo.InMemory("x", [1])]));
  }

  [Test, Category("RoundTrip")]
  public void Given_Files_When_Created_Then_ReadBackWithStampsAndNames() {
    var stamp = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
    var d = new IaArcFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [
      ArchiveInputInfo.InMemory("a.txt", "alpha"u8.ToArray(), new ArchiveEntryMetadata(LastWriteTimeUtc: stamp)),
      ArchiveInputInfo.InMemory("dir/b.bin", new byte[] { 0, 10, 13, 255 }, new ArchiveEntryMetadata(LastWriteTimeUtc: stamp)),
    ], new FormatCreateOptions());
    ms.Position = 0;
    var list = d.List(ms, null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "a.txt", "dir/b.bin" }));
    Assert.That(list[0].LastModified, Is.EqualTo(stamp.UtcDateTime));
    ms.Position = 0;
    Assert.That(((IArchiveFormatOperations)d).ExtractEntryToMemory(ms, "dir/b.bin", null), Is.EqualTo(new byte[] { 0, 10, 13, 255 }));
  }

  [Test, Category("RoundTrip")]
  public void Given_RealArc_When_RecordRemovedAndAdded_Then_SurvivorsByteIdentical() {
    var plain = Plain("blackbook-head.arc.gz");
    var d = (IArchiveModifiable)new IaArcFormatDescriptor();
    var before = new IaArcReader(new MemoryStream(plain));
    var keptBody = before.ReadBody(before.Records[2]);

    using var ms = new MemoryStream();
    ms.Write(plain);
    d.Remove(ms, ["www.archive.org/robots.txt"]);
    d.Add(ms, [ArchiveInputInfo.InMemory("note.txt", "added"u8.ToArray())]);

    var after = new IaArcReader(ms);
    Assert.That(after.Records, Has.Count.EqualTo(before.Records.Count));
    Assert.That(after.Records.Select(x => x.Url), Does.Not.Contain("http://www.archive.org/robots.txt"));
    Assert.That(after.Records[^1].Url, Is.EqualTo("note.txt"));
    Assert.That(after.ReadBody(after.Records[1]), Is.EqualTo(keptBody));
    // The filedesc record and the first record are copied byte for byte.
    var prefix = (int)before.Records[1].RecordOffset;
    Assert.That(ms.ToArray()[..prefix], Is.EqualTo(plain[..prefix]));
  }

  [Test, Category("EdgeCase")]
  public void Given_DuplicateUrls_When_Named_Then_Disambiguated() {
    var recs = new[] {
      new IaArcRecord(0, "", "http://x/a", "", "", "", 0, 0),
      new IaArcRecord(0, "", "http://x/a?q=1", "", "", "", 0, 0),
      new IaArcRecord(0, "", "dns:x", "", "", "", 0, 0),
      new IaArcRecord(0, "", "http://../", "", "", "", 0, 0),
    };
    Assert.That(IaArcFormatDescriptor.EntryNames(recs), Is.EqualTo(new[] { "x/a", "x/a~2", "dns_x", "record-0003" }));
  }
}
