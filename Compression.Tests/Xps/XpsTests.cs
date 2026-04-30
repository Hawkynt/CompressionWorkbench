using SysZipArchive = System.IO.Compression.ZipArchive;
using SysZipArchiveMode = System.IO.Compression.ZipArchiveMode;

namespace Compression.Tests.Xps;

[TestFixture]
public class XpsTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Xps.XpsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Xps"));
    Assert.That(d.DisplayName, Is.EqualTo("XPS / OpenXPS"));
    Assert.That(d.Extensions, Contains.Item(".xps"));
    Assert.That(d.Extensions, Contains.Item(".oxps"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".xps"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("xps"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("XPS"));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Magic_IsEmpty() {
    var d = new FileFormat.Xps.XpsFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Detection_ByExtension() {
    var fXps = Compression.Lib.FormatDetector.DetectByExtension("test.xps");
    Assert.That(fXps, Is.EqualTo(Compression.Lib.FormatDetector.Format.Xps));

    var fOxps = Compression.Lib.FormatDetector.DetectByExtension("test.oxps");
    Assert.That(fOxps, Is.EqualTo(Compression.Lib.FormatDetector.Format.Xps));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var pageContent = "<FixedPage xmlns=\"http://schemas.microsoft.com/xps/2005/06\" Width=\"816\" Height=\"1056\"/>"u8.ToArray();
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, pageContent);
      var desc = new FileFormat.Xps.XpsFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmpFile, "Documents/1/Pages/1.fpage", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;

      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("Documents/1/Pages/1.fpage"));
      Assert.That(entries[0].OriginalSize, Is.EqualTo(pageContent.Length));

      var outDir = Path.Combine(Path.GetTempPath(), "xps_rt_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(outDir);
      try {
        ms.Position = 0;
        desc.Extract(ms, outDir, null, null);
        var extracted = File.ReadAllBytes(Path.Combine(outDir, "Documents", "1", "Pages", "1.fpage"));
        Assert.That(extracted, Is.EqualTo(pageContent));
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
    var fdseq = "<FixedDocumentSequence xmlns=\"http://schemas.microsoft.com/xps/2005/06\"/>"u8.ToArray();
    var fdoc = "<FixedDocument xmlns=\"http://schemas.microsoft.com/xps/2005/06\"/>"u8.ToArray();
    var fpage = "<FixedPage xmlns=\"http://schemas.microsoft.com/xps/2005/06\" Width=\"816\" Height=\"1056\"/>"u8.ToArray();

    var tmpDir = Path.Combine(Path.GetTempPath(), "xps_min_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var pCt = Path.Combine(tmpDir, "ContentTypes.xml");
      var pRels = Path.Combine(tmpDir, "rels.xml");
      var pSeq = Path.Combine(tmpDir, "FixedDocumentSequence.fdseq");
      var pDoc = Path.Combine(tmpDir, "FixedDocument.fdoc");
      var pPg = Path.Combine(tmpDir, "1.fpage");
      File.WriteAllBytes(pCt, contentTypes);
      File.WriteAllBytes(pRels, rels);
      File.WriteAllBytes(pSeq, fdseq);
      File.WriteAllBytes(pDoc, fdoc);
      File.WriteAllBytes(pPg, fpage);

      var desc = new FileFormat.Xps.XpsFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(
        ms,
        [
          new Compression.Registry.ArchiveInputInfo(pCt, "[Content_Types].xml", false),
          new Compression.Registry.ArchiveInputInfo(pRels, "_rels/.rels", false),
          new Compression.Registry.ArchiveInputInfo(pSeq, "FixedDocumentSequence.fdseq", false),
          new Compression.Registry.ArchiveInputInfo(pDoc, "Documents/1/FixedDocument.fdoc", false),
          new Compression.Registry.ArchiveInputInfo(pPg, "Documents/1/Pages/1.fpage", false)
        ],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;

      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(5));
      var names = entries.Select(e => e.Name).ToList();
      Assert.That(names, Contains.Item("[Content_Types].xml"));
      Assert.That(names, Contains.Item("_rels/.rels"));
      Assert.That(names, Contains.Item("FixedDocumentSequence.fdseq"));
      Assert.That(names, Contains.Item("Documents/1/FixedDocument.fdoc"));
      Assert.That(names, Contains.Item("Documents/1/Pages/1.fpage"));
    } finally {
      Directory.Delete(tmpDir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesRealZip() {
    var entry1 = "<?xml version=\"1.0\"?><Types/>"u8.ToArray();
    var entry2 = "page-1-content"u8.ToArray();
    var entry3 = "page-2-content"u8.ToArray();

    byte[] zipBytes;
    using (var ms = new MemoryStream()) {
      using (var sysZip = new SysZipArchive(ms, SysZipArchiveMode.Create, leaveOpen: true)) {
        var e1 = sysZip.CreateEntry("[Content_Types].xml");
        using (var s = e1.Open()) s.Write(entry1, 0, entry1.Length);
        var e2 = sysZip.CreateEntry("Documents/1/Pages/1.fpage");
        using (var s = e2.Open()) s.Write(entry2, 0, entry2.Length);
        var e3 = sysZip.CreateEntry("Documents/1/Pages/2.fpage");
        using (var s = e3.Open()) s.Write(entry3, 0, entry3.Length);
      }
      zipBytes = ms.ToArray();
    }

    var tmpFile = Path.Combine(Path.GetTempPath(), "synth_" + Guid.NewGuid().ToString("N") + ".xps");
    File.WriteAllBytes(tmpFile, zipBytes);
    try {
      var desc = new FileFormat.Xps.XpsFormatDescriptor();
      using var fs = File.OpenRead(tmpFile);
      var entries = desc.List(fs, null);
      Assert.That(entries, Has.Count.EqualTo(3));
      var names = entries.Select(e => e.Name).ToList();
      Assert.That(names, Contains.Item("[Content_Types].xml"));
      Assert.That(names, Contains.Item("Documents/1/Pages/1.fpage"));
      Assert.That(names, Contains.Item("Documents/1/Pages/2.fpage"));
    } finally {
      File.Delete(tmpFile);
    }
  }
}
