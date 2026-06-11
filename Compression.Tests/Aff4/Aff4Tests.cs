using System.IO.Compression;
using System.Text;
using FileFormat.Aff4;

namespace Compression.Tests.Aff4;

[TestFixture]
public class Aff4Tests {

  private static byte[] BuildSyntheticAff4() {
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
      AddEntry(zip, "version.txt", "major=1\nminor=0\ntool=cwb-test\n");
      AddEntry(zip, "container.description", "aff4://11111111-2222-3333-4444-555555555555\n");
      AddEntry(zip, "information.turtle",
        "@prefix aff4: <http://aff4.org/Schema#> .\n" +
        "<aff4://stream> aff4:size 1048576 ;\n" +
        "  aff4:chunkSize 32768 ;\n" +
        "  aff4:compressionMethod \"deflate\" .\n");
      AddEntry(zip, "aff4%3A%2F%2Fstream/00000000", "rawdatablock");
    }
    return ms.ToArray();
  }

  private static void AddEntry(ZipArchive zip, string name, string content) {
    var e = zip.CreateEntry(name, CompressionLevel.NoCompression);
    using var s = e.Open();
    var bytes = Encoding.UTF8.GetBytes(content);
    s.Write(bytes);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new Aff4FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Aff4"));
    Assert.That(d.CompoundExtensions, Contains.Item(".aff4"));
    // ZIP-based: no magic so it doesn't steal generic ZIPs.
    Assert.That(d.MagicSignatures, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndMembers() {
    var img = BuildSyntheticAff4();
    var d = new Aff4FormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.aff4"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "version.txt"), Is.True);
    Assert.That(entries.Any(e => e.Name == "information.turtle"), Is.True);
    Assert.That(entries.Any(e => e.Name.EndsWith("00000000")), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalAndMetadataFromTurtle() {
    var img = BuildSyntheticAff4();
    var d = new Aff4FormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "aff4_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);

      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.aff4"));
      Assert.That(full, Is.EqualTo(img));

      Assert.That(File.Exists(Path.Combine(dir, "version.txt")), Is.True);

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("valid=1"));
      Assert.That(meta, Does.Contain("has_version_txt=1"));
      Assert.That(meta, Does.Contain("has_turtle=1"));
      Assert.That(meta, Does.Contain("image_size=1048576"));
      Assert.That(meta, Does.Contain("chunk_size=32768"));
      Assert.That(meta, Does.Contain("compression=deflate"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[128];
    Array.Fill(garbage, (byte)0x77);
    var d = new Aff4FormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "aff4_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.aff4"));
      Assert.That(full, Is.EqualTo(garbage));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
