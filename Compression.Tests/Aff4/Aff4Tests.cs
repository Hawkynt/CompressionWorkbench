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

  // ── ZIP layer ────────────────────────────────────────────────────────────

  [TestCase("deflate")]
  [TestCase("stored")]
  public void Create_GivenAnyInput_ThenEveryLocalHeaderIsZip64WithDataDescriptor(string method) {
    // AFF4 Standard v1.0 section 5.4: all ZIP headers MUST be ZIP64.
    using var archive = new MemoryStream();
    new Aff4FormatDescriptor().Create(archive, [ArchiveInputInfo.InMemory("a.txt", "abc"u8), ArchiveInputInfo.InMemory("e", ReadOnlySpan<byte>.Empty)],
      new FormatCreateOptions(method));
    var bytes = archive.ToArray();
    archive.Position = 0;
    using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
    Assert.That(zip.Entries, Has.Count.EqualTo(6));
    var headers = 0;
    for (var i = 0; i + 30 <= bytes.Length; ++i) {
      if (BitConverter.ToUInt32(bytes, i) != 0x04034B50) continue;
      ++headers;
      Assert.That(BitConverter.ToUInt16(bytes, i + 4), Is.EqualTo(45), "version needed");
      Assert.That(BitConverter.ToUInt16(bytes, i + 6) & 0x0008, Is.EqualTo(0x0008), "data descriptor flag");
      Assert.That(BitConverter.ToUInt32(bytes, i + 18), Is.EqualTo(uint.MaxValue), "compressed size deferred to ZIP64");
      var nameLength = BitConverter.ToUInt16(bytes, i + 26);
      Assert.That(BitConverter.ToUInt16(bytes, i + 28), Is.EqualTo(20));
      Assert.That(BitConverter.ToUInt16(bytes, i + 30 + nameLength), Is.EqualTo(0x0001), "ZIP64 extra id");
    }
    Assert.That(headers, Is.EqualTo(6));
  }

  [Test]
  public void Create_GivenEmptyFileWithDeflate_ThenMemberHoldsTheEmptyFinalBlock() {
    using var archive = new MemoryStream();
    new Aff4FormatDescriptor().Create(archive, [ArchiveInputInfo.InMemory("empty.bin", ReadOnlySpan<byte>.Empty)], new FormatCreateOptions("deflate"));
    archive.Position = 0;
    using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
    var segment = zip.Entries.Single(e => e.FullName.StartsWith("aff4://", StringComparison.Ordinal));
    Assert.That(segment.Length, Is.Zero);
    Assert.That(segment.CompressedLength, Is.EqualTo(2));
    using var data = segment.Open();
    Assert.That(data.ReadByte(), Is.EqualTo(-1));
  }

  [Test]
  public void Create_GivenNoInputs_ThenVolumeHoldsOnlyMetadata() {
    using var archive = new MemoryStream();
    new Aff4FormatDescriptor().Create(archive, [], new FormatCreateOptions());
    archive.Position = 0;
    var listed = new Aff4FormatDescriptor().List(archive, null).Select(e => e.Name).ToList();
    Assert.That(listed, Is.EquivalentTo(new[] { "FULL.aff4", "metadata.ini", "container.description", "version.txt", "information.turtle", "information.turtle.hashes" }));
  }

  // ── metadata ─────────────────────────────────────────────────────────────

  [Test]
  public void Create_GivenFile_ThenLegacyOriginalFileNameIsWrittenForPyaff4() {
    using var archive = new MemoryStream();
    new Aff4FormatDescriptor().Create(archive, [ArchiveInputInfo.InMemory("dir/a.txt", "x"u8)], new FormatCreateOptions());
    archive.Position = 0;
    using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
    var turtle = ReadEntry(zip, "information.turtle");
    Assert.That(turtle, Does.Contain("aff4:originalPathName \"/dir/a.txt\""));
    Assert.That(turtle, Does.Contain("aff4:originalFileName \"/dir/a.txt\""));
  }

  [TestCase("md5", "MD5", 32)]
  [TestCase("sha1", "SHA1", 40)]
  [TestCase("sha256", "SHA256", 64)]
  [TestCase("SHA-512", "SHA512", 128)]
  public void Create_GivenHashOption_ThenThatLinearHashIsStored(string option, string datatype, int hexLength) {
    using var archive = new MemoryStream();
    new Aff4FormatDescriptor().Create(archive, [ArchiveInputInfo.InMemory("a.txt", "abc"u8)],
      new FormatCreateOptions { FormatSpecific = new(StringComparer.OrdinalIgnoreCase) { ["Hashes"] = option } });
    archive.Position = 0;
    using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
    var match = System.Text.RegularExpressions.Regex.Match(ReadEntry(zip, "information.turtle"), $"\"([0-9a-f]+)\"\\^\\^aff4:{datatype}\\b");
    Assert.That(match.Success, Is.True);
    Assert.That(match.Groups[1].Value, Has.Length.EqualTo(hexLength));
  }

  [Test]
  public void Create_GivenSeveralHashes_ThenAllAreStoredOnEachFile() {
    using var archive = new MemoryStream();
    new Aff4FormatDescriptor().Create(archive, [ArchiveInputInfo.InMemory("a.txt", "abc"u8)],
      new FormatCreateOptions { FormatSpecific = new(StringComparer.OrdinalIgnoreCase) { ["Hashes"] = "md5, sha1,sha256,md5" } });
    archive.Position = 0;
    using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
    var turtle = ReadEntry(zip, "information.turtle");
    Assert.That(turtle, Does.Contain("\"900150983cd24fb0d6963f7d28e17f72\"^^aff4:MD5"));
    Assert.That(turtle, Does.Contain("\"a9993e364706816aba3e25717850c26c9cd0d89d\"^^aff4:SHA1"));
    Assert.That(turtle, Does.Contain("\"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\"^^aff4:SHA256"));
    Assert.That(System.Text.RegularExpressions.Regex.Matches(turtle, "aff4:MD5").Count, Is.EqualTo(1), "duplicates collapse");
  }

  [TestCase("crc32")]
  [TestCase("")]
  [TestCase(" , ")]
  public void Create_GivenUnsupportedOrNoHash_ThenRefused(string option) {
    Assert.Throws<ArgumentException>(() => new Aff4FormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a", "x"u8)],
      new FormatCreateOptions { FormatSpecific = new(StringComparer.OrdinalIgnoreCase) { ["Hashes"] = option } }));
  }

  [Test]
  public void Create_GivenNonAsciiNames_ThenListedBackUnchanged() {
    var name = "Grüße/ネコ 🐈.txt";
    using var archive = new MemoryStream();
    new Aff4FormatDescriptor().Create(archive, [ArchiveInputInfo.InMemory(name, "x"u8)], new FormatCreateOptions());
    archive.Position = 0;
    var listed = new Aff4FormatDescriptor().List(archive, null);
    Assert.That(listed.Select(e => e.Name), Does.Contain(name));
    Assert.That(listed.Where(e => e.IsDirectory).Select(e => e.Name), Does.Contain("Grüße"));
  }

  // ── reader ───────────────────────────────────────────────────────────────

  private static byte[] BuildVolume(string turtle, params (string Name, byte[] Data)[] members) {
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
      AddEntry(zip, "container.description", "aff4://00000000-0000-0000-0000-000000000001");
      AddEntry(zip, "version.txt", "major=1\nminor=1\ntool=cwb-test\n");
      AddEntry(zip, "information.turtle", turtle);
      foreach (var (name, data) in members) {
        var e = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = e.Open();
        s.Write(data);
      }
    }
    return ms.ToArray();
  }

  private static byte[] ExtractOne(byte[] volume, string name) {
    var dir = Path.Combine(Path.GetTempPath(), "aff4_one_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(volume);
      new Aff4FormatDescriptor().Extract(ms, dir, null, [name]);
      return File.ReadAllBytes(Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar)));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  private static (byte[] Bevy, byte[] Index) BuildBevy(IEnumerable<byte[]> storedChunks) {
    using var bevy = new MemoryStream();
    using var index = new MemoryStream();
    foreach (var chunk in storedChunks) {
      index.Write(BitConverter.GetBytes((ulong)bevy.Position));
      index.Write(BitConverter.GetBytes((uint)chunk.Length));
      bevy.Write(chunk);
    }
    return (bevy.ToArray(), index.ToArray());
  }

  private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 % 251))];

  [TestCase("<https://code.google.com/p/lz4/>", false)]
  [TestCase("<https://code.google.com/p/lz4/>", true)]
  [TestCase("<https://tools.ietf.org/html/rfc1951>", false)]
  [TestCase("<https://www.ietf.org/rfc/rfc1950.txt>", false)]
  [TestCase("<http://code.google.com/p/snappy/>", false)]
  [TestCase("<http://aff4.org/Schema#NullCompressor>", false)]
  public void Extract_GivenImageStreamWithMethod_ThenChunksDecodeAndPaddingIsTrimmed(string method, bool sizePrefixed) {
    const int chunkSize = 4096;
    var content = Pattern(chunkSize * 2 + 100);
    var chunks = new List<byte[]>();
    for (var offset = 0; offset < content.Length; offset += chunkSize) {
      var chunk = new byte[chunkSize]; // the final chunk is zero padded (section 3.2)
      content.AsSpan(offset, Math.Min(chunkSize, content.Length - offset)).CopyTo(chunk);
      chunks.Add(method switch {
        _ when method.Contains("lz4") => sizePrefixed
          ? [.. BitConverter.GetBytes(chunkSize), .. Compression.Core.Dictionary.Lz4.Lz4BlockCompressor.Compress(chunk)]
          : Compression.Core.Dictionary.Lz4.Lz4BlockCompressor.Compress(chunk),
        _ when method.Contains("rfc1951") => Deflate(chunk, zlib: false),
        _ when method.Contains("rfc1950") => Deflate(chunk, zlib: true),
        _ when method.Contains("snappy") => Compression.Core.Dictionary.Snappy.SnappyCompressor.Compress(chunk),
        _ => chunk,
      });
    }
    var (bevy0, index0) = BuildBevy(chunks.Take(2));
    var (bevy1, index1) = BuildBevy(chunks.Skip(2));
    var volume = BuildVolume(
      "@prefix aff4: <http://aff4.org/Schema#> .\n@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .\n" +
      "<aff4://00000000-0000-0000-0000-000000000001//data/s.bin> a aff4:FileImage, aff4:Image, aff4:ImageStream ;\n" +
      $"  aff4:chunkSize {chunkSize} ; aff4:chunksInSegment 2 ; aff4:compressionMethod {method} ;\n" +
      $"  aff4:originalFileName \"./data/s.bin\"^^xsd:string ; aff4:size {content.Length} .\n",
      ("/data/s.bin/00000000", bevy0), ("/data/s.bin/00000000.index", index0),
      ("/data/s.bin/00000001", bevy1), ("/data/s.bin/00000001.index", index1));
    Assert.That(ExtractOne(volume, "data/s.bin"), Is.EqualTo(content));
  }

  private static byte[] Deflate(byte[] data, bool zlib) {
    using var ms = new MemoryStream();
    using (Stream z = zlib ? new ZLibStream(ms, CompressionLevel.Optimal, true) : new DeflateStream(ms, CompressionLevel.Optimal, true))
      z.Write(data);
    return ms.ToArray();
  }

  [Test]
  public void Extract_GivenImageStreamMissingABevy_ThenFailsInsteadOfTruncating() {
    var (bevy0, index0) = BuildBevy([new byte[16]]);
    var volume = BuildVolume(
      "@prefix aff4: <http://aff4.org/Schema#> .\n" +
      "<aff4://00000000-0000-0000-0000-000000000001//s.bin> a aff4:FileImage, aff4:ImageStream ;\n" +
      "  aff4:chunkSize 16 ; aff4:chunksInSegment 1 ; aff4:originalFileName \"s.bin\" ; aff4:size 40 .\n",
      ("/s.bin/00000000", bevy0), ("/s.bin/00000000.index", index0));
    Assert.That(() => ExtractOne(volume, "s.bin"), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  public void Extract_GivenInMetadataStream_ThenBase64ContentIsTheFile() {
    // AFF4-L section 6.2 (the draft spells the property both dataStream and dataSteam).
    var volume = BuildVolume(
      "@prefix aff4: <http://aff4.org/Schema#> .\n@prefix aff4l: <https://aff4.org/Schema/2022/#> .\n" +
      "@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .\n" +
      "<aff4://2585b4c0-340c-45e7-ad9e-0f688da77ef8> a aff4:FileImage , aff4:Image , aff4:ContiguousImage ;\n" +
      "  aff4l:originalPathName \"/ads/hello.txt\" ; aff4:size \"11\"^^xsd:long ;\n" +
      "  aff4:dataSteam \"IkhlbGxvIEFEUyI=\"^^xsd:base64Binary .\n");
    Assert.That(Encoding.ASCII.GetString(ExtractOne(volume, "ads/hello.txt")), Is.EqualTo("\"Hello ADS\""));
  }

  [Test]
  public void List_GivenAff4StdEscapedForeignArn_ThenSegmentIsFound() {
    // AFF4 Standard v1.0 section 5.1/5.2: an ARN outside the volume is stored under its escaped prefix.
    var volume = BuildVolume(
      "@prefix aff4: <http://aff4.org/Schema#> .\n" +
      "<aff4://11111111-2222-3333-4444-555555555555/f.txt> a aff4:FileImage, aff4:ZipSegment ; aff4:originalFileName \"/f.txt\" .\n",
      ("aff4%3A%2F%2F11111111-2222-3333-4444-555555555555/f.txt", "foreign"u8.ToArray()));
    using var ms = new MemoryStream(volume);
    var listed = new Aff4FormatDescriptor().List(ms, null);
    Assert.That(listed.Single(e => e.Name == "f.txt").OriginalSize, Is.EqualTo(7));
    Assert.That(listed.Select(e => e.Name), Has.None.StartsWith("aff4%3A"));
  }

  [Test]
  public void List_GivenWindowsDrivePath_ThenDriveBecomesAFolderAsPyaff4Extracts() {
    var volume = BuildVolume(
      "@prefix aff4: <http://aff4.org/Schema#> .\n" +
      "<aff4://9b7f0000-0000-0000-0000-000000000000> a aff4:FileImage, aff4:ZipSegment ; aff4:originalFileName \"C:\\\\Users\\\\x.txt\" .\n",
      ("aff4://9b7f0000-0000-0000-0000-000000000000", "x"u8.ToArray()));
    using var ms = new MemoryStream(volume);
    Assert.That(new Aff4FormatDescriptor().List(ms, null).Select(e => e.Name), Does.Contain("C/Users/x.txt"));
  }

  // ── Turtle ───────────────────────────────────────────────────────────────

  [Test]
  public void Turtle_GivenFullSyntax_ThenTriplesAreRead() {
    var triples = Aff4Turtle.Parse(""""
      # comment
      @prefix ex: <http://e/#> .
      PREFIX p: <http://p/>
      @base <http://base/> .
      <rel> a ex:Thing ;   # trailing comment
        ex:long """line1
      "quoted" line2""" ;
        ex:esc "tab\there\u00FC\U0001F408" , 'single' ;
        ex:lang "hi"@en-GB ;
        ex:num 42 , -1.5 , 1e3 , true ;
        ex:blank [ p:inner "x" ] ;
        ex:list ( 1 2 ) ;
        ex:local p:a\-b.c .
      _:n1 ex:k ex:v .
      """".ReplaceLineEndings("\n"));
    string Obj(string predicate) => triples.First(t => t.Predicate == predicate).Object.Value;
    Assert.That(triples.First(t => t.Predicate == Aff4Turtle.RdfType).Subject.Value, Is.EqualTo("http://base/rel"));
    Assert.That(Obj("http://e/#long"), Is.EqualTo("line1\n\"quoted\" line2"));
    Assert.That(Obj("http://e/#esc"), Is.EqualTo("tab\there\u00FC\U0001F408"));
    Assert.That(triples.Count(t => t.Predicate == "http://e/#esc"), Is.EqualTo(2));
    Assert.That(Obj("http://e/#lang"), Is.EqualTo("hi"));
    Assert.That(triples.Where(t => t.Predicate == "http://e/#num").Select(t => t.Object.Value), Is.EqualTo(new[] { "42", "-1.5", "1e3", "true" }));
    Assert.That(Obj("http://p/inner"), Is.EqualTo("x"));
    Assert.That(Obj("http://e/#local"), Is.EqualTo("http://p/a-b.c"));
    Assert.That(triples.Any(t => t.Subject.Kind == Aff4RdfNodeKind.Blank && t.Predicate == "http://e/#k"), Is.True);
  }

  [Test]
  public void Turtle_GivenMalformedTail_ThenEarlierStatementsSurvive() {
    var triples = Aff4Turtle.Parse("@prefix ex: <http://e/#> .\n<a> ex:p \"ok\" .\n<b> ex:p \"unterminated");
    Assert.That(triples, Has.Count.EqualTo(1));
    Assert.That(triples[0].Object.Value, Is.EqualTo("ok"));
  }

  [Test]
  public void Turtle_GivenUnknownEscape_ThenBackslashIsKept() {
    // The AFF4-L draft's own example: "\GovDocs\000\000785.html".
    var triples = Aff4Turtle.Parse("<a> <http://p> \"\\GovDocs\\000\\000785.html\" .");
    Assert.That(triples.Single().Object.Value, Is.EqualTo("\\GovDocs\\000\\000785.html"));
  }

  private static string ReadEntry(ZipArchive zip, string name) {
    using var source = zip.GetEntry(name)!.Open();
    using var reader = new StreamReader(source, Encoding.UTF8);
    return reader.ReadToEnd();
  }
}
