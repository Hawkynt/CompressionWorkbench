using System.Text;
using FileFormat.Wheel;
using FileFormat.Zip;

namespace Compression.Tests.Wheel;

[TestFixture]
public class WheelTests {

  /// <summary>
  /// Builds a minimal valid wheel: ZIP containing <c>foo-1.2.dist-info/METADATA</c>,
  /// <c>foo-1.2.dist-info/WHEEL</c>, <c>foo-1.2.dist-info/RECORD</c> and a token
  /// payload module so the listing has more than just the dist-info.
  /// </summary>
  private static byte[] BuildWheel() {
    using var ms = new MemoryStream();
    using (var zip = new ZipWriter(ms, leaveOpen: true)) {
      var metadata = """
        Metadata-Version: 2.1
        Name: foo
        Version: 1.2.3
        Summary: Example wheel for tests
        Author: Test Author
        License: MIT
        Requires-Python: >=3.10
        Requires-Dist: requests (>=2.0)
        Requires-Dist: pyyaml
        """.Replace("\r", "");
      zip.AddEntry("foo-1.2.dist-info/METADATA", Encoding.UTF8.GetBytes(metadata));
      var wheel = """
        Wheel-Version: 1.0
        Generator: bdist_wheel (0.40.0)
        Root-Is-Purelib: true
        Tag: py3-none-any
        """.Replace("\r", "");
      zip.AddEntry("foo-1.2.dist-info/WHEEL", Encoding.UTF8.GetBytes(wheel));
      var record = "foo/__init__.py,sha256=abc,42\nfoo-1.2.dist-info/METADATA,sha256=xyz,128\n";
      zip.AddEntry("foo-1.2.dist-info/RECORD", Encoding.UTF8.GetBytes(record));
      zip.AddEntry("foo/__init__.py", Encoding.UTF8.GetBytes("# foo\n"));
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new WheelFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Wheel"));
    Assert.That(d.Extensions, Contains.Item(".whl"));
    Assert.That(d.MagicSignatures, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMetadataAndZipContents() {
    var data = BuildWheel();
    using var ms = new MemoryStream(data);
    var entries = new WheelFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("foo-1.2.dist-info/METADATA"));
    Assert.That(names, Does.Contain("foo-1.2.dist-info/WHEEL"));
    Assert.That(names, Does.Contain("foo/__init__.py"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_WritesParsedMetadata() {
    var data = BuildWheel();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new WheelFormatDescriptor().Extract(ms, tmp, null, null);
      var metaPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(metaPath), Is.True);
      var meta = File.ReadAllText(metaPath);
      Assert.That(meta, Does.Contain("dist_info = foo-1.2.dist-info"));
      Assert.That(meta, Does.Contain("name = foo"));
      Assert.That(meta, Does.Contain("version = 1.2.3"));
      Assert.That(meta, Does.Contain("wheel_version = 1.0"));
      Assert.That(meta, Does.Contain("tag_0 = py3-none-any"));
      Assert.That(meta, Does.Contain("requires_dist_count = 2"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void List_ZipWithoutDistInfo_Throws() {
    using var ms = new MemoryStream();
    using (var zip = new ZipWriter(ms, leaveOpen: true)) {
      zip.AddEntry("foo/__init__.py", "# foo\n"u8.ToArray());
    }
    ms.Position = 0;
    Assert.That(() => new WheelFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }
}
