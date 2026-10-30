using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// Readers other than 7-Zip: py7zr and libarchive's bsdtar must extract what we write wherever
/// they support the coders, and Unix 7-Zip's symbolic links (<c>-snl</c>) must list as links here.
/// The tools run inside WSL on Windows (natively on Linux); a missing tool skips the case.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[NonParallelizable]
public sealed class SevenZipSecondReaderTests {
  private const string Password = "S3cret!";
  private string _root = null!;
  private SortedDictionary<string, byte[]?> _tree = null!;

  [OneTimeSetUp]
  public void BuildTree() {
    this._root = Path.Combine(Path.GetTempPath(), $"cwb_7z2nd_{Guid.NewGuid():N}");
    // ASCII names only: WSL's view of NTFS names is not what this test is about.
    this._tree = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal) {
      ["text.txt"] = SevenZipOracle.Text(120_000, 1),
      ["code/x86.bin"] = SevenZipOracle.X86Like(90_000, 2),
      ["code"] = null,
      ["empty.txt"] = [],
      ["emptydir"] = null,
    };
  }

  [OneTimeTearDown]
  public void Cleanup() {
    try { Directory.Delete(this._root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
  }

  private static void RequireWsl(string tool) {
    if (!FsInteropToolbox.WslAvailable && OperatingSystem.IsWindows())
      Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"{tool} is not installed in WSL.");
  }

  private static bool HasPy7zr() => FsInteropToolbox.RunWsl("python3 -c 'import py7zr'").ExitCode == 0;

  private string Write(string name, SevenZipCodec codec, SevenZipFilter filter, bool encrypt, bool encryptHeaders) {
    Directory.CreateDirectory(this._root);
    var archive = Path.Combine(this._root, $"{name}.7z");
    using var file = File.Create(archive);
    using var writer = new SevenZipWriter(file, codec, filter: filter, deltaDistance: 2, password: encrypt ? Password : null, encryptHeaders: encryptHeaders);
    foreach (var (path, data) in this._tree)
      if (data == null)
        writer.AddDirectory(path);
      else
        writer.AddEntry(new SevenZipEntry { Name = path }, data);
    writer.Finish();
    return archive;
  }

  public static IEnumerable<TestCaseData> Py7zrProfiles() {
    static TestCaseData P(string name, SevenZipCodec codec, SevenZipFilter filter = SevenZipFilter.None, bool encrypt = false, bool headers = false)
      => new TestCaseData(name, codec, filter, encrypt, headers).SetName($"Given_OurArchive_{name}_When_Py7zrExtracts_Then_ItMatches");
    yield return P("lzma2", SevenZipCodec.Lzma2);
    yield return P("lzma", SevenZipCodec.Lzma);
    yield return P("bzip2", SevenZipCodec.BZip2);
    yield return P("deflate", SevenZipCodec.Deflate);
    yield return P("copy", SevenZipCodec.Copy);
    yield return P("bcj", SevenZipCodec.Lzma2, SevenZipFilter.BcjX86);
    yield return P("arm", SevenZipCodec.Lzma2, SevenZipFilter.BcjArm);
    yield return P("ppc", SevenZipCodec.Lzma2, SevenZipFilter.BcjPowerPC);
    yield return P("sparc", SevenZipCodec.Lzma2, SevenZipFilter.BcjSparc);
    yield return P("delta", SevenZipCodec.Lzma2, SevenZipFilter.Delta);
    yield return P("aes", SevenZipCodec.Lzma2, encrypt: true);
    yield return P("aes-headers", SevenZipCodec.Lzma2, encrypt: true, headers: true);
  }

  [TestCaseSource(nameof(Py7zrProfiles))]
  public void Given_OurArchive_When_Py7zrExtracts_Then_ItMatches(string name, SevenZipCodec codec, SevenZipFilter filter, bool encrypt, bool headers) {
    RequireWsl("python3");
    if (!HasPy7zr())
      Assert.Ignore("py7zr is not installed in WSL (pip install py7zr).");

    var archive = this.Write("py7zr-" + name, codec, filter, encrypt, headers);
    var output = Path.Combine(this._root, $"py7zr-{name}.out");
    Directory.CreateDirectory(output);
    var pw = encrypt ? $", password='{Password}'" : "";
    var script = $"import py7zr; py7zr.SevenZipFile({FsInteropToolbox.WinToWsl(archive)}, 'r'{pw}).extractall({FsInteropToolbox.WinToWsl(output)})";
    var result = FsInteropToolbox.RunWsl($"python3 -c \"{script}\"");
    Assert.That(result.ExitCode, Is.Zero, $"py7zr failed on '{name}':\n{result.StdOut}\n{result.StdErr}");
    SevenZipOracle.AssertSameTree(this._tree, SevenZipOracle.ReadTree(output), $"{name} (py7zr)");
  }

  public static IEnumerable<TestCaseData> BsdtarProfiles() {
    static TestCaseData P(string name, SevenZipCodec codec, SevenZipFilter filter = SevenZipFilter.None)
      => new TestCaseData(name, codec, filter).SetName($"Given_OurArchive_{name}_When_BsdtarExtracts_Then_ItMatches");
    yield return P("lzma2", SevenZipCodec.Lzma2);
    yield return P("lzma", SevenZipCodec.Lzma);
    yield return P("bzip2", SevenZipCodec.BZip2);
    yield return P("deflate", SevenZipCodec.Deflate);
    yield return P("copy", SevenZipCodec.Copy);
    yield return P("bcj", SevenZipCodec.Lzma2, SevenZipFilter.BcjX86);
    yield return P("bcj2", SevenZipCodec.Lzma2, SevenZipFilter.Bcj2);
    yield return P("delta", SevenZipCodec.Lzma2, SevenZipFilter.Delta);
  }

  [TestCaseSource(nameof(BsdtarProfiles))]
  public void Given_OurArchive_When_BsdtarExtracts_Then_ItMatches(string name, SevenZipCodec codec, SevenZipFilter filter) {
    RequireWsl("bsdtar");
    var archive = this.Write("bsdtar-" + name, codec, filter, encrypt: false, encryptHeaders: false);
    var output = Path.Combine(this._root, $"bsdtar-{name}.out");
    Directory.CreateDirectory(output);
    var result = FsInteropToolbox.RunWsl($"bsdtar -xf {FsInteropToolbox.WinToWsl(archive)} -C {FsInteropToolbox.WinToWsl(output)}");
    Assert.That(result.ExitCode, Is.Zero, $"bsdtar failed on '{name}':\n{result.StdOut}\n{result.StdErr}");
    SevenZipOracle.AssertSameTree(this._tree, SevenZipOracle.ReadTree(output), $"{name} (bsdtar)");
  }

  [Test]
  public void Given_Unix7ZipArchiveWithASymlink_When_Listed_Then_TheLinkAndItsTargetAreReported() {
    var tool = new[] { "7zz", "7z", "7za" }.FirstOrDefault(FsInteropToolbox.WslHasTool);
    if (tool == null)
      Assert.Ignore("No Unix 7-Zip (7zz / 7z / 7za) in WSL.");

    var archive = Path.Combine(this._root, "unix-links.7z");
    Directory.CreateDirectory(this._root);
    var linux = $"d=$(mktemp -d) && cd $d && printf target-content > target.txt && ln -s target.txt link.txt && mkdir sub && ln -s sub linkdir && chmod 640 target.txt && {tool} a -snl {FsInteropToolbox.WinToWsl(archive)} target.txt link.txt sub linkdir >/dev/null && rm -rf $d";
    var result = FsInteropToolbox.RunWsl(linux);
    Assert.That(result.ExitCode, Is.Zero, $"{tool} failed:\n{result.StdOut}\n{result.StdErr}");

    using var stream = File.OpenRead(archive);
    var entries = new SevenZipFormatDescriptor().List(stream, null).ToDictionary(static e => e.Name);
    Assert.That(entries["link.txt"].IsSymlink, Is.True);
    Assert.That(entries["link.txt"].LinkTarget, Is.EqualTo("target.txt"));
    Assert.That(entries["linkdir"].IsSymlink, Is.True);
    Assert.That(entries["linkdir"].LinkTarget, Is.EqualTo("sub"));
    Assert.That(entries["target.txt"].IsSymlink, Is.False);

    stream.Position = 0;
    using var reader = new SevenZipReader(stream, leaveOpen: true);
    var target = reader.Entries.Single(static e => e.Name == "target.txt");
    Assert.That(target.UnixMode & 0x1FF, Is.EqualTo(0x1A0)); // rw-r-----
  }
}
