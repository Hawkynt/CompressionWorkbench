using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.WinterMuteDcp;

namespace Compression.Tests.WinterMuteDcp;

/// <summary>
/// WinterMute DCP. Oracles: dead:code's own PackMaker (2003, shipped in the WME source tree) wrote the
/// <c>packmaker-*</c> fixtures, the WME 1.10.1 engine (<c>wme.exe</c> from the DevKit) loaded
/// <c>packmaker-v1-engine.dcp</c> and our <c>cwb-engine.dcp</c> as its <c>data.dcp</c> and ran the game
/// script inside (<c>wme-result.txt</c>), and dcp_unpacker read our packages. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class WinterMuteDcpTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"WinterMuteDcpVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded DCP vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Text(string name) => Encoding.UTF8.GetString(Vector(name)).Replace("\r\n", "\n");

  private static readonly byte[] BBin = [.. Enumerable.Range(0, 4096).Select(i => (byte)i)];

  private const string DefaultGame = "GAME {\n  NAME=\"cwb_dcp_check\"\n  CAPTION=\"cwb\"\n  SCRIPT=\"scripts\\game.script\"\n}\n";

  private const string StartupSettings =
    "SETTINGS {\n  GAME=\"default.game\"\n  RESOLUTION { 320,240 }\n  REQUIRE_3D_ACCELERATION=FALSE\n" +
    "  ALLOW_WINDOWED_MODE=TRUE\n  REGISTRY_PATH=\"software\\DEAD:CODE\\cwb_dcp_check\"\n}\n";

  private const string GameScript =
    "var out = new File(\"result.txt\");\n" +
    "out.OpenAsText(2);\n" +
    "\n" +
    "var t = new File(\"sub\\c.txt\");\n" +
    "if(t.OpenAsText(1)) { out.WriteLine(\"sub/c.txt=\" + t.ReadLine()); t.Close(); }\n" +
    "else out.WriteLine(\"sub/c.txt=missing\");\n" +
    "\n" +
    "var b = new File(\"sub\\b.bin\");\n" +
    "if(b.OpenAsBinary(1)) {\n" +
    "  var sum = 0;\n" +
    "  var n = 0;\n" +
    "  for(n = 0; n < b.Length; n = n + 1) sum = (sum * 31 + b.ReadByte()) % 1000003;\n" +
    "  out.WriteLine(\"sub/b.bin length=\" + b.Length + \" sum=\" + sum);\n" +
    "  b.Close();\n" +
    "}\n" +
    "else out.WriteLine(\"sub/b.bin=missing\");\n" +
    "\n" +
    "var m = new File(\"nope.txt\");\n" +
    "if(m.OpenAsText(1)) out.WriteLine(\"nope.txt=present\"); else out.WriteLine(\"nope.txt=missing\");\n" +
    "\n" +
    "out.Close();\n" +
    "Game.QuitGame();\n";

  /// <summary>A game whose script makes the WME engine report what it read from the package.</summary>
  private static IReadOnlyList<ArchiveInputInfo> EngineInputs() => [
    ArchiveInputInfo.InMemory("default.game", Encoding.ASCII.GetBytes(DefaultGame)),
    ArchiveInputInfo.InMemory("scripts/game.script", Encoding.ASCII.GetBytes(GameScript)),
    ArchiveInputInfo.InMemory("startup.settings", Encoding.ASCII.GetBytes(StartupSettings)),
    ArchiveInputInfo.InMemory("sub/b.bin", BBin),
    ArchiveInputInfo.InMemory("sub/c.txt", "deep file\n"u8.ToArray()),
  ];

  /// <summary>What the engine's script reports for <see cref="EngineInputs"/> (sum = (sum*31+byte) mod 1000003).</summary>
  private const string ExpectedEngineResult = "sub/c.txt=deep file\nsub/b.bin length=4096 sum=712247\nnope.txt=missing\n";

  private static byte[] Create(IReadOnlyList<ArchiveInputInfo> inputs, string? method = null, Dictionary<string, string>? specific = null) {
    using var ms = new MemoryStream();
    new WinterMuteDcpFormatDescriptor().Create(ms, inputs, new FormatCreateOptions { MethodName = method, FormatSpecific = specific ?? [] });
    return ms.ToArray();
  }

  private static void DumpIfRequested(string name, byte[] bytes) {
    var dir = Environment.GetEnvironmentVariable("CWB_DUMP_DCP");
    if (!string.IsNullOrEmpty(dir)) File.WriteAllBytes(Path.Combine(dir, name), bytes);
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_PackMakerV1Package_When_Read_Then_HeaderAndFilesMatchItsInputs() {
    var r = new DcpReader(new MemoryStream(Vector("packmaker-v1.dcp")));
    Assert.That(r.Version, Is.EqualTo(DcpReader.Version1));
    Assert.That(r.Description, Is.EqualTo("CWB test package"));
    Assert.That(r.GameVersion, Is.EqualTo(1u));
    Assert.That(r.Directories.Single().Name, Is.EqualTo("data"));
    var entries = r.Entries.ToList();
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "a.txt", "sub/b.bin", "sub/s.wav" }));
    Assert.That(r.Extract(entries[0]), Is.EqualTo("hello dcp\n"u8.ToArray()));
    Assert.That(entries[1].IsCompressed, Is.True);
    Assert.That(r.Extract(entries[1]), Is.EqualTo(BBin));
    Assert.That(entries[2].IsCompressed, Is.False, "PackMaker's Uncompressed filter covered *.wav");
    Assert.That(r.Extract(entries[2]), Is.EqualTo("RIFFxxxxWAVE"u8.ToArray()));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_PackMakerEnginePackage_When_Read_Then_HoldsTheGameTheEngineRan() {
    var r = new DcpReader(new MemoryStream(Vector("packmaker-v1-engine.dcp")));
    var files = r.Entries.ToDictionary(e => e.Name, r.Extract, StringComparer.OrdinalIgnoreCase);
    foreach (var input in EngineInputs())
      Assert.That(files[input.ArchiveName], Is.EqualTo(input.ReadContent()), input.ArchiveName);
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_EngineInputs_When_WeWrite_Then_IdenticalToThePackageWmeRan() {
    var bytes = Create(EngineInputs());
    DumpIfRequested("cwb-engine.dcp", bytes);
    Assert.That(bytes, Is.EqualTo(Vector("cwb-engine.dcp")));
    Assert.That(Text("wme-result.txt"), Is.EqualTo(ExpectedEngineResult));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_OurPackage_When_ReadByDcpUnpacker_Then_SameFiles() {
    var listing = Text("dcp-unpacker-listing.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Split('\t')).ToDictionary(f => f[0], f => f[1]);
    var r = new DcpReader(new MemoryStream(Vector("cwb-engine.dcp")));
    Assert.That(listing.Keys, Is.EquivalentTo(r.Entries.Select(e => e.Name)));
    foreach (var e in r.Entries)
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(r.Extract(e))), Is.EqualTo(listing[e.Name]), e.Name);
  }

  /// <summary>
  /// Runs the real engine when CWB_WME_DIR names a folder holding the DevKit's <c>wme.exe</c>,
  /// <c>dcscomp.dll</c> and a <c>wme.ini</c> that preselects this machine's devices.
  /// </summary>
  [Test, Category("ExternalTool")]
  public void Given_OurPackage_When_WmeRunsIt_Then_EngineReadsStoredAndCompressedFiles() {
    var wme = Environment.GetEnvironmentVariable("CWB_WME_DIR");
    if (string.IsNullOrEmpty(wme) || !File.Exists(Path.Combine(wme, "wme.exe")) || !OperatingSystem.IsWindows())
      Assert.Ignore("Set CWB_WME_DIR to a folder with WME's wme.exe, dcscomp.dll and wme.ini to run the engine check.");
    var dir = Path.Combine(Path.GetTempPath(), "cwb-wme-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      foreach (var f in Directory.GetFiles(wme!))
        File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
      File.WriteAllBytes(Path.Combine(dir, "data.dcp"), Create(EngineInputs()));
      using (var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "wme.exe")) { WorkingDirectory = dir, UseShellExecute = false })!) {
        if (!p.WaitForExit(60_000)) { p.Kill(); Assert.Fail("WME did not exit."); }
      }
      Assert.That(File.ReadAllText(Path.Combine(dir, "result.txt")).Replace("\r\n", "\n"), Is.EqualTo(ExpectedEngineResult));
    } finally {
      try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
  }

  /// <summary>Reads a real game package when CWB_DCP_REAL names one; writes a SHA-256 listing beside CWB_DUMP_DCP.</summary>
  [Test, Category("ExternalTool")]
  public void Given_RealGamePackage_When_Read_Then_EveryFileDecodes() {
    var path = Environment.GetEnvironmentVariable("CWB_DCP_REAL");
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
      Assert.Ignore("Set CWB_DCP_REAL to a WME game's data.dcp to run this check.");
    using var fs = File.OpenRead(path!);
    var r = new DcpReader(fs);
    Assert.That(r.Version, Is.EqualTo(DcpReader.Version2));
    var lines = r.Entries.Select(e => e.Name + "\t" + Convert.ToHexStringLower(SHA256.HashData(r.Extract(e)))).ToList();
    Assert.That(lines, Is.Not.Empty);
    DumpIfRequested("real-listing.txt", Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
  }

  [TestCase(null)]
  [TestCase("store")]
  [Category("RoundTrip")]
  public void Given_Inputs_When_Created_Then_ReadsBackIdentically(string? method) {
    var bytes = Create(EngineInputs(), method, new() { ["name"] = "game", ["description"] = "Main data", ["gameversion"] = "7" });
    var r = new DcpReader(new MemoryStream(bytes));
    Assert.That(r.Version, Is.EqualTo(DcpReader.Version2));
    Assert.That(r.Directories.Single().Name, Is.EqualTo("game"));
    Assert.That(r.Description, Is.EqualTo("Main data"));
    Assert.That(r.GameVersion, Is.EqualTo(7u));
    var inputs = EngineInputs();
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(inputs.Select(i => i.ArchiveName)));
    foreach (var (e, i) in r.Entries.Zip(inputs))
      Assert.That(r.Extract(e), Is.EqualTo(i.ReadContent()), e.Name);
    Assert.That(r.Entries.Any(e => e.IsCompressed), Is.EqualTo(method != "store"));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoFiles_When_Created_Then_EmptyListing()
    => Assert.That(new WinterMuteDcpFormatDescriptor().List(new MemoryStream(Create([])), null), Is.Empty);

  [Test, Category("EdgeCase")]
  public void Given_EmptyFile_When_Created_Then_StoredWithZeroLength() {
    var r = new DcpReader(new MemoryStream(Create([ArchiveInputInfo.InMemory("empty.txt", Array.Empty<byte>())])));
    Assert.That(r.Entries.Single().IsCompressed, Is.False);
    Assert.That(r.Extract(r.Entries.Single()), Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Given_NamesDifferingOnlyInCase_When_Created_Then_Refused()
    => Assert.Throws<ArgumentException>(() => Create([ArchiveInputInfo.InMemory("A.txt", new byte[] { 1 }), ArchiveInputInfo.InMemory("a.txt", new byte[] { 2 })]));

  [Test, Category("EdgeCase")]
  public void Given_NonLatin1Name_When_Created_Then_Refused()
    => Assert.Throws<ArgumentException>(() => Create([ArchiveInputInfo.InMemory("日本.txt", new byte[] { 1 })]));

  [Test, Category("EdgeCase")]
  public void Given_DescriptionOver99Bytes_When_Created_Then_Refused()
    => Assert.Throws<ArgumentException>(() => Create([], specific: new() { ["description"] = new string('x', 100) }));

  [Test, Category("EdgeCase")]
  public void Given_UnknownMethod_When_Created_Then_NotSupported()
    => Assert.Throws<NotSupportedException>(() => Create(EngineInputs(), method: "lzma"));

  [Test, Category("RoundTrip")]
  public void Given_OurPackage_When_AddedAndRemoved_Then_HeaderAndUntouchedFilesSurvive() {
    var original = Create(EngineInputs(), specific: new() { ["description"] = "keep me" });
    var before = new DcpReader(new MemoryStream(original));
    using var ms = new MemoryStream();
    ms.Write(original);
    var d = (IArchiveModifiable)new WinterMuteDcpFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("new.txt", "added"u8.ToArray()), ArchiveInputInfo.InMemory("SUB/C.TXT", "replaced"u8.ToArray())]);
    d.Remove(ms, ["default.game"]);
    ms.Position = 0;
    var r = new DcpReader(ms);
    Assert.That(r.Description, Is.EqualTo("keep me"));
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "scripts/game.script", "startup.settings", "sub/b.bin", "SUB/C.TXT", "new.txt" }));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "SUB/C.TXT")), Is.EqualTo("replaced"u8.ToArray()));
    var bin = r.Entries.Single(e => e.Name == "sub/b.bin");
    Assert.That(r.ReadStored(bin), Is.EqualTo(before.ReadStored(before.Entries.Single(e => e.Name == "sub/b.bin"))));
  }

  [Test, Category("EdgeCase")]
  public void Given_Version1Package_When_Modified_Then_RefusedAndUntouched() {
    var original = Vector("packmaker-v1.dcp");
    using var ms = new MemoryStream();
    ms.Write(original);
    Assert.Throws<NotSupportedException>(() => ((IArchiveModifiable)new WinterMuteDcpFormatDescriptor()).Remove(ms, ["a.txt"]));
    Assert.That(ms.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase")]
  public void Given_MasterIndex_When_Extracted_Then_RefusedButListed() {
    var bytes = Create(EngineInputs());
    bytes[18] = 1; // master-index flag
    var r = new DcpReader(new MemoryStream(bytes));
    Assert.That(r.HoldsItsOwnData, Is.False);
    Assert.That(new WinterMuteDcpFormatDescriptor().List(new MemoryStream(bytes), null), Has.Count.EqualTo(5));
    Assert.Throws<NotSupportedException>(() => r.Extract(r.Entries.First()));
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongMagic_When_Read_Then_InvalidData() {
    var bytes = Create(EngineInputs());
    bytes[4] = (byte)'X';
    Assert.Throws<InvalidDataException>(() => _ = new DcpReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnknownVersion_When_Read_Then_InvalidData() {
    var bytes = Create(EngineInputs());
    bytes[9] = 3;
    Assert.Throws<InvalidDataException>(() => _ = new DcpReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_DirectoryOffsetPastTheEnd_When_Read_Then_InvalidData() {
    var bytes = Create(EngineInputs());
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x80), (uint)bytes.Length + 10);
    Assert.Throws<InvalidDataException>(() => _ = new DcpReader(new MemoryStream(bytes)));
  }

  [Test, Category("EdgeCase")]
  public void Given_DamagedZlibFile_When_Extracted_Then_Throws() {
    var bytes = Create([ArchiveInputInfo.InMemory("b.bin", BBin)]);
    bytes[0x84 + 10] ^= 0xFF;
    var r = new DcpReader(new MemoryStream(bytes));
    Assert.Catch<Exception>(() => r.Extract(r.Entries.Single()));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedPackage_When_Read_Then_Throws()
    => Assert.Catch<Exception>(() => _ = new DcpReader(new MemoryStream(Create(EngineInputs())[..100])));

  [Test, Category("HappyPath")]
  public void Given_DcpBytes_When_DetectedByMagic_Then_WinterMuteDcp()
    => Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Vector("packmaker-v1.dcp")).ToString(), Is.EqualTo("WinterMuteDcp"));
}
