using System.Buffers.Binary;

namespace Compression.Tests.WordPerfect;

[TestFixture]
public class WordPerfectTests {

  private static byte[] BuildWp6Document(uint docOffset = 0x100, int totalSize = 0x200) {
    var buf = new byte[totalSize];
    // Magic \xFFWPC
    buf[0] = 0xFF; buf[1] = 0x57; buf[2] = 0x50; buf[3] = 0x43;
    // Document area pointer (LE)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), docOffset);
    buf[8] = 0x01; // product_type = WordPerfect
    buf[9] = 0x10; // file_type = Document
    buf[10] = 0x06; // major_version = 6
    buf[11] = 0x00; // minor_version
    buf[12] = 0x00; // encryption off
    // Prefix area filler: bytes 16..docOffset-1
    for (var i = 16; i < (int)docOffset && i < buf.Length; i++) buf[i] = (byte)(i & 0xFF);
    // Document area filler: docOffset..end
    for (var i = (int)docOffset; i < buf.Length; i++) buf[i] = 0xDD;
    return buf;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.WordPerfect.WordPerfectFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("WordPerfect"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".wpd"));
    Assert.That(desc.Extensions, Does.Contain(".wpd"));
    Assert.That(desc.Extensions, Does.Contain(".wp5"));
    Assert.That(desc.Extensions, Does.Contain(".wp6"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0xFF, 0x57, 0x50, 0x43 }));
  }

  [Test, Category("HappyPath")]
  public void List_CanonicalEntries() {
    var data = BuildWp6Document();
    using var ms = new MemoryStream(data);
    var desc = new FileFormat.WordPerfect.WordPerfectFormatDescriptor();
    var entries = desc.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.wpd"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("header.bin"));
    Assert.That(names, Does.Contain("prefix_area.bin"));
    Assert.That(names, Does.Contain("document_area.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFilesAndMetadata() {
    var data = BuildWp6Document(docOffset: 0x50, totalSize: 0x100);
    using var ms = new MemoryStream(data);
    var desc = new FileFormat.WordPerfect.WordPerfectFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "wp_test_" + Guid.NewGuid().ToString("N"));
    try {
      desc.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.wpd")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "header.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "prefix_area.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "document_area.bin")), Is.True);

      var header = File.ReadAllBytes(Path.Combine(outDir, "header.bin"));
      Assert.That(header.Length, Is.EqualTo(16));
      Assert.That(header[0], Is.EqualTo(0xFF));
      Assert.That(header[1], Is.EqualTo(0x57));

      var prefix = File.ReadAllBytes(Path.Combine(outDir, "prefix_area.bin"));
      Assert.That(prefix.Length, Is.EqualTo(0x50 - 16));

      var docArea = File.ReadAllBytes(Path.Combine(outDir, "document_area.bin"));
      Assert.That(docArea.Length, Is.EqualTo(0x100 - 0x50));
      Assert.That(docArea[0], Is.EqualTo(0xDD));

      var ini = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(ini, Does.Contain("major_version=6"));
      Assert.That(ini, Does.Contain("product_type=0x01"));
      Assert.That(ini, Does.Contain("file_type=0x10"));
      Assert.That(ini, Does.Contain("encrypted=false"));
      Assert.That(ini, Does.Contain($"document_area_offset=80"));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_BadMagic_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[64]);
    var desc = new FileFormat.WordPerfect.WordPerfectFormatDescriptor();
    List<Compression.Registry.ArchiveEntryInfo>? result = null;
    Assert.DoesNotThrow(() => result = desc.List(ms, null));
    Assert.That(result, Is.Not.Null);
    Assert.That(result, Is.Empty);
  }
}
