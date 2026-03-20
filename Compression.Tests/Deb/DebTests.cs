using System.Text;
using FileFormat.Deb;

namespace Compression.Tests.Deb;

[TestFixture]
public class DebTests {
  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SimplePackage() {
    var controlFiles = new List<DebEntry> {
      new("control", Encoding.UTF8.GetBytes(
        "Package: test\nVersion: 1.0\nArchitecture: all\nDescription: Test\n"), false),
    };

    var dataFiles = new List<DebEntry> {
      new("usr/", [], true),
      new("usr/bin/", [], true),
      new("usr/bin/hello", Encoding.UTF8.GetBytes("#!/bin/sh\necho hello\n"), false),
    };

    using var ms = new MemoryStream();
    var writer = new DebWriter(ms);
    writer.Write(controlFiles, dataFiles);

    ms.Seek(0, SeekOrigin.Begin);
    using var reader = new DebReader(ms);

    // Verify control
    var controlText = reader.GetControlText();
    Assert.That(controlText, Does.Contain("Package: test"));
    Assert.That(controlText, Does.Contain("Version: 1.0"));

    // Verify data
    var data = reader.ReadDataEntries();
    Assert.That(data.Count, Is.GreaterThanOrEqualTo(3));

    var hello = data.FirstOrDefault(e => e.Path.Contains("hello"));
    Assert.That(hello, Is.Not.Null);
    Assert.That(Encoding.UTF8.GetString(hello!.Data), Does.Contain("echo hello"));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyDataArchive() {
    var controlFiles = new List<DebEntry> {
      new("control", Encoding.UTF8.GetBytes("Package: empty\nVersion: 0.1\n"), false),
    };

    using var ms = new MemoryStream();
    var writer = new DebWriter(ms);
    writer.Write(controlFiles, []);

    ms.Seek(0, SeekOrigin.Begin);
    using var reader = new DebReader(ms);

    var data = reader.ReadDataEntries();
    Assert.That(data, Is.Empty);
  }

  [Category("Exception")]
  [Test]
  public void Reader_InvalidPackage_Throws() {
    var bad = new byte[64];
    new Random(1).NextBytes(bad);

    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => new DebReader(ms));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MultipleDataFiles() {
    var controlFiles = new List<DebEntry> {
      new("control", "Package: multi\nVersion: 2.0\n"u8.ToArray(), false),
    };

    var dataFiles = new List<DebEntry> {
      new("etc/", [], true),
      new("etc/config.txt", Encoding.UTF8.GetBytes("key=value\n"), false),
      new("usr/", [], true),
      new("usr/share/", [], true),
      new("usr/share/doc/", [], true),
      new("usr/share/doc/README", Encoding.UTF8.GetBytes("Read me!\n"), false),
    };

    using var ms = new MemoryStream();
    new DebWriter(ms).Write(controlFiles, dataFiles);

    ms.Seek(0, SeekOrigin.Begin);
    using var reader = new DebReader(ms);

    var data = reader.ReadDataEntries();
    var config = data.FirstOrDefault(e => e.Path.Contains("config.txt"));
    var readme = data.FirstOrDefault(e => e.Path.Contains("README"));

    Assert.That(config, Is.Not.Null);
    Assert.That(readme, Is.Not.Null);
    Assert.That(Encoding.UTF8.GetString(config!.Data), Is.EqualTo("key=value\n"));
    Assert.That(Encoding.UTF8.GetString(readme!.Data), Is.EqualTo("Read me!\n"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_XzCompression() {
    var controlFiles = new List<DebEntry> {
      new("control", "Package: xztest\nVersion: 1.0\n"u8.ToArray(), false),
    };

    var dataFiles = new List<DebEntry> {
      new("usr/", [], true),
      new("usr/bin/", [], true),
      new("usr/bin/hello", Encoding.UTF8.GetBytes("#!/bin/sh\necho xz\n"), false),
    };

    using var ms = new MemoryStream();
    new DebWriter(ms, DebCompression.Xz).Write(controlFiles, dataFiles);

    ms.Seek(0, SeekOrigin.Begin);
    using var reader = new DebReader(ms);

    var controlText = reader.GetControlText();
    Assert.That(controlText, Does.Contain("Package: xztest"));

    var data = reader.ReadDataEntries();
    var hello = data.FirstOrDefault(e => e.Path.Contains("hello"));
    Assert.That(hello, Is.Not.Null);
    Assert.That(Encoding.UTF8.GetString(hello!.Data), Does.Contain("echo xz"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_ZstdCompression() {
    var controlFiles = new List<DebEntry> {
      new("control", "Package: zsttest\nVersion: 1.0\n"u8.ToArray(), false),
    };

    var dataFiles = new List<DebEntry> {
      new("usr/", [], true),
      new("usr/bin/", [], true),
      new("usr/bin/hello", Encoding.UTF8.GetBytes("#!/bin/sh\necho zstd\n"), false),
    };

    using var ms = new MemoryStream();
    new DebWriter(ms, DebCompression.Zstd).Write(controlFiles, dataFiles);

    ms.Seek(0, SeekOrigin.Begin);
    using var reader = new DebReader(ms);

    var controlText = reader.GetControlText();
    Assert.That(controlText, Does.Contain("Package: zsttest"));

    var data = reader.ReadDataEntries();
    var hello = data.FirstOrDefault(e => e.Path.Contains("hello"));
    Assert.That(hello, Is.Not.Null);
    Assert.That(Encoding.UTF8.GetString(hello!.Data), Does.Contain("echo zstd"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Bzip2Compression() {
    var controlFiles = new List<DebEntry> {
      new("control", "Package: bz2test\nVersion: 1.0\n"u8.ToArray(), false),
    };

    var dataFiles = new List<DebEntry> {
      new("usr/", [], true),
      new("usr/bin/", [], true),
      new("usr/bin/hello", Encoding.UTF8.GetBytes("#!/bin/sh\necho bzip2\n"), false),
    };

    using var ms = new MemoryStream();
    new DebWriter(ms, DebCompression.Bzip2).Write(controlFiles, dataFiles);

    ms.Seek(0, SeekOrigin.Begin);
    using var reader = new DebReader(ms);

    var controlText = reader.GetControlText();
    Assert.That(controlText, Does.Contain("Package: bz2test"));

    var data = reader.ReadDataEntries();
    var hello = data.FirstOrDefault(e => e.Path.Contains("hello"));
    Assert.That(hello, Is.Not.Null);
    Assert.That(Encoding.UTF8.GetString(hello!.Data), Does.Contain("echo bzip2"));
  }
}
