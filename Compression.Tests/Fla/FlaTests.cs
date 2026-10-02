#pragma warning disable CS1591
using System.IO.Compression;
using System.Text;
using Compression.Registry;
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

  [TestCase(null, null)]
  [TestCase("stored", null)]
  [TestCase("deflate", 0)]
  [TestCase("deflate", 1)]
  [TestCase("deflate", 2)]
  [TestCase("deflate", 3)]
  [TestCase("deflate", 4)]
  [TestCase("deflate", 5)]
  [TestCase("deflate", 6)]
  [TestCase("deflate", 7)]
  [TestCase("deflate", 8)]
  [TestCase("deflate", 9)]
  public void Create_XflRoundTripsEverySupportedMethodAndLevel(string? method, int? level) {
    var descriptor = new FlaFormatDescriptor();
    var timestamp = new DateTime(2022, 4, 5, 6, 7, 8, DateTimeKind.Utc);
    var root = Path.Combine(Path.GetTempPath(), "fla_create_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try {
      var documentPath = Path.Combine(root, "DOMDocument.xml");
      var assetPath = Path.Combine(root, "asset.bin");
      File.WriteAllText(documentPath, "<DOMDocument/>", Encoding.UTF8);
      File.WriteAllBytes(assetPath, [0, 1, 2, 3, 0xFE, 0xFF]);
      File.SetLastWriteTimeUtc(documentPath, timestamp);
      File.SetLastWriteTimeUtc(assetPath, timestamp);
      var inputs = new[] {
        ArchiveInputInfo.FromFile(new FileInfo(documentPath)),
        ArchiveInputInfo.FromFile(new FileInfo(assetPath), "bin/asset.bin"),
      };
      using var archive = new MemoryStream();
      descriptor.Create(archive, inputs, new FormatCreateOptions(method) { Level = level });
      var bytes = archive.ToArray();
      Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo(new byte[] { 0x50, 0x4B, 0x03, 0x04 }));

      archive.Position = 0;
      var listed = descriptor.List(archive, null);
      Assert.That(listed.Select(e => e.Name), Does.Contain("DOMDocument.xml"));
      Assert.That(listed.Select(e => e.Name), Does.Contain("bin/asset.bin"));
      Assert.That(listed.Single(e => e.Name == "bin/asset.bin").Method,
        Is.EqualTo(string.Equals(method, "stored", StringComparison.OrdinalIgnoreCase) ? "stored" : "deflate"));
      Assert.That(listed.Single(e => e.Name == "bin/asset.bin").LastModified, Is.EqualTo(File.GetLastWriteTime(assetPath)));

      var extracted = Path.Combine(root, "out");
      archive.Position = 0;
      descriptor.Extract(archive, extracted, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(extracted, "bin", "asset.bin")), Is.EqualTo(new byte[] { 0, 1, 2, 3, 0xFE, 0xFF }));
      Assert.That(File.GetLastWriteTimeUtc(Path.Combine(extracted, "bin", "asset.bin")), Is.EqualTo(timestamp));
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [Test]
  public void Create_RejectsProjectsWithoutRootDocument() {
    using var output = new MemoryStream();
    var inputs = new[] { ArchiveInputInfo.InMemory("LIBRARY/symbol.xml", "<symbol/>"u8) };
    Assert.Throws<ArgumentException>(() => new FlaFormatDescriptor().Create(output, inputs, new FormatCreateOptions()));
  }

  [TestCase(null)]
  [TestCase("deflate")]
  [TestCase("stored")]
  public void Create_GivenProjectWithoutMimetype_WhenWritten_ThenStoredMimetypeIsLastMemberLikeFlashWritesIt(string? method) {
    var inputs = new[] {
      ArchiveInputInfo.InMemory("LIBRARY/symbol.xml", "<symbol/>"u8),
      ArchiveInputInfo.InMemory("DOMDocument.xml", "<DOMDocument/>"u8),
    };
    using var output = new MemoryStream();
    new FlaFormatDescriptor().Create(output, inputs, new FormatCreateOptions(method));

    output.Position = 0;
    using var zip = new ZipArchive(output, ZipArchiveMode.Read);
    Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "LIBRARY/symbol.xml", "DOMDocument.xml", "mimetype" }));
    var mimetype = zip.Entries[^1];
    Assert.That(mimetype.CompressedLength, Is.EqualTo(25), "mimetype must be stored");
    using (var s = mimetype.Open())
    using (var r = new StreamReader(s))
      Assert.That(r.ReadToEnd(), Is.EqualTo("application/vnd.adobe.xfl"));
    // Raw local header of the last member: method 0 (stored), name 'mimetype', no extra field.
    var bytes = output.ToArray();
    var header = bytes.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4B, 0x03, 0x04 });
    Assert.That(BitConverter.ToUInt16(bytes, header + 8), Is.Zero);
    Assert.That(BitConverter.ToUInt16(bytes, header + 28), Is.Zero);
    Assert.That(Encoding.ASCII.GetString(bytes, header + 30, 8), Is.EqualTo("mimetype"));
  }

  [Test]
  public void Create_GivenMatchingMimetypeInput_WhenWritten_ThenItIsNotDuplicated() {
    var inputs = new[] {
      ArchiveInputInfo.InMemory("mimetype", "application/vnd.adobe.xfl"u8),
      ArchiveInputInfo.InMemory("DOMDocument.xml", "<DOMDocument/>"u8),
    };
    using var output = new MemoryStream();
    new FlaFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    output.Position = 0;
    using var zip = new ZipArchive(output, ZipArchiveMode.Read);
    Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "DOMDocument.xml", "mimetype" }));
  }

  [Test]
  public void Create_GivenFolderInputs_WhenWritten_ThenEachBecomesAStoredFolderEntry() {
    var inputs = new[] {
      new ArchiveInputInfo("LIBRARY", "LIBRARY", IsDirectory: true),
      new ArchiveInputInfo("META-INF/", "META-INF/", IsDirectory: true),
      ArchiveInputInfo.InMemory("DOMDocument.xml", "<DOMDocument/>"u8),
    };
    using var output = new MemoryStream();
    new FlaFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    output.Position = 0;
    using var zip = new ZipArchive(output, ZipArchiveMode.Read);
    Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "LIBRARY/", "META-INF/", "DOMDocument.xml", "mimetype" }));
    Assert.That(zip.Entries.Take(2).Select(e => e.Length), Is.All.Zero);
  }

  [Test]
  public void Create_GivenOnlyAFolderNamedLikeTheDocument_WhenWritten_ThenRefused() {
    var inputs = new[] { new ArchiveInputInfo("DOMDocument.xml", "DOMDocument.xml", IsDirectory: true) };
    Assert.Throws<ArgumentException>(() => new FlaFormatDescriptor().Create(new MemoryStream(), inputs, new FormatCreateOptions()));
  }

  [TestCase("application/zip")]
  [TestCase("application/vnd.adobe.xfl\n")]
  [TestCase("")]
  public void Create_GivenForeignMimetypeInput_WhenWritten_ThenRefused(string content) {
    var inputs = new[] {
      ArchiveInputInfo.InMemory("DOMDocument.xml", "<DOMDocument/>"u8),
      ArchiveInputInfo.InMemory("mimetype", Encoding.ASCII.GetBytes(content)),
    };
    Assert.Throws<ArgumentException>(() => new FlaFormatDescriptor().Create(new MemoryStream(), inputs, new FormatCreateOptions()));
  }

  [Test]
  public void Create_RejectsUnsupportedParameters() {
    var inputs = new[] { ArchiveInputInfo.InMemory("DOMDocument.xml", "<DOMDocument/>"u8) };
    Assert.Throws<NotSupportedException>(() => new FlaFormatDescriptor().Create(new MemoryStream(), inputs, new FormatCreateOptions("bzip2")));
    Assert.Throws<NotSupportedException>(() => new FlaFormatDescriptor().Create(new MemoryStream(), inputs, new FormatCreateOptions { Password = "secret" }));
    Assert.Throws<ArgumentOutOfRangeException>(() => new FlaFormatDescriptor().Create(new MemoryStream(), inputs, new FormatCreateOptions("deflate") { Level = 10 }));
    Assert.Throws<ArgumentOutOfRangeException>(() => new FlaFormatDescriptor().Create(new MemoryStream(), inputs, new FormatCreateOptions("deflate") { Level = -1 }));
    Assert.Throws<ArgumentException>(() => new FlaFormatDescriptor().Create(new MemoryStream(), inputs, new FormatCreateOptions("stored") { Level = 1 }));
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
