using System.Text;
using Compression.Registry;
using FileFormat.Msix;
using FileFormat.Zip;

namespace Compression.Tests.Msix;

[TestFixture]
public class MsixTests {

  /// <summary>
  /// Builds a minimal MSIX-shaped ZIP with an <c>AppxManifest.xml</c> carrying
  /// an Identity element plus a second payload file so there are at least two
  /// archive-level entries behind the synthetic <c>metadata.ini</c>.
  /// </summary>
  private static byte[] BuildMinimalMsix() {
    const string manifest = """
      <?xml version="1.0" encoding="utf-8"?>
      <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
        <Identity Name="TestApp"
                  Publisher="CN=TestPublisher"
                  Version="1.2.3.4"
                  ProcessorArchitecture="x64" />
        <Properties>
          <DisplayName>Test Application</DisplayName>
          <PublisherDisplayName>Test Publisher Ltd</PublisherDisplayName>
          <Description>Unit-test fixture MSIX.</Description>
        </Properties>
      </Package>
      """;

    using var ms = new MemoryStream();
    using (var w = new ZipWriter(ms, leaveOpen: true)) {
      w.AddEntry("AppxManifest.xml", Encoding.UTF8.GetBytes(manifest));
      w.AddEntry("app/hello.txt", Encoding.UTF8.GetBytes("hello msix"));
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties_AreStable() {
    var d = new MsixFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Msix"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".msix"));
    Assert.That(d.Extensions, Contains.Item(".msix"));
    Assert.That(d.Extensions, Contains.Item(".msixbundle"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsMetadataPlusZipEntries() {
    var data = BuildMinimalMsix();
    using var ms = new MemoryStream(data);
    var entries = new MsixFormatDescriptor().List(ms, null);

    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("metadata.ini"));
    Assert.That(entries.Any(e => e.Name == "AppxManifest.xml"), Is.True);
    Assert.That(entries.Any(e => e.Name == "app/hello.txt"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void List_MetadataParsesIdentity() {
    var data = BuildMinimalMsix();
    using var ms = new MemoryStream(data);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      new MsixFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);
      var text = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(text, Does.Contain("name = TestApp"));
      Assert.That(text, Does.Contain("version = 1.2.3.4"));
      Assert.That(text, Does.Contain("processor_architecture = x64"));
      Assert.That(text, Does.Contain("manifest_kind = AppxManifest"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void List_NonZipInput_Throws() {
    using var ms = new MemoryStream(Enumerable.Repeat((byte)0xAB, 128).ToArray());
    Assert.That(() => new MsixFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }
}
