using System.Text;
using FileFormat.Wacz;
using FileFormat.Zip;

namespace Compression.Tests.Wacz;

[TestFixture]
public class WaczTests {

  /// <summary>
  /// Builds a minimal valid WACZ in memory: a ZIP containing a top-level
  /// <c>datapackage.json</c> with title/version, an <c>archive/data.warc.gz</c>
  /// blob (contents are arbitrary — the descriptor only counts entries) and a
  /// <c>pages/pages.jsonl</c> page index.
  /// </summary>
  private static byte[] BuildWacz() {
    using var ms = new MemoryStream();
    using (var zip = new ZipWriter(ms, leaveOpen: true)) {
      var datapackage = """
        {
          "title": "Test Crawl",
          "wacz_version": "1.1.1",
          "software": "test-suite/1.0"
        }
        """;
      zip.AddEntry("datapackage.json", Encoding.UTF8.GetBytes(datapackage));
      zip.AddEntry("archive/data.warc.gz", new byte[] { 0x1F, 0x8B, 0x08 });
      // A 3-line JSONL file: 1 header line + 2 page lines.
      zip.AddEntry("pages/pages.jsonl", Encoding.UTF8.GetBytes("{\"format\":\"json-pages-1.0\"}\n{\"url\":\"https://a\"}\n{\"url\":\"https://b\"}\n"));
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new WaczFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Wacz"));
    Assert.That(d.Extensions, Contains.Item(".wacz"));
    Assert.That(d.MagicSignatures, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMetadataAndUnderlyingZipEntries() {
    var data = BuildWacz();
    using var ms = new MemoryStream(data);
    var entries = new WaczFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("datapackage.json"));
    Assert.That(names, Does.Contain("archive/data.warc.gz"));
    Assert.That(names, Does.Contain("pages/pages.jsonl"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_WritesParsedMetadata() {
    var data = BuildWacz();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new WaczFormatDescriptor().Extract(ms, tmp, null, null);
      var metaPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(metaPath), Is.True);
      var meta = File.ReadAllText(metaPath);
      Assert.That(meta, Does.Contain("title = Test Crawl"));
      Assert.That(meta, Does.Contain("wacz_version = 1.1.1"));
      Assert.That(meta, Does.Contain("warc_count = 1"));
      Assert.That(meta, Does.Contain("page_count = 2"));
      Assert.That(File.Exists(Path.Combine(tmp, "datapackage.json")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "archive", "data.warc.gz")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void List_PlainZipMissingDatapackage_Throws() {
    using var ms = new MemoryStream();
    using (var zip = new ZipWriter(ms, leaveOpen: true)) {
      zip.AddEntry("readme.txt", "no datapackage here"u8.ToArray());
    }
    ms.Position = 0;
    Assert.That(() => new WaczFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }
}
