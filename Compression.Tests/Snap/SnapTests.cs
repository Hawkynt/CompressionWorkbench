using System.Text;
using Compression.Registry;
using FileFormat.Snap;
using FileSystem.SquashFs;

namespace Compression.Tests.Snap;

[TestFixture]
public class SnapTests {

  /// <summary>
  /// Builds a minimal SquashFS image containing the canonical
  /// <c>meta/snap.yaml</c> plus one payload file.
  /// </summary>
  private static byte[] BuildMinimalSnap() {
    const string snapYaml = """
      name: test-snap
      version: 0.1
      summary: A tiny test snap
      description: Fixture used by unit tests.
      confinement: strict
      base: core22
      grade: stable
      """;

    using var ms = new MemoryStream();
    using (var w = new SquashFsWriter(ms, leaveOpen: true)) {
      w.AddFile("meta/snap.yaml", Encoding.UTF8.GetBytes(snapYaml));
      w.AddFile("bin/hello", Encoding.UTF8.GetBytes("hello snap"));
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties_AreStable() {
    var d = new SnapFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Snap"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".snap"));
    Assert.That(d.Extensions, Contains.Item(".snap"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsMetadataAndFilesystemEntries() {
    var data = BuildMinimalSnap();
    using var ms = new MemoryStream(data);
    var entries = new SnapFormatDescriptor().List(ms, null);

    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("metadata.ini"));
    Assert.That(entries.Any(e => e.Name.Contains("meta/snap.yaml")), Is.True);
    Assert.That(entries.Any(e => e.Name.Contains("bin/hello")), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_EmitsMetadataWithParsedSnapYaml() {
    var data = BuildMinimalSnap();
    using var ms = new MemoryStream(data);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      new SnapFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);
      var text = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(text, Does.Contain("name = test-snap"));
      Assert.That(text, Does.Contain("version = 0.1"));
      Assert.That(text, Does.Contain("confinement = strict"));
      Assert.That(text, Does.Contain("base = core22"));
      Assert.That(text, Does.Contain("snap_yaml_present = True"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void List_NonSquashFsInput_Throws() {
    using var ms = new MemoryStream(Enumerable.Repeat((byte)0xAB, 256).ToArray());
    Assert.That(() => new SnapFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }
}
