using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Gem;
using FileFormat.Tar;

namespace Compression.Tests.Gem;

[TestFixture]
public class GemTests {

  /// <summary>
  /// Builds a minimal valid .gem fixture: outer TAR with metadata.gz, data.tar.gz
  /// and checksums.yaml.gz. The metadata YAML uses the canonical
  /// <c>!ruby/object:Gem::Specification</c> shape that <c>gem build</c> emits.
  /// </summary>
  private static byte[] BuildGem() {
    var metadataYaml = """
      --- !ruby/object:Gem::Specification
      name: example_gem
      version: !ruby/object:Gem::Version
        version: 0.1.5
      summary: A test gem fixture
      license: MIT
      homepage: https://example.com
      dependencies:
      - !ruby/object:Gem::Dependency
        name: rspec
      - !ruby/object:Gem::Dependency
        name: rake
      """.Replace("\r", "");

    var innerTar = BuildSimpleTar([
      ("lib/example_gem.rb", "module ExampleGem; end\n"u8.ToArray()),
      ("README.md", "# example_gem\n"u8.ToArray()),
    ]);

    var checksums = "---\nSHA256:\n  metadata.gz: 0\n  data.tar.gz: 0\n"u8.ToArray();

    using var ms = new MemoryStream();
    using var writer = new TarWriter(ms, leaveOpen: true);
    AddGzippedEntry(writer, "metadata.gz", Encoding.UTF8.GetBytes(metadataYaml));
    AddGzippedEntry(writer, "data.tar.gz", innerTar);
    AddGzippedEntry(writer, "checksums.yaml.gz", checksums);
    writer.Finish();
    return ms.ToArray();
  }

  private static byte[] BuildSimpleTar(IEnumerable<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    using var writer = new TarWriter(ms, leaveOpen: true);
    foreach (var (name, data) in files)
      writer.AddEntry(new TarEntry { Name = name, Size = data.Length }, data);
    writer.Finish();
    return ms.ToArray();
  }

  private static void AddGzippedEntry(TarWriter writer, string name, byte[] data) {
    using var gzMs = new MemoryStream();
    using (var gz = new GZipStream(gzMs, CompressionLevel.Fastest, leaveOpen: true))
      gz.Write(data);
    var gzipped = gzMs.ToArray();
    writer.AddEntry(new TarEntry { Name = name, Size = gzipped.Length }, gzipped);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new GemFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Id, Is.EqualTo("Gem"));
      Assert.That(d.Extensions, Contains.Item(".gem"));
      Assert.That(d.MagicSignatures, Is.Empty);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMetadataYamlAndDataPayload() {
    var data = BuildGem();
    using var ms = new MemoryStream(data);
    var entries = new GemFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("metadata.yaml"));
    Assert.That(names, Does.Contain("data/lib/example_gem.rb"));
    Assert.That(names, Does.Contain("data/README.md"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_WritesParsedMetadata() {
    var data = BuildGem();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new GemFormatDescriptor().Extract(ms, tmp, null, null);
      var metaPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(metaPath), Is.True);
      var meta = File.ReadAllText(metaPath);
      Assert.That(meta, Does.Contain("name = example_gem"));
      Assert.That(meta, Does.Contain("version = 0.1.5"));
      Assert.That(meta, Does.Contain("license = MIT"));
      Assert.That(meta, Does.Contain("runtime_dependencies = 2"));
      Assert.That(File.Exists(Path.Combine(tmp, "data", "lib", "example_gem.rb")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_RoundTripsPayloadAndWritesRealChecksums() {
    var library = "module Demo; end\n"u8.ToArray();
    var readme = "# Demo\n"u8.ToArray();
    ArchiveInputInfo[] inputs = [
      ArchiveInputInfo.InMemory("lib/demo.rb", library),
      ArchiveInputInfo.InMemory("README.md", readme),
    ];

    using var output = new MemoryStream();
    var descriptor = new GemFormatDescriptor();
    descriptor.Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var listed = descriptor.List(output, null).Select(entry => entry.Name).ToArray();
    Assert.Multiple(() => {
      Assert.That(listed, Does.Contain("data/lib/demo.rb"));
      Assert.That(listed, Does.Contain("data/README.md"));
      Assert.That(listed, Does.Contain("metadata.yaml"));
      Assert.That(listed, Does.Contain("checksums.yaml"));
    });

    output.Position = 0;
    var outer = ReadOuterMembers(output);
    var checksums = Encoding.UTF8.GetString(Gunzip(outer["checksums.yaml.gz"]));
    Assert.Multiple(() => {
      Assert.That(checksums, Does.Contain(Convert.ToHexStringLower(SHA256.HashData(outer["metadata.gz"]))));
      Assert.That(checksums, Does.Contain(Convert.ToHexStringLower(SHA256.HashData(outer["data.tar.gz"]))));
      Assert.That(checksums, Does.Contain(Convert.ToHexStringLower(SHA512.HashData(outer["metadata.gz"]))));
      Assert.That(checksums, Does.Contain(Convert.ToHexStringLower(SHA512.HashData(outer["data.tar.gz"]))));
    });

    output.Position = 0;
    Assert.That(((IArchiveFormatOperations)descriptor).ExtractEntryToMemory(output, "data/lib/demo.rb", null),
      Is.EqualTo(library).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void List_PlainTar_NoCanonicalLayout_Throws() {
    var bogus = BuildSimpleTar([("hello.txt", "world\n"u8.ToArray())]);
    using var ms = new MemoryStream(bogus);
    Assert.That(() => new GemFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  private static Dictionary<string, byte[]> ReadOuterMembers(Stream stream) {
    var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var reader = new TarReader(stream);
    while (reader.GetNextEntry() is { } entry) {
      if (entry.IsDirectory) {
        reader.Skip();
        continue;
      }
      using var entryStream = reader.GetEntryStream();
      using var copy = new MemoryStream();
      entryStream.CopyTo(copy);
      result[entry.Name] = copy.ToArray();
    }
    return result;
  }

  private static byte[] Gunzip(byte[] data) {
    using var input = new MemoryStream(data);
    using var gzip = new GZipStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream();
    gzip.CopyTo(output);
    return output.ToArray();
  }
}
