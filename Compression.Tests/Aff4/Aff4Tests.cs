using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
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

  [TestCase("deflate")]
  [TestCase("stored")]
  [Category("HappyPath")]
  public void Create_ProducesAff4LZipSegmentsAndRoundTrips(string method) {
    var d = new Aff4FormatDescriptor();
    var payload = Encoding.UTF8.GetBytes(new string('A', 4096) + "AFF4 payload");
    var inputs = new[] {
      ArchiveInputInfo.InMemory("evidence/note.txt", payload),
      ArchiveInputInfo.InMemory("empty.bin", ReadOnlySpan<byte>.Empty),
      new ArchiveInputInfo("folder", "evidence/empty", IsDirectory: true),
    };
    using var archive = new MemoryStream();
    d.Create(archive, inputs, new FormatCreateOptions(method) {
      FormatSpecific = new(StringComparer.OrdinalIgnoreCase) { ["Level"] = "9" },
    });
    archive.Position = 0;

    using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true)) {
      Assert.That(ReadEntry(zip, "version.txt"), Does.Contain("major=2\nminor=1"));
      var turtle = ReadEntry(zip, "information.turtle");
      Assert.That(turtle, Does.Contain("aff4:FileImage, aff4:Image, aff4:ZipSegment"));
      Assert.That(turtle, Does.Contain("aff4:originalPathName \"/evidence/note.txt\""));
      Assert.That(turtle, Does.Contain("aff4:SHA256"));
      // Two segments: the payload and the empty file. Pick the payload by its recorded size.
      var streamEntry = zip.Entries.Single(e => e.FullName.StartsWith("aff4://", StringComparison.Ordinal) && e.Length == payload.Length);
      Assert.That(streamEntry.CompressedLength == streamEntry.Length, Is.EqualTo(method == "stored" || payload.Length == 0));
      using var segment = streamEntry.Open();
      using var actual = new MemoryStream();
      segment.CopyTo(actual);
      Assert.That(actual.ToArray(), Is.EqualTo(payload));
      var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(turtle))).ToLowerInvariant();
      Assert.That(ReadEntry(zip, "information.turtle.hashes"), Does.Contain(expectedHash));
    }

    archive.Position = 0;
    var listed = d.List(archive, null);
    Assert.That(listed.Any(e => e.Name == "evidence/note.txt"), Is.True);
    Assert.That(listed.Any(e => e.Name == "evidence/empty" && e.IsDirectory), Is.True);
    var dir = Path.Combine(Path.GetTempPath(), "aff4_create_" + Guid.NewGuid().ToString("N"));
    try {
      archive.Position = 0;
      d.Extract(archive, dir, null, ["evidence/note.txt"]);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "evidence", "note.txt")), Is.EqualTo(payload));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void Create_RejectsUnsupportedMethodsAndEncryption() {
    var d = new Aff4FormatDescriptor();
    var inputs = new[] { ArchiveInputInfo.InMemory("x", [1, 2, 3]) };
    Assert.Throws<ArgumentException>(() => d.Create(new MemoryStream(), inputs, new FormatCreateOptions("snappy")));
    Assert.Throws<NotSupportedException>(() => d.Create(new MemoryStream(), inputs, new FormatCreateOptions { Password = "secret" }));
    Assert.Throws<ArgumentException>(() => d.Create(new MemoryStream(), inputs,
      new FormatCreateOptions { FormatSpecific = new(StringComparer.OrdinalIgnoreCase) { ["Level"] = "invalid" } }));
  }

  [Test]
  public void Create_GivenAnyInput_ThenContainerDescriptionIsFirstAndNamesTheVolume() {
    using var archive = new MemoryStream();
    new Aff4FormatDescriptor().Create(archive, [ArchiveInputInfo.InMemory("a.txt", "x"u8)], new FormatCreateOptions());
    archive.Position = 0;
    using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
    Assert.That(zip.Entries[0].FullName, Is.EqualTo("container.description"));
    var volume = ReadEntry(zip, "container.description");
    Assert.That(volume, Does.Match("^aff4://[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"));
    Assert.That(zip.Comment, Is.EqualTo(volume));
    Assert.That(ReadEntry(zip, "information.turtle"), Does.StartWith($"@prefix : <{volume}> ."));
  }

  [Test]
  public void Create_GivenFolders_ThenFolderArnsAreGuidBasedNotPathBased() {
    using var archive = new MemoryStream();
    new Aff4FormatDescriptor().Create(archive, [ArchiveInputInfo.InMemory("deep/er/a.txt", "x"u8)], new FormatCreateOptions());
    archive.Position = 0;
    using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
    var turtle = ReadEntry(zip, "information.turtle");
    var folderArns = System.Text.RegularExpressions.Regex.Matches(turtle, "<(aff4://[^>]+)> a aff4:Folder").Select(m => m.Groups[1].Value).ToList();
    Assert.That(folderArns, Has.Count.EqualTo(2));
    Assert.That(folderArns, Has.All.Match("^aff4://[0-9a-f-]{36}$"));
    Assert.That(turtle, Does.Contain("aff4:originalPathName \"/deep/er\""));
  }

  [Test]
  public void Create_GivenControlCharacterInName_ThenEscapedWithRawBase64AndReadBack() {
    var name = "odd\tname%.txt";
    using var archive = new MemoryStream();
    new Aff4FormatDescriptor().Create(archive, [ArchiveInputInfo.InMemory(name, "payload"u8)], new FormatCreateOptions());
    archive.Position = 0;
    using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true)) {
      var turtle = ReadEntry(zip, "information.turtle");
      Assert.That(turtle, Does.Contain("aff4:fileName \"odd%09name%25.txt\""));
      Assert.That(turtle, Does.Contain($"aff4:fileNameRaw \"{Convert.ToBase64String(Encoding.UTF8.GetBytes(name))}\"^^xsd:base64Binary"));
    }
    archive.Position = 0;
    Assert.That(new Aff4FormatDescriptor().List(archive, null).Select(e => e.Name), Does.Contain(name));
  }

  [Test]
  public void List_GivenPyaff4StyleOriginalFileName_ThenLogicalNameIsUsed() {
    // AFF4 v1.1 logical images (pyaff4) name files with aff4:originalFileName, and producers write
    // Windows separators unescaped.
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
      AddEntry(zip, "container.description", "aff4://0b9ad2a0-1111-2222-3333-444444444444");
      AddEntry(zip, "version.txt", "major=1\nminor=1\ntool=pyaff4\n");
      AddEntry(zip, "information.turtle",
        "@prefix aff4: <http://aff4.org/Schema#> .\n" +
        "<aff4://5f1ef6b0-aaaa-bbbb-cccc-dddddddddddd> a aff4:FileImage, aff4:Image, aff4:ZipSegment ;\n" +
        "  aff4:originalFileName \"\\GovDocs\\000\\785.html\" ;\n  aff4:size \"4\"^^xsd:long .\n");
      AddEntry(zip, "aff4://5f1ef6b0-aaaa-bbbb-cccc-dddddddddddd", "html");
    }
    ms.Position = 0;
    Assert.That(new Aff4FormatDescriptor().List(ms, null).Select(e => e.Name), Does.Contain("GovDocs/000/785.html"));
  }

  [TestCase("../escape.txt", TestName = "Create_RejectsParentTraversal")]
  [TestCase("a/./b.txt", TestName = "Create_RejectsDotSegment")]
  [TestCase("information.turtle", TestName = "Create_RejectsReservedName")]
  public void Create_GivenUnsafeOrReservedName_ThenRefusedAsArgument(string name) {
    Assert.Throws<ArgumentException>(() => new Aff4FormatDescriptor().Create(new MemoryStream(),
      [ArchiveInputInfo.InMemory(name, "x"u8)], new FormatCreateOptions()));
  }

  [Test]
  public void Create_GivenDuplicatePathsDifferingInCase_ThenRefused() {
    Assert.Throws<ArgumentException>(() => new Aff4FormatDescriptor().Create(new MemoryStream(),
      [ArchiveInputInfo.InMemory("A.txt", "x"u8), ArchiveInputInfo.InMemory("a.TXT", "y"u8)], new FormatCreateOptions()));
  }

  private static string ReadEntry(ZipArchive zip, string name) {
    using var source = zip.GetEntry(name)!.Open();
    using var reader = new StreamReader(source, Encoding.UTF8);
    return reader.ReadToEnd();
  }
}
