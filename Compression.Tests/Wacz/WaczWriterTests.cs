using System.Text;
using Compression.Registry;
using FileFormat.Wacz;
using FileFormat.Zip;

namespace Compression.Tests.Wacz;

[TestFixture]
public sealed class WaczWriterTests {
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_WritesValidContainerThatReaderLists() {
    ArchiveInputInfo[] inputs = [
      ArchiveInputInfo.InMemory("datapackage.json", "{\"profile\":\"data-package\",\"resources\":[],\"wacz_version\":\"1.1.1\"}"u8.ToArray()),
      ArchiveInputInfo.InMemory("archive/data.warc.gz", new byte[] { 0x1F, 0x8B, 0x08, 0x00 }),
      ArchiveInputInfo.InMemory("pages/pages.jsonl", "{\"format\":\"json-pages-1.0\"}\n{\"url\":\"https://example.test\",\"ts\":\"2026-01-01T00:00:00Z\"}\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("indexes/index.cdxj", "com,example)/ 20260101000000 {\"url\":\"https://example.test\"}\n"u8.ToArray()),
    ];

    using var output = new MemoryStream();
    var descriptor = new WaczFormatDescriptor();
    descriptor.Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var entries = descriptor.List(output, null).Select(entry => entry.Name).ToArray();
    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(entries, Does.Contain("datapackage.json"));
      Assert.That(entries, Does.Contain("archive/data.warc.gz"));
      Assert.That(entries, Does.Contain("pages/pages.jsonl"));
      Assert.That(entries, Does.Contain("indexes/index.cdxj"));
    });

    output.Position = 0;
    using var zip = new ZipReader(output, leaveOpen: true);
    var warc = zip.Entries.Single(entry => entry.FileName == "archive/data.warc.gz");
    Assert.That(warc.CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store),
      "already-gzipped WARC members should not be deflated a second time");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_GenericFiles_PreservesPayloadAndSynthesizesWebArchiveResources() {
    var payload = "generic archive payload\n"u8.ToArray();
    ArchiveInputInfo[] inputs = [ArchiveInputInfo.InMemory("docs/readme.txt", payload)];
    using var output = new MemoryStream();
    var descriptor = new WaczFormatDescriptor();

    descriptor.Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    using (var zip = new ZipReader(output, leaveOpen: true)) {
      var names = zip.Entries.Select(entry => entry.FileName).ToArray();
      Assert.Multiple(() => {
        Assert.That(names, Does.Contain("docs/readme.txt"));
        Assert.That(names, Does.Contain("archive/data.warc"));
        Assert.That(names, Does.Contain("indexes/index.cdxj"));
        Assert.That(names, Does.Contain("pages/pages.jsonl"));
        Assert.That(names, Does.Contain("datapackage.json"));
      });

      var warcEntry = zip.Entries.Single(entry => entry.FileName == "archive/data.warc");
      Assert.That(warcEntry.CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store));

      var manifestEntry = zip.Entries.Single(entry => entry.FileName == "datapackage.json");
      var manifest = Encoding.UTF8.GetString(zip.ExtractEntry(manifestEntry));
      Assert.Multiple(() => {
        Assert.That(manifest, Does.Contain("\"profile\": \"data-package\""));
        Assert.That(manifest, Does.Contain("\"wacz_version\": \"1.1.1\""));
        Assert.That(manifest, Does.Contain("\"path\": \"docs/readme.txt\""));
        Assert.That(manifest, Does.Contain("sha256:"));
      });
    }

    output.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(output, "docs/readme.txt", null), Is.EqualTo(payload).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void Create_SuppliedWarcWithoutManifest_GeneratesManifest() {
    ArchiveInputInfo[] inputs = [
      ArchiveInputInfo.InMemory("archive/data.warc", "WARC/1.0\r\nWARC-Type: resource\r\nWARC-Record-ID: <urn:test>\r\nWARC-Date: 2026-01-01T00:00:00Z\r\nContent-Length: 0\r\n\r\n\r\n\r\n"u8.ToArray()),
    ];
    using var output = new MemoryStream();

    new WaczFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    using var zip = new ZipReader(output, leaveOpen: true);
    var manifest = zip.Entries.Single(entry => entry.FileName == "datapackage.json");
    var text = Encoding.UTF8.GetString(zip.ExtractEntry(manifest));
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("\"profile\": \"data-package\""));
      Assert.That(text, Does.Contain("\"path\": \"archive/data.warc\""));
    });
  }
}
