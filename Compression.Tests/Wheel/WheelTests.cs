using System.Text;
using Compression.Registry;
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
    Assert.Multiple(() => {
      Assert.That(d.Id, Is.EqualTo("Wheel"));
      Assert.That(d.Extensions, Contains.Item(".whl"));
      Assert.That(d.MagicSignatures, Is.Empty);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    });
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

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_GeneratesRecordAndRoundTrips() {
    var metadata = "Metadata-Version: 2.1\nName: foo\nVersion: 1.2.3\n"u8.ToArray();
    var wheel = "Wheel-Version: 1.0\nGenerator: CompressionWorkbench\nRoot-Is-Purelib: true\nTag: py3-none-any\n"u8.ToArray();
    var module = "# foo\n"u8.ToArray();
    ArchiveInputInfo[] inputs = [
      ArchiveInputInfo.InMemory("foo/__init__.py", module),
      ArchiveInputInfo.InMemory("foo-1.2.dist-info/METADATA", metadata),
      ArchiveInputInfo.InMemory("foo-1.2.dist-info/WHEEL", wheel),
    ];

    using var output = new MemoryStream();
    var descriptor = new WheelFormatDescriptor();
    descriptor.Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    using (var zip = new ZipReader(output, leaveOpen: true)) {
      var recordEntry = zip.Entries.Single(entry => entry.FileName == "foo-1.2.dist-info/RECORD");
      var record = Encoding.UTF8.GetString(zip.ExtractEntry(recordEntry));
      Assert.Multiple(() => {
        Assert.That(record, Does.Contain("foo/__init__.py,sha256="));
        Assert.That(record, Does.Contain("foo-1.2.dist-info/METADATA,sha256="));
        Assert.That(record, Does.Contain("foo-1.2.dist-info/WHEEL,sha256="));
        Assert.That(record, Does.EndWith("foo-1.2.dist-info/RECORD,,\n"));
      });
    }

    output.Position = 0;
    var listed = descriptor.List(output, null).Select(entry => entry.Name).ToArray();
    Assert.Multiple(() => {
      Assert.That(listed, Does.Contain("foo/__init__.py"));
      Assert.That(listed, Does.Contain("foo-1.2.dist-info/METADATA"));
      Assert.That(listed, Does.Contain("foo-1.2.dist-info/WHEEL"));
      Assert.That(listed, Does.Contain("foo-1.2.dist-info/RECORD"));
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_GenericFiles_SynthesizesMinimalWheelMetadata() {
    ArchiveInputInfo[] inputs = [ArchiveInputInfo.InMemory("docs/readme.txt", "hello\n"u8.ToArray())];
    using var output = new MemoryStream();
    var descriptor = new WheelFormatDescriptor();

    descriptor.Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    using var zip = new ZipReader(output, leaveOpen: true);
    var names = zip.Entries.Select(entry => entry.FileName).ToArray();
    var metadataEntry = zip.Entries.Single(entry => entry.FileName == "compression_workbench_archive-0.dist-info/METADATA");
    var metadata = Encoding.UTF8.GetString(zip.ExtractEntry(metadataEntry));
    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("docs/readme.txt"));
      Assert.That(names, Does.Contain("compression_workbench_archive-0.dist-info/WHEEL"));
      Assert.That(names, Does.Contain("compression_workbench_archive-0.dist-info/RECORD"));
      Assert.That(metadata, Does.Contain("Name: compression-workbench-archive"));
      Assert.That(metadata, Does.Contain("Version: 0"));
    });

    output.Position = 0;
    Assert.That(descriptor.List(output, null).Select(entry => entry.Name), Does.Contain("docs/readme.txt"));
  }

  /// <summary>The same tree twice must give the same bytes.</summary>
  [Test, Category("EdgeCase")]
  public void Create_GenericFiles_IsDeterministic() {
    ArchiveInputInfo[] inputs = [ArchiveInputInfo.InMemory("docs/readme.txt", "hello\n"u8.ToArray())];
    using var first = new MemoryStream();
    using var second = new MemoryStream();
    new WheelFormatDescriptor().Create(first, inputs, new FormatCreateOptions());
    new WheelFormatDescriptor().Create(second, inputs, new FormatCreateOptions());
    Assert.That(second.ToArray(), Is.EqualTo(first.ToArray()));
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
