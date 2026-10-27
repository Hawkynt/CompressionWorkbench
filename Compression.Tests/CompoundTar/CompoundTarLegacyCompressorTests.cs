namespace Compression.Tests.CompoundTar;

/// <summary>
/// tar.Z, tar.lzma and tar.lzo: the three tar wrappers GNU tar selects with -Z, --lzma and
/// --lzop. Round-trips through our own code, plus GNU tar (WSL / native Linux) reading what we
/// write and writing what we read.
/// </summary>
[TestFixture]
public class CompoundTarLegacyCompressorTests {

  private static readonly object[] Cases = [
    new object[] { "TarZ", ".tar.Z", "-Z", "compress" },
    new object[] { "TarLzma", ".tar.lzma", "--lzma", "lzma" },
    new object[] { "TarLzo", ".tar.lzo", "--lzop", "lzop" },
  ];

  private string _dir = null!;

  [SetUp]
  public void Setup() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_tarlegacy_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static Dictionary<string, byte[]> Payload() {
    var big = new byte[150_000];
    for (var i = 0; i < big.Length; ++i) big[i] = (byte)(i * 31 + (i >> 9));
    return new(StringComparer.Ordinal) {
      ["hello.txt"] = "tar wrapped by a legacy compressor\n"u8.ToArray(),
      ["empty.bin"] = [],
      ["sub/big.bin"] = big,
    };
  }

  private string WriteSource() {
    var src = Path.Combine(this._dir, "src");
    foreach (var (name, data) in Payload()) {
      var path = Path.Combine(src, name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, data);
    }
    return src;
  }

  private string CreateOurs(string extension) {
    var src = this.WriteSource();
    var archive = Path.Combine(this._dir, "ours" + extension);
    var inputs = Payload().Keys
      .Select(n => new Compression.Lib.ArchiveInput(Path.Combine(src, n.Replace('/', Path.DirectorySeparatorChar)), n))
      .ToList();
    Compression.Lib.ArchiveOperations.Create(archive, inputs, new Compression.Lib.CompressionOptions());
    return archive;
  }

  private void AssertExtractedMatches(string outDir) {
    foreach (var (name, data) in Payload())
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(data), name);
  }

  [TestCaseSource(nameof(Cases)), Category("HappyPath")]
  public void GivenCompoundExtension_WhenDetected_ThenResolvesToTheCompoundTarFormat(string id, string ext, string _, string __) {
    Assert.That(Compression.Lib.FormatDetector.DetectByExtension("x" + ext).ToString(), Is.EqualTo(id));
    Assert.That(Compression.Lib.FormatDetector.DetectByExtension("x" + ext.ToUpperInvariant()).ToString(), Is.EqualTo(id));
    Assert.That(Compression.Lib.FormatDetector.DetectByExtensionForCreate("x" + ext).ToString(), Is.EqualTo(id));
  }

  [TestCaseSource(nameof(Cases)), Category("RoundTrip")]
  public void GivenFiles_WhenCreatedAndExtracted_ThenEveryByteRoundTrips(string id, string ext, string _, string __) {
    var archive = this.CreateOurs(ext);

    var entries = Compression.Lib.ArchiveOperations.List(archive, null);
    Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name), Is.EquivalentTo(Payload().Keys));

    var outDir = Path.Combine(this._dir, "out");
    Compression.Lib.ArchiveOperations.Extract(archive, outDir, null, null);
    this.AssertExtractedMatches(outDir);
  }

  [TestCaseSource(nameof(Cases)), Category("Exception")]
  public void GivenTruncatedArchive_WhenExtracted_ThenFailsRatherThanWritingWrongBytes(string id, string ext, string _, string __) {
    var archive = this.CreateOurs(ext);
    var bytes = File.ReadAllBytes(archive);
    File.WriteAllBytes(archive, bytes[..(bytes.Length / 2)]);

    var outDir = Path.Combine(this._dir, "out");
    Assert.That(() => {
      Compression.Lib.ArchiveOperations.Extract(archive, outDir, null, null);
      this.AssertExtractedMatches(outDir);
    }, Throws.Exception);
  }

  [TestCaseSource(nameof(Cases)), Category("ExternalInterop")]
  public void GivenOurArchive_WhenGnuTarExtractsIt_ThenEveryByteMatches(string id, string ext, string flag, string tool) {
    RequireTool("tar");
    RequireTool(tool);
    var archive = this.CreateOurs(ext);
    var outDir = Path.Combine(this._dir, "gnu");
    Directory.CreateDirectory(outDir);

    var r = FsInteropToolbox.RunWsl(
      $"tar {flag} -xf '{FsInteropToolbox.WinToWsl(archive)}' -C '{FsInteropToolbox.WinToWsl(outDir)}'");

    Assert.That(r.ExitCode, Is.EqualTo(0), $"GNU tar {flag} rejected our archive:\n{r.StdOut}\n{r.StdErr}");
    this.AssertExtractedMatches(outDir);
  }

  [TestCaseSource(nameof(Cases)), Category("ExternalInterop")]
  public void GivenGnuTarArchive_WhenWeExtractIt_ThenEveryByteMatches(string id, string ext, string flag, string tool) {
    RequireTool("tar");
    RequireTool(tool);
    var src = this.WriteSource();
    var archive = Path.Combine(this._dir, "theirs" + ext);

    var r = FsInteropToolbox.RunWsl(
      $"tar -cf - -C '{FsInteropToolbox.WinToWsl(src)}' hello.txt empty.bin sub/big.bin | {Compressor(tool)} > '{FsInteropToolbox.WinToWsl(archive)}'");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"GNU tar {flag} -c failed:\n{r.StdOut}\n{r.StdErr}");

    Assert.That(Compression.Lib.FormatDetector.DetectByExtension(archive).ToString(), Is.EqualTo(id));
    var outDir = Path.Combine(this._dir, "out");
    Compression.Lib.ArchiveOperations.Extract(archive, outDir, null, null);
    this.AssertExtractedMatches(outDir);
  }

  // The compressor itself, not tar's -Z/--lzma/--lzop: GNU tar 1.35 on Ubuntu runs "lzma" as plain
  // xz, which writes an .xz stream, not the LZMA-alone stream a .tar.lzma holds.
  private static string Compressor(string tool) => tool switch {
    "compress" => "compress -c",
    "lzma" => "xz --format=lzma -c",
    _ => "lzop -c",
  };

  private static void RequireTool(string tool) {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"'{tool}' is not installed (apt: tar / ncompress / xz-utils / lzop).");
  }
}
