using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Compression.Analysis.ExternalTools;
using Compression.Registry;
using FileFormat.Nsis;
using FileFormat.PeResources;

namespace Compression.Tests.Nsis;

/// <summary>
/// NSIS installer creation. Without NSIS a synthetic stub checks the layout against our reader;
/// with NSIS 3's stubs (an installed NSIS, NSISDIR or CWB_NSIS_DIR) the installers are checked by
/// 7-Zip and, on Windows, by running them silently and comparing what they install.
/// </summary>
[TestFixture]
public class NsisInstallerWriterTests {

  private static readonly (NsisCompression Compression, bool Solid)[] Variants = [
    (NsisCompression.Zlib, false), (NsisCompression.Zlib, true),
    (NsisCompression.BZip2, false), (NsisCompression.BZip2, true),
    (NsisCompression.Lzma, false), (NsisCompression.Lzma, true),
  ];

  private static IEnumerable<TestCaseData> AllVariants()
    => Variants.Select(v => new TestCaseData(v.Compression, v.Solid).SetArgDisplayNames($"{v.Compression}{(v.Solid ? "-solid" : "")}"));

  private string _tmp = null!;

  [SetUp]
  public void SetUp() => Directory.CreateDirectory(this._tmp = Path.Combine(Path.GetTempPath(), "cwb-nsisw-" + Guid.NewGuid().ToString("N")[..8]));

  [TearDown]
  public void TearDown() {
    try {
      Directory.Delete(this._tmp, recursive: true);
    } catch (IOException) {
    } catch (UnauthorizedAccessException) {
    }
  }

