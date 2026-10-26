using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Xp3;

namespace Compression.Tests.Xp3;

/// <summary>
/// KiriKiri XP3. Oracles: the KiriKiri 2 Releaser (krkrrel 2.32 rev. 2) wrote the <c>rel-*</c>
/// fixtures, GARbro 1.5.44 wrote the <c>garbro-*</c> ones and read every fixture (its view is
/// <c>garbro-listing.txt</c>), and the KiriKiri Z 1.4.0 engine ran <c>cwb-engine.xp3</c> as its
/// <c>data.xp3</c> (its output is <c>krkrz-result.txt</c>). See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class Xp3Tests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Xp3Vectors.{name}")
      ?? throw new FileNotFoundException($"Embedded XP3 vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Text(string name) => Encoding.UTF8.GetString(Vector(name)).Replace("\r\n", "\n");

  private static readonly byte[] ATxt = "hello xp3\n"u8.ToArray();
  private static readonly byte[] BBin = [.. Enumerable.Range(0, 4096).Select(i => (byte)i)];
  private static readonly byte[] Umlaut = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("kirikiri ", 50)));

  /// <summary>The Releaser's project folder, in the order it archived it.</summary>
  private static IReadOnlyList<ArchiveInputInfo> ReleaserInputs() => [
    ArchiveInputInfo.InMemory("a.txt", ATxt),
    ArchiveInputInfo.InMemory("one.txt", "z"u8.ToArray()),
    ArchiveInputInfo.InMemory("sub/b.bin", BBin),
    ArchiveInputInfo.InMemory("sub/dup.txt", ATxt),
    ArchiveInputInfo.InMemory("sub/umlaut-ä.txt", Umlaut),
  ];

  private const string StartupTjs =
    "var out = [];\n" +
    "var arc = System.exePath + \"data.xp3>\";\n" +
    "try {\n" +
    "  out.add(\"a.txt=\" + [].load(arc + \"a.txt\").join(\"|\"));\n" +
    "  out.add(\"sub/c.txt=\" + [].load(arc + \"sub/c.txt\").join(\"|\"));\n" +
    "  out.add(\"umlaut=\" + [].load(arc + \"sub/umlaut-ä.txt\").join(\"|\"));\n" +
    "  out.add(\"exists sub/b.bin=\" + Storages.isExistentStorage(arc + \"sub/b.bin\"));\n" +
    "  out.add(\"exists nope.txt=\" + Storages.isExistentStorage(arc + \"nope.txt\"));\n" +
    "} catch (e) {\n" +
    "  out.add(\"error=\" + e.message);\n" +
    "}\n" +
    "out.save(System.exePath + \"result.txt\");\n" +
    "System.exit();\n";

  /// <summary>An archive whose startup.tjs makes the KiriKiri Z engine report what it read.</summary>
  private static IReadOnlyList<ArchiveInputInfo> EngineInputs() => [
    ArchiveInputInfo.InMemory("startup.tjs", Encoding.UTF8.GetBytes(StartupTjs)),
    ArchiveInputInfo.InMemory("a.txt", "hello xp3\nline two\n"u8.ToArray()),
    ArchiveInputInfo.InMemory("sub/c.txt", "deep file\n"u8.ToArray()),
    ArchiveInputInfo.InMemory("sub/b.bin", BBin),
    ArchiveInputInfo.InMemory("sub/umlaut-ä.txt", Umlaut),
  ];

  private static byte[] Create(IReadOnlyList<ArchiveInputInfo> inputs, string? method = null, string? index = null, string? version = null) {
    var specific = new Dictionary<string, string>();
    if (index != null) specific["index"] = index;
    if (version != null) specific["version"] = version;
    using var ms = new MemoryStream();
    new Xp3FormatDescriptor().Create(ms, inputs, new FormatCreateOptions { MethodName = method, FormatSpecific = specific });
    return ms.ToArray();
  }

  /// <summary>GARbro's listing lines of one fixture: name, size, packed|stored, SHA-256.</summary>
  private static List<string[]> GarbroListing(string vector)
    => Text("garbro-listing.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Split('\t'))
      .Where(f => f[0] == vector)
      .Select(f => f[1..])
      .ToList();

  [TestCase("rel-default.xp3")]
  [TestCase("rel-stored-raw.xp3")]
  [TestCase("rel-raw-protect.xp3")]
  [TestCase("garbro-v1.xp3")]
  [TestCase("garbro-v2-zlib.xp3")]
  [TestCase("cwb-v2-zlib.xp3")]
  [TestCase("cwb-v1-raw-stored.xp3")]
  [TestCase("cwb-engine.xp3")]
  [Category("ThirdPartyVector")]
  public void Given_Fixture_When_Listed_Then_MatchesGarbro(string vector) {
    var expected = GarbroListing(vector);
    Assert.That(expected, Is.Not.Empty, "garbro-listing.txt has no lines for " + vector);
    var bytes = Vector(vector);
    var d = new Xp3FormatDescriptor();
    var list = d.List(new MemoryStream(bytes), null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(expected.Select(f => f[0])));

    var r = new Xp3Reader(new MemoryStream(bytes));
    foreach (var (e, f) in r.Entries.Where(e => !e.IsBanner).Zip(expected)) {
      var data = r.Extract(e);
      Assert.That(data, Has.Length.EqualTo(long.Parse(f[1])), e.Name);
      Assert.That(e.OriginalSize != e.ArchivedSize ? "packed" : "stored", Is.EqualTo(f[2]), e.Name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(f[3]), e.Name);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_ReleaserArchive_When_Read_Then_DuplicatesShareOneSegmentAndIndexIsZlib() {
    var r = new Xp3Reader(new MemoryStream(Vector("rel-default.xp3")));
    Assert.That(r.Version, Is.EqualTo(2));
    Assert.That(r.IndexCompressed, Is.True);
    var a = r.Entries.Single(e => e.Name == "a.txt");
    var dup = r.Entries.Single(e => e.Name == "sub/dup.txt");
    Assert.That(dup.Segments[0].Offset, Is.EqualTo(a.Segments[0].Offset));
    Assert.That(r.Extract(dup), Is.EqualTo(ATxt));
    Assert.That(r.Entries.Single(e => e.Name == "sub/umlaut-ä.txt").Adler32, Is.Not.Null);
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_ReleaserProtectedArchive_When_Read_Then_FlagsSetAndBannerHidden() {
    var bytes = Vector("rel-raw-protect.xp3");
    var r = new Xp3Reader(new MemoryStream(bytes));
    Assert.That(r.IndexCompressed, Is.False);
    Assert.That(r.Entries, Has.Count.EqualTo(6));
    Assert.That(r.Entries[0].IsBanner, Is.True);
    Assert.That(r.Entries.Skip(1).All(e => e.IsProtected), Is.True);
    Assert.That(new Xp3FormatDescriptor().List(new MemoryStream(bytes), null), Has.Count.EqualTo(5));
  }

  [TestCase("cwb-v2-zlib.xp3", null, null, null)]
  [TestCase("cwb-v1-raw-stored.xp3", "store", "raw", "1")]
  [Category("ThirdPartyVector")]
  public void Given_ReleaserInputs_When_WeWrite_Then_IdenticalToTheGarbroVerifiedFixture(string vector, string? method, string? index, string? version) {
    var bytes = Create(ReleaserInputs(), method, index, version);
    DumpIfRequested(vector, bytes);
    Assert.That(bytes, Is.EqualTo(Vector(vector)));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_EngineInputs_When_WeWrite_Then_IdenticalToTheArchiveKiriKiriZRan() {
    var bytes = Create(EngineInputs());
    DumpIfRequested("cwb-engine.xp3", bytes);
    Assert.That(bytes, Is.EqualTo(Vector("cwb-engine.xp3")));
    Assert.That(Text("krkrz-result.txt"), Is.EqualTo(ExpectedEngineResult));
  }

  private static readonly string ExpectedEngineResult =
    "a.txt=hello xp3|line two\n" +
    "sub/c.txt=deep file\n" +
    "umlaut=" + string.Concat(Enumerable.Repeat("kirikiri ", 50)) + "\n" +
    "exists sub/b.bin=1\n" +
    "exists nope.txt=0\n";

  /// <summary>Runs the real engine when CWB_KRKRZ names a KiriKiri Z <c>tvpwin32.exe</c>.</summary>
  [Test, Category("ExternalTool")]
  public void Given_OurArchive_When_KiriKiriZRunsIt_Then_EngineReadsEveryMember() {
    var exe = Environment.GetEnvironmentVariable("CWB_KRKRZ");
    if (string.IsNullOrEmpty(exe) || !File.Exists(exe) || !OperatingSystem.IsWindows())
      Assert.Ignore("Set CWB_KRKRZ to KiriKiri Z's tvpwin32.exe to run the engine check.");
    var dir = Path.Combine(Path.GetTempPath(), "cwb-krkrz-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var engine = Path.Combine(dir, "tvpwin32.exe");
      File.Copy(exe!, engine);
      File.WriteAllBytes(Path.Combine(dir, "data.xp3"), Create(EngineInputs()));
      using (var p = Process.Start(new ProcessStartInfo(engine) { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true })!) {
        p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(60_000)) { p.Kill(); Assert.Fail("KiriKiri Z did not exit."); }
      }
      var result = Encoding.Unicode.GetString(File.ReadAllBytes(Path.Combine(dir, "result.txt"))).TrimStart((char)0xFEFF).Replace("\r\n", "\n");
      Assert.That(result, Is.EqualTo(ExpectedEngineResult));
    } finally {
      try { Directory.Delete(dir, true); } catch (IOException) { }
    }
  }

  private static void DumpIfRequested(string name, byte[] bytes) {
    var dir = Environment.GetEnvironmentVariable("CWB_DUMP_XP3");
    if (!string.IsNullOrEmpty(dir)) File.WriteAllBytes(Path.Combine(dir, name), bytes);
  }

  [TestCase(null, null, null)]
  [TestCase("store", "raw", "1")]
  [TestCase("deflate", "raw", "2")]
  [TestCase("store", "zlib", "1")]
  [Category("RoundTrip")]
  public void Given_Options_When_Created_Then_ReadsBackIdentically(string? method, string? index, string? version) {
    var bytes = Create(ReleaserInputs(), method, index, version);
    var r = new Xp3Reader(new MemoryStream(bytes));
    Assert.That(r.Version, Is.EqualTo(version == "1" ? 1 : 2));
    Assert.That(r.IndexCompressed, Is.EqualTo(index != "raw"));
    var inputs = ReleaserInputs();
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(inputs.Select(i => i.ArchiveName)));
    foreach (var (e, i) in r.Entries.Zip(inputs))
      Assert.That(r.Extract(e), Is.EqualTo(i.ReadContent()), e.Name);
    Assert.That(r.Entries.Any(e => e.Segments.Any(s => s.IsCompressed)), Is.EqualTo(method != "store"));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoFiles_When_Created_Then_EmptyListing() {
    var bytes = Create([]);
    Assert.That(new Xp3FormatDescriptor().List(new MemoryStream(bytes), null), Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Given_EmptyMember_When_Created_Then_StoredWithZeroLength() {
    var bytes = Create([ArchiveInputInfo.InMemory("empty.txt", Array.Empty<byte>())]);
    var r = new Xp3Reader(new MemoryStream(bytes));
    Assert.That(r.Entries.Single().Segments.Single().IsCompressed, Is.False);
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Given_IncompressibleMember_When_Created_Then_StoredNotInflated() {
    var noise = new byte[2000];
    new Random(7).NextBytes(noise);
    var r = new Xp3Reader(new MemoryStream(Create([ArchiveInputInfo.InMemory("noise.bin", noise)])));
    Assert.That(r.Entries[0].Segments[0].IsCompressed, Is.False);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(noise));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnknownMethod_When_Created_Then_NotSupported()
    => Assert.Throws<NotSupportedException>(() => Create(ReleaserInputs(), method: "lzma"));

  [Test, Category("EdgeCase")]
  public void Given_BadVersion_When_Created_Then_Argument()
    => Assert.Throws<ArgumentException>(() => Create(ReleaserInputs(), version: "3"));

  [Test, Category("RoundTrip")]
  public void Given_ReleaserArchive_When_AddedAndRemoved_Then_LayoutFlagsAndOtherMembersSurvive() {
    var original = new Xp3Reader(new MemoryStream(Vector("rel-raw-protect.xp3")));
    using var ms = new MemoryStream();
    ms.Write(Vector("rel-raw-protect.xp3"));
    var d = (IArchiveModifiable)new Xp3FormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("new.txt", "added"u8.ToArray()), ArchiveInputInfo.InMemory("a.txt", "replaced"u8.ToArray())]);
    d.Remove(ms, ["one.txt"]);
    ms.Position = 0;
    var r = new Xp3Reader(ms);
    Assert.That(r.Version, Is.EqualTo(original.Version));
    Assert.That(r.IndexCompressed, Is.EqualTo(original.IndexCompressed));
    Assert.That(r.Entries.Select(e => e.Name).Skip(1), Is.EqualTo(new[] { "a.txt", "sub/b.bin", "sub/dup.txt", "sub/umlaut-ä.txt", "new.txt" }));
    Assert.That(r.Entries[0].IsBanner, Is.True);
    Assert.That(r.Entries.Single(e => e.Name == "a.txt").IsProtected, Is.True);
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "a.txt")), Is.EqualTo("replaced"u8.ToArray()));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "sub/dup.txt")), Is.EqualTo(ATxt));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "new.txt")), Is.EqualTo("added"u8.ToArray()));
    // Untouched members keep the Releaser's own compressed bytes.
    var before = original.Entries.Single(e => e.Name == "sub/umlaut-ä.txt");
    var after = r.Entries.Single(e => e.Name == "sub/umlaut-ä.txt");
    Assert.That(r.ReadSegment(after.Segments[0]), Is.EqualTo(original.ReadSegment(before.Segments[0])));
  }

  [Test, Category("EdgeCase")]
  public void Given_IndexWithForeignChunk_When_Modified_Then_RefusedAndUntouched() {
    var original = WithForeignChunk(Create(ReleaserInputs(), index: "raw"));
    Assert.That(new Xp3Reader(new MemoryStream(original)).ForeignChunks, Has.Count.EqualTo(1));
    using var ms = new MemoryStream();
    ms.Write(original);
    var d = (IArchiveModifiable)new Xp3FormatDescriptor();
    Assert.Throws<NotSupportedException>(() => d.Remove(ms, ["a.txt"]));
    Assert.Throws<NotSupportedException>(() => d.Add(ms, [ArchiveInputInfo.InMemory("x", new byte[] { 1 })]));
    Assert.That(ms.ToArray(), Is.EqualTo(original));
  }

  /// <summary>Appends an obfuscated-game style "hnfn" name-table chunk to a raw index.</summary>
  private static byte[] WithForeignChunk(byte[] archive) {
    var indexOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(archive.AsSpan(0x20));
    var size = BinaryPrimitives.ReadInt64LittleEndian(archive.AsSpan(indexOffset + 1));
    byte[] chunk = [.. "hnfn"u8, 10, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 2, 0, (byte)'a', 0, (byte)'b', 0];
    var result = archive.Concat(chunk).ToArray();
    BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(indexOffset + 1), size + chunk.Length);
    return result;
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongMagic_When_Read_Then_InvalidData() {
    var bytes = Vector("rel-default.xp3");
    bytes[2] = (byte)'4';
    Assert.Throws<InvalidDataException>(() => _ = new Xp3Reader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_IndexOffsetPastTheEnd_When_Read_Then_InvalidData() {
    var bytes = Vector("garbro-v1.xp3");
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(11), bytes.Length + 100);
    Assert.Throws<InvalidDataException>(() => _ = new Xp3Reader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_SegmentPastTheEnd_When_Read_Then_InvalidData() {
    var bytes = Create([ArchiveInputInfo.InMemory("a.txt", ATxt)], method: "store", index: "raw");
    // index: flag(1) size(8) "File"(4) size(8) "info"(4) size(8) body(22+10) "segm"(4) size(8) flags(4) offset(8)
    var indexOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0x20));
    var segmentOffsetField = indexOffset + 9 + 12 + 12 + 32 + 12 + 4;
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(segmentOffsetField), bytes.Length);
    Assert.Throws<InvalidDataException>(() => _ = new Xp3Reader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_MemberFailingAdler32_When_Extracted_Then_RefusedNotReturned() {
    var bytes = Create([ArchiveInputInfo.InMemory("a.txt", ATxt)], method: "store");
    bytes[0x28] ^= 0x55; // first data byte, as a game cipher would leave it
    var r = new Xp3Reader(new MemoryStream(bytes));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries[0]));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedArchive_When_Read_Then_Throws()
    => Assert.Catch<Exception>(() => _ = new Xp3Reader(new MemoryStream(Vector("rel-default.xp3")[..30])));

  [Test, Category("HappyPath")]
  public void Given_Xp3Bytes_When_DetectedByMagic_Then_Xp3()
    => Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Vector("rel-default.xp3")).ToString(), Is.EqualTo("Xp3"));
}
