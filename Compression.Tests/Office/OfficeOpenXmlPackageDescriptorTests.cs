#pragma warning disable CS1591
using System.IO.Compression;
using Compression.Lib;
using Compression.Registry;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Office;

[TestFixture]
public sealed class OfficeOpenXmlPackageDescriptorTests {

  [SetUp]
  public void SetUp() => FormatRegistration.EnsureInitialized();

  [Test]
  public void Registry_ClaimsAllModernOfficePackageExtensions() {
    var descriptor = FormatRegistry.GetById("OfficeOpenXml");
    Assert.That(descriptor, Is.Not.Null);

    Assert.Multiple(() => {
      Assert.That(descriptor!.Extensions, Does.Contain(".docx"));
      Assert.That(descriptor.Extensions, Does.Contain(".docm"));
      Assert.That(descriptor.Extensions, Does.Contain(".dotx"));
      Assert.That(descriptor.Extensions, Does.Contain(".dotm"));
      Assert.That(descriptor.Extensions, Does.Contain(".xlsx"));
      Assert.That(descriptor.Extensions, Does.Contain(".xlsm"));
      Assert.That(descriptor.Extensions, Does.Contain(".xltx"));
      Assert.That(descriptor.Extensions, Does.Contain(".xltm"));
      Assert.That(descriptor.Extensions, Does.Contain(".pptx"));
      Assert.That(descriptor.Extensions, Does.Contain(".pptm"));
      Assert.That(descriptor.Extensions, Does.Contain(".ppsx"));
      Assert.That(descriptor.Extensions, Does.Contain(".ppsm"));
      Assert.That(descriptor.Extensions, Does.Contain(".potx"));
      Assert.That(descriptor.Extensions, Does.Contain(".potm"));
      Assert.That(descriptor.MagicSignatures, Is.Empty,
        "OPC must not steal arbitrary ZIP files during content-only detection.");
    });
  }

  [Test]
  public void List_PreservesOriginalPackageParts_WithoutFlatteningMultiImageAssets() {
    var animatedGif = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 2, 3, 4 };
    var package = _BuildPackage(("word/media/animated.gif", animatedGif));
    var descriptor = new OfficeOpenXmlPackageDescriptor();

