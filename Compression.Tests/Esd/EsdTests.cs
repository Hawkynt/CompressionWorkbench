using System.Text;
using FileFormat.Esd;
using FileFormat.Wim;

namespace Compression.Tests.Esd;

[TestFixture]
public class EsdTests {

  /// <summary>
  /// Builds a minimal WIM-shaped byte stream (which is what ESD looks like on disk —
  /// the 8-byte MSWIM magic + the standard header + uncompressed resources).
  /// We use the CompressionNone path so the resources round-trip without bringing
  /// any real LZMS encryption into the picture.
  /// </summary>
  private static byte[] BuildWimLikeEsd(IReadOnlyList<byte[]> resources) {
    using var ms = new MemoryStream();
    var writer = new WimWriter(ms, WimConstants.CompressionNone);
    writer.Write(resources);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new EsdFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Esd"));
    Assert.That(d.Extensions, Contains.Item(".esd"));
    Assert.That(d.MagicSignatures, Is.Empty); // extension-only detection
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMetadataAndResourceEntries() {
    var data = BuildWimLikeEsd([Encoding.UTF8.GetBytes("first"), Encoding.UTF8.GetBytes("second")]);
    using var ms = new MemoryStream(data);
    var entries = new EsdFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("manifest.xml"));
    Assert.That(names.Count(n => n.StartsWith("resource_", StringComparison.Ordinal)), Is.EqualTo(2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_WritesPlaintextResources() {
    var first = Encoding.UTF8.GetBytes("ESD plaintext one");
    var second = Encoding.UTF8.GetBytes("ESD plaintext two");
    var data = BuildWimLikeEsd([first, second]);

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new EsdFormatDescriptor().Extract(ms, tmp, null, null);
      var resourceFiles = Directory.GetFiles(tmp, "resource_*.bin");
      Assert.That(resourceFiles, Has.Length.EqualTo(2));
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      var contents = resourceFiles.Select(File.ReadAllBytes).ToList();
      Assert.That(contents.Any(c => c.SequenceEqual(first)), Is.True, "first payload missing from extracted resources");
      Assert.That(contents.Any(c => c.SequenceEqual(second)), Is.True, "second payload missing from extracted resources");
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void List_NonWimInput_Throws() {
    var data = new byte[64];
    data[0] = (byte)'X'; data[1] = (byte)'Y'; data[2] = (byte)'Z';
    using var ms = new MemoryStream(data);
    Assert.That(() => new EsdFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }
}
