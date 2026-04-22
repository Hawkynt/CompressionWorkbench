#pragma warning disable CS1591
using System.IO.Compression;
using System.Text;
using FileFormat.Fla;

namespace Compression.Tests.Fla;

[TestFixture]
public class FlaTests {

  /// <summary>
  /// Build a minimal CFB container with a "DOMDocument.xml" stream.
  /// Reuses the public CfbWriter from FileFormat.Msi so the byte layout
  /// is a structurally valid OLE2 file.
  /// </summary>
  private static byte[] MakeCfbFla() {
    var w = new FileFormat.Msi.CfbWriter();
    w.AddStream("DOMDocument.xml", "<DOMDocument/>"u8.ToArray());
    w.AddStream("Contents", "ContentsBody"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Build a minimal ZIP-based XFL container with a document.xml and a bin/ entry.
  /// </summary>
  private static byte[] MakeXflFla() {
    using var ms = new MemoryStream();
    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
      var doc = archive.CreateEntry("document.xml");
      using (var s = doc.Open())
        s.Write("<DOMDocument/>"u8);
      var bin = archive.CreateEntry("bin/stub.dat");
      using (var s = bin.Open())
        s.Write(new byte[] { 0x01, 0x02, 0x03, 0x04 });
      var lib = archive.CreateEntry("LIBRARY/symbol.xml");
      using (var s = lib.Open())
        s.Write("<symbol/>"u8);
    }
    return ms.ToArray();
  }

  [Test]
  public void CfbVariant_SurfacesStreamsAndMetadata() {
    var data = MakeCfbFla();
    using var ms = new MemoryStream(data);
    var entries = new FlaFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Contains.Item("FULL.fla"));
    Assert.That(names, Contains.Item("metadata.ini"));
    Assert.That(names.Any(n => n.StartsWith("streams/") && n.Contains("DOMDocument.xml")), Is.True,
      "expected a streams/DOMDocument.xml.bin entry, got: " + string.Join(", ", names));
  }

  [Test]
  public void CfbVariant_ExtractWritesFiles() {
    var data = MakeCfbFla();
    var tmp = Path.Combine(Path.GetTempPath(), "fla_cfb_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new FlaFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.fla")), Is.True);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=cfb"));
      Assert.That(ini, Does.Contain("cfb_stream_count="));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void XflVariant_ListsZipEntries() {
    var data = MakeXflFla();
    using var ms = new MemoryStream(data);
    var entries = new FlaFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Contains.Item("FULL.fla"));
    Assert.That(names, Contains.Item("metadata.ini"));
    Assert.That(names, Contains.Item("document.xml"));
    Assert.That(names, Contains.Item("bin/stub.dat"));
    Assert.That(names, Contains.Item("LIBRARY/symbol.xml"));
  }

  [Test]
  public void XflVariant_ExtractWritesFiles() {
    var data = MakeXflFla();
    var tmp = Path.Combine(Path.GetTempPath(), "fla_xfl_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new FlaFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.fla")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "document.xml")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "bin/stub.dat")), Is.True);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=xfl"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void Descriptor_UsesCompoundExtensionOnly() {
    var d = new FlaFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Fla"));
    Assert.That(d.Extensions, Is.Empty, "must not claim single extensions (conflicts with OLE2/ZIP)");
    Assert.That(d.CompoundExtensions, Contains.Item(".fla"));
    Assert.That(d.MagicSignatures, Is.Empty, "must not claim magic bytes (conflicts with OLE2/ZIP)");
  }

  [Test]
  public void List_DoesNotThrowOnGarbage() {
    var junk = Encoding.ASCII.GetBytes("not a fla file at all; no magic here");
    using var ms = new MemoryStream(junk);
    Assert.DoesNotThrow(() => new FlaFormatDescriptor().List(ms, null));
  }
}
