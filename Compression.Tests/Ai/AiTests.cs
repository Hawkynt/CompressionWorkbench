using System.Text;

namespace Compression.Tests.Ai;

[TestFixture]
public class AiTests {

  private static byte[] BuildPsBasedAi() {
    var sb = new StringBuilder();
    sb.Append("%!PS-Adobe-3.0\n");
    sb.Append("%%Title: Hello.ai\n");
    sb.Append("%%Creator: Adobe Illustrator(TM) 7.0\n");
    sb.Append("%%CreationDate: 4/21/2026\n");
    sb.Append("%%BoundingBox: 0 0 612 792\n");
    sb.Append("%%DocumentFonts: Helvetica\n");
    sb.Append("%AI7_Thumbnail: 128 128 8\n");
    // Two bytes of thumbnail: 0xAB 0xCD
    sb.Append("%AB CD\n");
    sb.Append("%AI7_EndThumbnail\n");
    sb.Append("%%EndComments\n");
    sb.Append("%%EOF\n");
    return Encoding.Latin1.GetBytes(sb.ToString());
  }

  private static byte[] BuildPdfBasedAi() {
    var sb = new StringBuilder();
    sb.Append("%PDF-1.6\n");
    sb.Append("1 0 obj\n");
    sb.Append("<< /Creator (Adobe Illustrator 26.0) /Title (SomeDoc) /Producer (Adobe PDF) >>\n");
    sb.Append("endobj\n");
    sb.Append("%%EOF\n");
    return Encoding.Latin1.GetBytes(sb.ToString());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Ai.AiFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Ai"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".ai"));
    Assert.That(desc.CompoundExtensions, Does.Contain(".ai"));
    // Must not claim generic PDF/PS — no magic, no single extensions.
    Assert.That(desc.Extensions, Is.Empty);
    Assert.That(desc.MagicSignatures, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void List_PostScriptAi_ReturnsCanonicalEntries() {
    using var ms = new MemoryStream(BuildPsBasedAi());
    var desc = new FileFormat.Ai.AiFormatDescriptor();
    var entries = desc.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.ai"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("document.ai"));
    Assert.That(names, Does.Contain("thumbnail.tiff"));
  }

  [Test, Category("HappyPath")]
  public void Extract_PostScriptAi_WritesFilesAndParsesDsc() {
    var data = BuildPsBasedAi();
    using var ms = new MemoryStream(data);
    var desc = new FileFormat.Ai.AiFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "ai_test_" + Guid.NewGuid().ToString("N"));
    try {
      desc.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.ai")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "document.ai")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "thumbnail.tiff")), Is.True);

      var ini = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=ps"));
      Assert.That(ini, Does.Contain("creator=Adobe Illustrator(TM) 7.0"));
      Assert.That(ini, Does.Contain("bounding_box=0 0 612 792"));
      Assert.That(ini, Does.Contain("has_thumbnail=true"));

      var thumb = File.ReadAllBytes(Path.Combine(outDir, "thumbnail.tiff"));
      Assert.That(thumb, Is.EqualTo(new byte[] { 0xAB, 0xCD }));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_PdfBasedAi_DetectsFormatAndScrapesCreator() {
    var data = BuildPdfBasedAi();
    using var ms = new MemoryStream(data);
    var desc = new FileFormat.Ai.AiFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "ai_test_" + Guid.NewGuid().ToString("N"));
    try {
      desc.Extract(ms, outDir, null, null);
      var ini = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=pdf"));
      Assert.That(ini, Does.Contain("creator=Adobe Illustrator 26.0"));
      Assert.That(ini, Does.Contain("has_thumbnail=false"));
      Assert.That(ini, Does.Contain("FileFormat.Pdf"));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_Garbage_DoesNotThrow() {
    using var ms = new MemoryStream([0x00, 0x01, 0x02]);
    var desc = new FileFormat.Ai.AiFormatDescriptor();
    Assert.DoesNotThrow(() => desc.List(ms, null));
  }
}