  /// <summary>Text, an empty file, incompressible bytes (stored blocks), a run, and nested and non-ASCII names.</summary>
  private static List<NsisInstallerFile> Payload() {
    var noise = new byte[70_000];
    new Random(42).NextBytes(noise);
    var time = new DateTime(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);
    return [
      new("readme.txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("NSIS writer payload line\r\n", 200))), time),
      new("empty.dat", [], time),
      new("bin/noise.bin", noise, time),
      new("bin/deep/runs.bin", Enumerable.Repeat((byte)0x5A, 300_000).ToArray(), time),
      new("docs/Grüße ü.txt", Encoding.UTF8.GetBytes("unicode name"), time),
    ];
  }

  private static byte[] Build(byte[] stub, NsisCompression compression, bool solid, IReadOnlyList<NsisInstallerFile>? files = null) {
    using var ms = new MemoryStream();
    NsisInstallerWriter.Write(ms, stub, files ?? Payload(), new NsisInstallerOptions { Compression = compression, Solid = solid, Name = "CWB Test" });
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> ReadBack(byte[] installer) {
    using var reader = new NsisReader(new MemoryStream(installer), leaveOpen: false);
    var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    reader.ExtractMany(reader.Entries.ToList(), (e, data) => result[e.FileName] = data);
    return result;
  }

  private static void AssertSameFiles(IReadOnlyDictionary<string, byte[]> actual, string because) {
    var expected = Payload().ToDictionary(f => f.Path, f => f.Data);
    Assert.That(actual.Keys.Order(), Is.EqualTo(expected.Keys.Order()), because);
    foreach (var (name, data) in expected)
      Assert.That(actual[name], Is.EqualTo(data), $"{because}: {name}");
  }

  // ── synthetic stub: layout and reader, no NSIS needed ───────────────────────────────────

  /// <summary>A PE32 with .text (naming CreateDirectoryW), .ndata and a .rsrc holding dialog 106, like an exehead.</summary>
  internal static byte[] SyntheticStub(bool unicode = true) {
    var pe = new byte[0x600];
    var s = pe.AsSpan();
    s[0] = (byte)'M';
    s[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(s[0x3C..], 0x80);
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x80..], 0x00004550);
    BinaryPrimitives.WriteUInt16LittleEndian(s[0x84..], 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(s[0x86..], 3);
    BinaryPrimitives.WriteUInt16LittleEndian(s[0x94..], 0xE0);
    var opt = 0x98;
    BinaryPrimitives.WriteUInt16LittleEndian(s[opt..], 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 32)..], 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 36)..], 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 56)..], 0x4000);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 60)..], 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 92)..], 16);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 96 + 16)..], 0x3000);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 96 + 20)..], 0x40);
    var table = opt + 0xE0;
    void Section(int i, string name, uint vs, uint va, uint rs, uint rp, uint ch) {
      var h = table + 40 * i;
      Encoding.ASCII.GetBytes(name).CopyTo(pe.AsSpan(h));
      BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(h + 8), vs);
      BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(h + 12), va);
      BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(h + 16), rs);
      BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(h + 20), rp);
      BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(h + 36), ch);
    }
    Section(0, ".text", 0x100, 0x1000, 0x200, 0x200, 0x60000020);
    Section(1, ".ndata", 0x800, 0x2000, 0, 0, 0xC2000080);
    Section(2, ".rsrc", 0x40, 0x3000, 0x200, 0x400, 0x40000040);
    Encoding.ASCII.GetBytes(unicode ? "CreateDirectoryW" : "CreateDirectoryA").CopyTo(s[0x210..]);
    // root -> type 5 -> id 106 -> lang 1033 -> 3 bytes at RVA 0x3038
    var r = 0x400;
    BinaryPrimitives.WriteUInt16LittleEndian(s[(r + 14)..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(r + 16)..], 5);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(r + 20)..], 0x80000018);
    BinaryPrimitives.WriteUInt16LittleEndian(s[(r + 0x18 + 14)..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(r + 0x18 + 16)..], 106);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(r + 0x18 + 20)..], 0x80000030);
    BinaryPrimitives.WriteUInt16LittleEndian(s[(r + 0x30 + 14)..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(r + 0x30 + 16)..], 1033);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(r + 0x30 + 20)..], 0x48);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(r + 0x48)..], 0x3058);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(r + 0x4C)..], 3);
    "dlg"u8.CopyTo(s[(r + 0x58)..]);
    return pe;
  }

  [TestCaseSource(nameof(AllVariants))]
  public void GivenFiles_WhenWrittenOntoAStub_ThenOurReaderReturnsEveryFile(NsisCompression compression, bool solid) {
    var installer = Build(SyntheticStub(), compression, solid);
    using (var reader = new NsisReader(new MemoryStream(installer), leaveOpen: false)) {
      Assert.That(reader.Compression, Is.EqualTo(compression));
      Assert.That(reader.IsSolid, Is.EqualTo(solid));
    }
    AssertSameFiles(ReadBack(installer), "our reader");
  }

  [Test]
  public void GivenAnInstaller_WhenItsCrcIsRecomputed_ThenItCoversTheFileFromByte512() {
    var installer = Build(SyntheticStub(), NsisCompression.Lzma, solid: false);
    var stored = BinaryPrimitives.ReadUInt32LittleEndian(installer.AsSpan(installer.Length - 4));
    Assert.That(Compression.Core.Checksums.Crc32.Compute(installer.AsSpan(512, installer.Length - 516)), Is.EqualTo(stored));
  }

  [Test]
  public void GivenAStub_WhenPrepared_ThenNdataGrowsTheResourcesMoveAndAManifestJoinsThem() {
    var installer = Build(SyntheticStub(), NsisCompression.Zlib, solid: false);
    using var ms = new MemoryStream(installer);
    Assert.Multiple(() => {
      Assert.That(PeResourceLocator.Find(ms, 5, 106) is { } dialog && installer.AsSpan((int)dialog.Offset, dialog.Size).SequenceEqual("dlg"u8), Is.True, "the stub's own resources survive");
      Assert.That(PeResourceLocator.Find(ms, 24, 1) is { } manifest
        && Encoding.UTF8.GetString(installer, (int)manifest.Offset, manifest.Size).Contains("asInvoker", StringComparison.Ordinal), Is.True, "manifest");
      var ndataVirtualSize = BinaryPrimitives.ReadUInt32LittleEndian(installer.AsSpan(0x98 + 0xE0 + 40 + 8));
      Assert.That(ndataVirtualSize, Is.EqualTo(32u * 1024 * 2), ".ndata holds 32 variables of 1024 UTF-16 characters");
      var rsrcVa = BinaryPrimitives.ReadUInt32LittleEndian(installer.AsSpan(0x98 + 0xE0 + 80 + 12));
      Assert.That(rsrcVa, Is.EqualTo(0x12000u), ".rsrc starts after the grown .ndata");
    });
  }

  [Test]
  public void GivenNoFiles_WhenWritten_ThenTheInstallerListsNothing()
    => Assert.That(ReadBack(Build(SyntheticStub(), NsisCompression.Lzma, false, [])), Is.Empty);

  [Test]
  public void GivenAnAnsiStub_WhenWriting_ThenItIsRefused()
    => Assert.That(() => Build(SyntheticStub(unicode: false), NsisCompression.Zlib, false), Throws.ArgumentException);

  [Test]
  public void GivenSomethingThatIsNotAPe_WhenWriting_ThenItIsRefused()
    => Assert.That(() => Build(new byte[4096], NsisCompression.Zlib, false), Throws.ArgumentException);

  [TestCase("../escape.txt")]
  [TestCase("a/../../escape.txt")]
  [TestCase("C:/abs.txt")]
  [TestCase("")]
  public void GivenAPathOutsideTheInstallDirectory_WhenWriting_ThenItIsRefused(string path)
    => Assert.That(() => Build(SyntheticStub(), NsisCompression.Zlib, false, [new(path, [1])]), Throws.ArgumentException);

  [Test]
  public void GivenNoCompressor_WhenWriting_ThenItIsRefused()
    => Assert.That(() => Build(SyntheticStub(), NsisCompression.None, false), Throws.InstanceOf<ArgumentOutOfRangeException>());

  [TestCase(@"$INSTDIR\x")]
  [TestCase(@"$WINDIR\x")]
  public void GivenAnInstallDirectoryWithAnUnsupportedVariable_WhenWriting_ThenItIsRefused(string directory) {
    using var ms = new MemoryStream();
    Assert.That(() => NsisInstallerWriter.Write(ms, SyntheticStub(), Payload(), new NsisInstallerOptions { InstallDirectory = directory }), Throws.ArgumentException);
  }

  [Test]
  public void GivenTheDescriptor_WhenCreatingWithAnExplicitStub_ThenOurReaderReadsIt() {
    var stubPath = Path.Combine(this._tmp, "stub.bin");
    File.WriteAllBytes(stubPath, SyntheticStub());
    var inputs = Payload().Select(f => ArchiveInputInfo.InMemory(f.Path, f.Data)).ToList();
    using var ms = new MemoryStream();
    new NsisFormatDescriptor().Create(ms, inputs, new FormatCreateOptions("bzip2:solid") { FormatSpecific = { ["stub"] = stubPath } });
    AssertSameFiles(ReadBack(ms.ToArray()), "descriptor");
  }

  // ── NSIS's own stubs: 7-Zip and the installer itself ────────────────────────────────────

  private static byte[] RealStub(NsisCompression compression, bool solid) {
    var stub = NsisInstallerWriter.LoadStub(compression, solid, Environment.GetEnvironmentVariable("CWB_NSIS_DIR"));
    if (stub == null)
      Assert.Ignore("NSIS 3 stubs not found (install NSIS 3, or set NSISDIR / CWB_NSIS_DIR).");
    return stub!;
  }

  private string WriteReal(NsisCompression compression, bool solid) {
    var path = Path.Combine(this._tmp, $"cwbpkg-{compression}{(solid ? "-solid" : "")}.exe".ToLowerInvariant());
    File.WriteAllBytes(path, Build(RealStub(compression, solid), compression, solid));
    return path;
  }

  private static Dictionary<string, byte[]> Tree(string directory)
    => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
      .ToDictionary(p => Path.GetRelativePath(directory, p).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);

  [TestCaseSource(nameof(AllVariants)), Category("EndToEnd")]
  public void GivenAnInstallerOnNsisOwnStub_WhenSevenZipExtractsIt_ThenEveryFileComesBack(NsisCompression compression, bool solid) {
    var installer = this.WriteReal(compression, solid);
    AssertSameFiles(ReadBack(File.ReadAllBytes(installer)), "our reader");
    var sz = ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
    if (sz == null)
      Assert.Ignore("7-Zip not found.");
    var outDir = Path.Combine(this._tmp, "7z");
    var psi = new ProcessStartInfo(sz!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "x", installer, "-o" + outDir, "-y" })
      psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    Assert.That(p.ExitCode, Is.Zero, output);
    AssertSameFiles(Tree(outDir), "7-Zip");
  }

  [TestCaseSource(nameof(AllVariants)), Category("EndToEnd")]
  public void GivenAnInstallerOnNsisOwnStub_WhenRunSilently_ThenItInstallsExactlyOurFiles(NsisCompression compression, bool solid) {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("The exehead is a Windows program.");
    var installer = this.WriteReal(compression, solid);
    var target = Path.Combine(this._tmp, "installed");
    if (target.Contains(' '))
      Assert.Ignore("/D= takes the rest of the command line; the temp path has a space.");
    var psi = new ProcessStartInfo(installer, "/S /D=" + target) { UseShellExecute = false };
    using var p = Process.Start(psi)!;
    if (!p.WaitForExit(120_000)) {
      p.Kill(entireProcessTree: true);
      Assert.Fail("The installer did not finish within two minutes.");
    }
    Assert.That(p.ExitCode, Is.Zero);
    AssertSameFiles(Tree(target), "silent install");
    Assert.That(Directory.GetDirectories(target, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(target, d).Replace('\\', '/')).Order(),
      Is.EqualTo(new[] { "bin", "bin/deep", "docs" }), "no directories beyond the files' own");
  }
}
