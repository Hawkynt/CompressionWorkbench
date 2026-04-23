using System.IO.Compression;
using System.Text;
using FileFormat.Crate;
using FileFormat.Tar;

namespace Compression.Tests.Crate;

[TestFixture]
public class CrateTests {

  /// <summary>
  /// Builds a minimal valid .crate fixture: gzip(tar) where every entry sits
  /// under a single <c>foo-0.1.0/</c> top-level directory with a Cargo.toml at
  /// the conventional location.
  /// </summary>
  private static byte[] BuildCrate() {
    var cargoToml = """
      [package]
      name = "foo"
      version = "0.1.0"
      edition = "2021"
      authors = ["Alice <alice@example.com>", "Bob"]
      description = "A test crate"
      license = "MIT OR Apache-2.0"
      repository = "https://example.com/foo"

      [dependencies]
      serde = "1"
      """.Replace("\r", "");

    var tar = BuildTar([
      ("foo-0.1.0/Cargo.toml", Encoding.UTF8.GetBytes(cargoToml)),
      ("foo-0.1.0/src/lib.rs", "pub fn hello() {}\n"u8.ToArray()),
      ("foo-0.1.0/.cargo_vcs_info.json", "{\"git\":{\"sha1\":\"abc\"}}"u8.ToArray()),
    ]);

    using var ms = new MemoryStream();
    using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) {
      gz.Write(tar);
    }
    return ms.ToArray();
  }

  private static byte[] BuildTar(IEnumerable<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    using var writer = new TarWriter(ms, leaveOpen: true);
    foreach (var (name, data) in files) {
      writer.AddEntry(new TarEntry { Name = name, Size = data.Length }, data);
    }
    writer.Finish();
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new CrateFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Crate"));
    Assert.That(d.Extensions, Contains.Item(".crate"));
    Assert.That(d.MagicSignatures, Is.Empty);
    Assert.That(d.TarCompressionFormatId, Is.EqualTo("Gzip"));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMetadataAndPayload() {
    var data = BuildCrate();
    using var ms = new MemoryStream(data);
    var entries = new CrateFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("foo-0.1.0/Cargo.toml"));
    Assert.That(names, Does.Contain("foo-0.1.0/src/lib.rs"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_WritesParsedCargoToml() {
    var data = BuildCrate();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new CrateFormatDescriptor().Extract(ms, tmp, null, null);
      var metaPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(metaPath), Is.True);
      var meta = File.ReadAllText(metaPath);
      Assert.That(meta, Does.Contain("top_directory = foo-0.1.0"));
      Assert.That(meta, Does.Contain("name = foo"));
      Assert.That(meta, Does.Contain("version = 0.1.0"));
      Assert.That(meta, Does.Contain("edition = 2021"));
      Assert.That(meta, Does.Contain("license = MIT OR Apache-2.0"));
      Assert.That(meta, Does.Contain("Alice <alice@example.com>"));
      Assert.That(File.Exists(Path.Combine(tmp, "foo-0.1.0", "Cargo.toml")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "foo-0.1.0", "src", "lib.rs")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void List_NotGzip_Throws() {
    var data = new byte[64];
    data[0] = (byte)'X'; data[1] = (byte)'Y';
    using var ms = new MemoryStream(data);
    Assert.That(() => new CrateFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }
}
