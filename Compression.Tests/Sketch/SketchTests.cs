using System.IO.Compression;
using System.Text;

namespace Compression.Tests.Sketch;

[TestFixture]
public class SketchTests {

  private static byte[] BuildMinimalSketch() {
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
      AddText(zip, "document.json", "{\"pages\":[{\"_ref\":\"pages/P1\"}]}");
      AddText(zip, "meta.json", "{\"appVersion\":\"96.1\",\"app\":\"com.bohemiancoding.sketch3\"}");
      AddText(zip, "user.json", "{}");
      AddText(zip, "pages/P1.json", "{\"layers\":[]}");
      AddText(zip, "pages/P2.json", "{\"layers\":[]}");
      AddBinary(zip, "previews/preview.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
      AddBinary(zip, "images/logo.png", [0x89, 0x50, 0x4E, 0x47]);
      AddText(zip, "text-previews/layer.txt", "hello");
    }
    return ms.ToArray();
  }

  private static void AddText(ZipArchive zip, string name, string text) {
    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
    using var s = entry.Open();
    var bytes = Encoding.UTF8.GetBytes(text);
    s.Write(bytes, 0, bytes.Length);
  }

  private static void AddBinary(ZipArchive zip, string name, byte[] data) {
    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
    using var s = entry.Open();
    s.Write(data, 0, data.Length);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Sketch.SketchFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Sketch"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".sketch"));
    Assert.That(desc.CompoundExtensions, Does.Contain(".sketch"));
    // Must not claim generic ZIP — no magic signatures, no .zip extension.
    Assert.That(desc.Extensions, Is.Empty);
    Assert.That(desc.MagicSignatures, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsCanonicalEntries() {
    var data = BuildMinimalSketch();
    using var ms = new MemoryStream(data);
    var desc = new FileFormat.Sketch.SketchFormatDescriptor();
    var entries = desc.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.sketch"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("document.json"));
    Assert.That(names, Does.Contain("meta.json"));
    Assert.That(names, Does.Contain("user.json"));
    Assert.That(names, Does.Contain("pages/P1.json"));
    Assert.That(names, Does.Contain("pages/P2.json"));
    Assert.That(names, Does.Contain("previews/preview.png"));
    Assert.That(names, Does.Contain("images/logo.png"));
    Assert.That(names, Does.Contain("other/text-previews/layer.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesExpectedFiles() {
    var data = BuildMinimalSketch();
    using var ms = new MemoryStream(data);
    var desc = new FileFormat.Sketch.SketchFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "sketch_test_" + Guid.NewGuid().ToString("N"));
    try {
      desc.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.sketch")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "document.json")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "meta.json")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "pages", "P1.json")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "previews", "preview.png")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "images", "logo.png")), Is.True);

      var ini = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(ini, Does.Contain("page_count=2"));
      Assert.That(ini, Does.Contain("has_preview=true"));
      Assert.That(ini, Does.Contain("has_document_json=true"));
      Assert.That(ini, Does.Contain("app_version=96.1"));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_Garbage_DoesNotThrow() {
    using var ms = new MemoryStream([0x00, 0x01, 0x02, 0x03]);
    var desc = new FileFormat.Sketch.SketchFormatDescriptor();
    Assert.DoesNotThrow(() => desc.List(ms, null));
  }
}
