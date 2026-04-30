using SysZipArchive = System.IO.Compression.ZipArchive;
using SysZipArchiveMode = System.IO.Compression.ZipArchiveMode;

namespace Compression.Tests.Vsdx;

[TestFixture]
public class VsdxTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Vsdx.VsdxFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Vsdx"));
    Assert.That(d.DisplayName, Is.EqualTo("Visio Drawing"));
    Assert.That(d.Extensions, Contains.Item(".vsdx"));
    Assert.That(d.Extensions, Contains.Item(".vstx"));
    Assert.That(d.Extensions, Contains.Item(".vssx"));
    Assert.That(d.Extensions, Contains.Item(".vsdm"));
    Assert.That(d.Extensions, Contains.Item(".vstm"));
    Assert.That(d.Extensions, Contains.Item(".vssm"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".vsdx"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("vsdx"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("Visio"));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsDirectories), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Magic_IsEmpty() {
    var d = new FileFormat.Vsdx.VsdxFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Detection_AllExtensions() {
    var fVsdx = Compression.Lib.FormatDetector.DetectByExtension("test.vsdx");
    Assert.That(fVsdx, Is.EqualTo(Compression.Lib.FormatDetector.Format.Vsdx));

    var fVstx = Compression.Lib.FormatDetector.DetectByExtension("test.vstx");
    Assert.That(fVstx, Is.EqualTo(Compression.Lib.FormatDetector.Format.Vsdx));

    var fVssx = Compression.Lib.FormatDetector.DetectByExtension("test.vssx");
    Assert.That(fVssx, Is.EqualTo(Compression.Lib.FormatDetector.Format.Vsdx));

    var fVsdm = Compression.Lib.FormatDetector.DetectByExtension("test.vsdm");
    Assert.That(fVsdm, Is.EqualTo(Compression.Lib.FormatDetector.Format.Vsdx));

    var fVstm = Compression.Lib.FormatDetector.DetectByExtension("test.vstm");
    Assert.That(fVstm, Is.EqualTo(Compression.Lib.FormatDetector.Format.Vsdx));

    var fVssm = Compression.Lib.FormatDetector.DetectByExtension("test.vssm");
    Assert.That(fVssm, Is.EqualTo(Compression.Lib.FormatDetector.Format.Vsdx));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var docContent = "<?xml version=\"1.0\" encoding=\"utf-8\"?><VisioDocument xmlns=\"http://schemas.microsoft.com/office/visio/2012/main\"/>"u8.ToArray();
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, docContent);
      var desc = new FileFormat.Vsdx.VsdxFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmpFile, "visio/document.xml", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;

      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("visio/document.xml"));
      Assert.That(entries[0].OriginalSize, Is.EqualTo(docContent.Length));

      var outDir = Path.Combine(Path.GetTempPath(), "vsdx_rt_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(outDir);
      try {
        ms.Position = 0;
        desc.Extract(ms, outDir, null, null);
        var extracted = File.ReadAllBytes(Path.Combine(outDir, "visio", "document.xml"));
        Assert.That(extracted, Is.EqualTo(docContent));
      } finally {
        Directory.Delete(outDir, recursive: true);
      }
    } finally {
      File.Delete(tmpFile);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MinimalDocument() {
    var contentTypes = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"u8.ToArray();
    var rels = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>"u8.ToArray();
    var document = "<?xml version=\"1.0\" encoding=\"utf-8\"?><VisioDocument xmlns=\"http://schemas.microsoft.com/office/visio/2012/main\"/>"u8.ToArray();
    var pagesRels = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>"u8.ToArray();
    var pages = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Pages xmlns=\"http://schemas.microsoft.com/office/visio/2012/main\"/>"u8.ToArray();

    var tmpDir = Path.Combine(Path.GetTempPath(), "vsdx_min_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var pCt = Path.Combine(tmpDir, "ContentTypes.xml");
      var pRels = Path.Combine(tmpDir, "rels.xml");
      var pDoc = Path.Combine(tmpDir, "document.xml");
      var pPagesRels = Path.Combine(tmpDir, "pages.xml.rels");
      var pPages = Path.Combine(tmpDir, "pages.xml");
      File.WriteAllBytes(pCt, contentTypes);
      File.WriteAllBytes(pRels, rels);
      File.WriteAllBytes(pDoc, document);
      File.WriteAllBytes(pPagesRels, pagesRels);
      File.WriteAllBytes(pPages, pages);

      var desc = new FileFormat.Vsdx.VsdxFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(
        ms,
        [
          new Compression.Registry.ArchiveInputInfo(pCt, "[Content_Types].xml", false),
          new Compression.Registry.ArchiveInputInfo(pRels, "_rels/.rels", false),
          new Compression.Registry.ArchiveInputInfo(pDoc, "visio/document.xml", false),
          new Compression.Registry.ArchiveInputInfo(pPagesRels, "visio/pages/_rels/pages.xml.rels", false),
          new Compression.Registry.ArchiveInputInfo(pPages, "visio/pages/pages.xml", false)
        ],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;

      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(5));
      var names = entries.Select(e => e.Name).ToList();
      Assert.That(names, Contains.Item("[Content_Types].xml"));
      Assert.That(names, Contains.Item("_rels/.rels"));
      Assert.That(names, Contains.Item("visio/document.xml"));
      Assert.That(names, Contains.Item("visio/pages/_rels/pages.xml.rels"));
      Assert.That(names, Contains.Item("visio/pages/pages.xml"));
    } finally {
      Directory.Delete(tmpDir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesRealZip() {
    var entry1 = "<?xml version=\"1.0\"?><Types/>"u8.ToArray();
    var entry2 = "<VisioDocument/>"u8.ToArray();
    var entry3 = "<Pages/>"u8.ToArray();

    byte[] zipBytes;
    using (var ms = new MemoryStream()) {
      using (var sysZip = new SysZipArchive(ms, SysZipArchiveMode.Create, leaveOpen: true)) {
        var e1 = sysZip.CreateEntry("[Content_Types].xml");
        using (var s = e1.Open()) s.Write(entry1, 0, entry1.Length);
        var e2 = sysZip.CreateEntry("visio/document.xml");
        using (var s = e2.Open()) s.Write(entry2, 0, entry2.Length);
        var e3 = sysZip.CreateEntry("visio/pages/pages.xml");
        using (var s = e3.Open()) s.Write(entry3, 0, entry3.Length);
      }
      zipBytes = ms.ToArray();
    }

    var tmpFile = Path.Combine(Path.GetTempPath(), "synth_" + Guid.NewGuid().ToString("N") + ".vsdx");
    File.WriteAllBytes(tmpFile, zipBytes);
    try {
      var desc = new FileFormat.Vsdx.VsdxFormatDescriptor();
      using var fs = File.OpenRead(tmpFile);
      var entries = desc.List(fs, null);
      Assert.That(entries, Has.Count.EqualTo(3));
      var names = entries.Select(e => e.Name).ToList();
      Assert.That(names, Contains.Item("[Content_Types].xml"));
      Assert.That(names, Contains.Item("visio/document.xml"));
      Assert.That(names, Contains.Item("visio/pages/pages.xml"));
    } finally {
      File.Delete(tmpFile);
    }
  }
}