    using var stream = new MemoryStream(package, writable: false);
    var entries = descriptor.List(stream, password: null);
    var names = entries.Select(entry => entry.Name).ToArray();

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("[Content_Types].xml"));
      Assert.That(names, Does.Contain("_rels/.rels"));
      Assert.That(names, Does.Contain("word/document.xml"));
      Assert.That(names, Does.Contain("word/media/animated.gif"));
      Assert.That(names.Count(name => name == "word/media/animated.gif"), Is.EqualTo(1));
      Assert.That(names, Has.None.Contains("frame_"));
      Assert.That(names, Has.None.Contains("page_"));
      Assert.That(names, Has.None.Contains("image_000"));
    });
  }

  [Test]
  public void OpenEntry_ReturnsExactOriginalPartBytes() {
    var original = Enumerable.Range(0, 257).Select(i => (byte)(i * 47)).ToArray();
    var package = _BuildPackage(("ppt/media/multipage.tiff", original));
    var descriptor = new OfficeOpenXmlPackageDescriptor();

    using var archive = new MemoryStream(package, writable: false);
    using var part = descriptor.OpenEntry(archive, "ppt/media/multipage.tiff", password: null);
    using var extracted = new MemoryStream();
    part.CopyTo(extracted);

    Assert.That(extracted.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void List_ExposesNonImagePartsAndObjectPayloadsToo() {
    var package = _BuildPackage(
      ("docProps/thumbnail.jpeg", [0xFF, 0xD8, 0xFF, 0xD9]),
      ("customUI/images/icon.ico", [0, 0, 1, 0, 0, 0]),
      ("word/embeddings/oleObject1.bin", [0xD0, 0xCF, 0x11, 0xE0]),
      ("word/media/background.png", [137, 80, 78, 71]));
    var descriptor = new OfficeOpenXmlPackageDescriptor();

    using var stream = new MemoryStream(package, writable: false);
    var names = descriptor.List(stream, password: null).Select(entry => entry.Name).ToArray();

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("docProps/thumbnail.jpeg"));
      Assert.That(names, Does.Contain("customUI/images/icon.ico"));
      Assert.That(names, Does.Contain("word/embeddings/oleObject1.bin"));
      Assert.That(names, Does.Contain("word/media/background.png"));
      Assert.That(names, Does.Contain("word/document.xml"));
    });
  }

  [Test]
  public void Detector_UsesOfficeExtension_ButKeepsOrdinaryZipAsZip() {
    var package = _BuildPackage();
    var officePath = Path.Combine(Path.GetTempPath(), $"cwb_opc_{Guid.NewGuid():N}.docx");
    var zipPath = Path.Combine(Path.GetTempPath(), $"cwb_opc_{Guid.NewGuid():N}.zip");

    try {
      File.WriteAllBytes(officePath, package);
      File.WriteAllBytes(zipPath, package);

      Assert.Multiple(() => {
        Assert.That(FormatDetector.Detect(officePath).ToString(), Is.EqualTo("OfficeOpenXml"));
        Assert.That(FormatDetector.Detect(zipPath).ToString(), Is.EqualTo("Zip"));
      });
    } finally {
      try { File.Delete(officePath); } catch { /* best effort */ }
      try { File.Delete(zipPath); } catch { /* best effort */ }
    }
  }

  [Test]
  public void NonOpcZip_IsRejectedEvenWhenExplicitlyOpenedAsOffice() {
    byte[] zip;
    using (var memory = new MemoryStream()) {
      using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        _Write(archive, "hello.txt", "not OPC"u8);
      zip = memory.ToArray();
    }

    var descriptor = new OfficeOpenXmlPackageDescriptor();
    using var stream = new MemoryStream(zip, writable: false);
    Assert.Throws<InvalidDataException>(() => descriptor.List(stream, password: null));
  }

  [Test]
  public void StructureValidation_RequiresOpcPlumbingParts() {
    var descriptor = new OfficeOpenXmlPackageDescriptor();
    var package = _BuildPackage();
    using var valid = new MemoryStream(package, writable: false);
    var validResult = descriptor.ValidateStructure(valid);

    byte[] plainZip;
    using (var memory = new MemoryStream()) {
      using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        _Write(archive, "hello.txt", "not OPC"u8);
      plainZip = memory.ToArray();
    }
    using var invalid = new MemoryStream(plainZip, writable: false);
    var invalidResult = descriptor.ValidateStructure(invalid);

    Assert.Multiple(() => {
      Assert.That(validResult.IsValid, Is.True);
      Assert.That(validResult.Confidence, Is.GreaterThanOrEqualTo(0.99));
      Assert.That(invalidResult.IsValid, Is.False);
      Assert.That(invalidResult.Issues.Any(issue => issue.Code == "OPC_REQUIRED_PART_MISSING"), Is.True);
    });
  }

  private static byte[] _BuildPackage(params (string Path, byte[] Data)[] additionalParts) {
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true)) {
      _Write(archive, "[Content_Types].xml", """
        <?xml version="1.0" encoding="UTF-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
        </Types>
        """u8);
      _Write(archive, "_rels/.rels", """
        <?xml version="1.0" encoding="UTF-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
        </Relationships>
        """u8);
      _Write(archive, "word/document.xml", """
        <?xml version="1.0" encoding="UTF-8"?>
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body/></w:document>
        """u8);

      foreach (var (path, data) in additionalParts)
        _Write(archive, path, data);
    }

    return memory.ToArray();
  }

  private static void _Write(ZipArchive archive, string path, ReadOnlySpan<byte> data) {
    var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
    using var stream = entry.Open();
    stream.Write(data);
  }
}
