using Compression.Registry;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// We write, 7-Zip reads: every codec, filter, block layout and encryption profile our writer
/// and our descriptor produce must pass <c>7z t</c> and extract byte for byte with the real
/// 7-Zip command line.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[NonParallelizable]
public sealed class SevenZipConformanceWriteTests {
  private const string Password = "S3cret!";

  /// <summary>
  /// Profiles our writer cannot yet produce in a form 7-Zip accepts, with the reason. A gap that
  /// starts passing fails the test, so the list cannot go stale.
  /// </summary>
  private static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal) {
    ["ppmd"] = "PPMd var.H interoperability belongs to the PPMd work (codec-proof audit, stacked on #560): our model and method id are not 7-Zip's yet.",
    ["ppmd-aes"] = "PPMd var.H interoperability belongs to the PPMd work (codec-proof audit, stacked on #560).",
  };

  private string _root = null!;
  private SortedDictionary<string, byte[]?> _tree = null!;

  [OneTimeSetUp]
  public void BuildTree() {
    this._root = Path.Combine(Path.GetTempPath(), $"cwb_7zconfw_{Guid.NewGuid():N}");
    this._tree = SevenZipOracle.WriteStandardTree(Path.Combine(this._root, "src"));
  }

  [OneTimeTearDown]
  public void Cleanup() {
    try { Directory.Delete(this._root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
  }

  public static IEnumerable<TestCaseData> WriterProfiles() {
    static TestCaseData P(string name, SevenZipCodec codec, SevenZipFilter filter = SevenZipFilter.None, bool encrypt = false, bool encryptHeaders = false, int threads = 1, long blockSize = 0)
      => new TestCaseData(name, codec, filter, encrypt, encryptHeaders, threads, blockSize).SetName($"Given_OurArchive_{name}_When_7ZipTestsAndExtracts_Then_ItMatches");

    yield return P("copy", SevenZipCodec.Copy);
    yield return P("lzma", SevenZipCodec.Lzma);
    yield return P("lzma2", SevenZipCodec.Lzma2);
    yield return P("deflate", SevenZipCodec.Deflate);
    yield return P("bzip2", SevenZipCodec.BZip2);
    yield return P("ppmd", SevenZipCodec.PPMd);
    yield return P("bcj", SevenZipCodec.Lzma2, SevenZipFilter.BcjX86);
    yield return P("bcj2", SevenZipCodec.Lzma2, SevenZipFilter.Bcj2);
    yield return P("bcj2-lzma", SevenZipCodec.Lzma, SevenZipFilter.Bcj2);
    yield return P("arm", SevenZipCodec.Lzma2, SevenZipFilter.BcjArm);
    yield return P("armt", SevenZipCodec.Lzma2, SevenZipFilter.BcjArmThumb);
    yield return P("ppc", SevenZipCodec.Lzma2, SevenZipFilter.BcjPowerPC);
    yield return P("sparc", SevenZipCodec.Lzma2, SevenZipFilter.BcjSparc);
    yield return P("ia64", SevenZipCodec.Lzma2, SevenZipFilter.BcjIA64);
    yield return P("delta", SevenZipCodec.Lzma2, SevenZipFilter.Delta);
    yield return P("swap2", SevenZipCodec.Lzma2, SevenZipFilter.Swap2);
    yield return P("swap4", SevenZipCodec.Lzma2, SevenZipFilter.Swap4);
    yield return P("copy-filter", SevenZipCodec.Lzma2, SevenZipFilter.Copy);
    yield return P("aes", SevenZipCodec.Lzma2, encrypt: true);
    yield return P("aes-copy", SevenZipCodec.Copy, encrypt: true);
    yield return P("aes-bcj", SevenZipCodec.Lzma2, SevenZipFilter.BcjX86, encrypt: true);
    yield return P("aes-bcj2", SevenZipCodec.Lzma2, SevenZipFilter.Bcj2, encrypt: true);
    yield return P("ppmd-aes", SevenZipCodec.PPMd, encrypt: true);
    yield return P("aes-headers", SevenZipCodec.Lzma2, encrypt: true, encryptHeaders: true);
    yield return P("aes-headers-bcj2", SevenZipCodec.Lzma2, SevenZipFilter.Bcj2, encrypt: true, encryptHeaders: true);
    yield return P("blocks", SevenZipCodec.Lzma2, threads: 1, blockSize: 64 << 10);
    yield return P("blocks-mt", SevenZipCodec.Lzma2, threads: 4, blockSize: 64 << 10);
    yield return P("blocks-mt-aes-bcj", SevenZipCodec.Lzma2, SevenZipFilter.BcjX86, encrypt: true, threads: 4, blockSize: 64 << 10);
  }

  [TestCaseSource(nameof(WriterProfiles))]
  public void Given_OurArchive_When_7ZipTestsAndExtracts_Then_ItMatches(string name, SevenZipCodec codec, SevenZipFilter filter, bool encrypt, bool encryptHeaders, int threads, long blockSize) {
    var archive = Path.Combine(this._root, $"w-{name}.7z");
    using (var file = File.Create(archive))
    using (var writer = new SevenZipWriter(file, codec, filter: filter, deltaDistance: 4,
             password: encrypt ? Password : null, encryptHeaders: encryptHeaders)) {
      foreach (var (path, data) in this._tree)
        if (data == null)
          writer.AddDirectory(path);
        else
          writer.AddEntry(new SevenZipEntry { Name = path, LastWriteTime = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc) }, data);
      writer.Finish(threads, blockSize);
    }

    this.AssertSevenZipAccepts(name, archive, encrypt ? Password : null);
  }

  public static IEnumerable<TestCaseData> DescriptorProfiles() {
    static TestCaseData P(string name, FormatCreateOptions options)
      => new TestCaseData(name, options).SetName($"Given_DescriptorCreate_{name}_When_7ZipTestsAndExtracts_Then_ItMatches");

    yield return P("default", new FormatCreateOptions());
    yield return P("lzma", new FormatCreateOptions("lzma"));
    yield return P("deflate", new FormatCreateOptions("deflate"));
    yield return P("bzip2", new FormatCreateOptions("bzip2"));
    yield return P("copy", new FormatCreateOptions("copy"));
    yield return P("solid-64k-mt", new FormatCreateOptions { SolidSize = 64 << 10, Threads = 4 });
    yield return P("password", new FormatCreateOptions { Password = Password });
    yield return P("password-headers", new FormatCreateOptions { Password = Password, EncryptFilenames = true });
    yield return P("password-mt", new FormatCreateOptions { Password = Password, SolidSize = 64 << 10, Threads = 4 });
    yield return P("schema-password", new FormatCreateOptions { FormatSpecific = new() { ["Password"] = Password, ["Method"] = "lzma" } });
  }

  [TestCaseSource(nameof(DescriptorProfiles))]
  public void Given_DescriptorCreate_When_7ZipTestsAndExtracts_Then_ItMatches(string name, FormatCreateOptions options) {
    var source = Path.Combine(this._root, "src");
    var inputs = this._tree.Select(p => p.Value == null
      ? new ArchiveInputInfo(Path.Combine(source, p.Key), p.Key, IsDirectory: true)
      : ArchiveInputInfo.FromFile(new FileInfo(Path.Combine(source, p.Key)), p.Key)).ToList();
    var archive = Path.Combine(this._root, $"d-{name}.7z");
    using (var file = File.Create(archive))
      new SevenZipFormatDescriptor().Create(file, inputs, options);

    var password = options.Password ?? (options.FormatSpecific.TryGetValue("Password", out var p) ? p : null);
    this.AssertSevenZipAccepts("descriptor-" + name, archive, password);
  }

  private void AssertSevenZipAccepts(string name, string archive, string? password) {
    var pw = password == null ? "-p-" : $"-p{password}";
    var (testExit, testOutput) = SevenZipOracle.Run(this._root, "t", pw, archive);
    var accepted = testExit == 0 && testOutput.Contains("Everything is Ok", StringComparison.Ordinal);

    if (KnownGaps.TryGetValue(name, out var reason)) {
      Assert.That(accepted, Is.False, $"'{name}' now passes 7-Zip's test: remove it from KnownGaps ({reason}).");
      Assert.Ignore($"Known gap: {reason}");
    }
    Assert.That(accepted, Is.True, $"7z t rejects our '{name}' archive:\n{testOutput}");

    var output = Path.Combine(this._root, $"{Path.GetFileNameWithoutExtension(archive)}.out");
    SevenZipOracle.RunOk(this._root, "x", "-y", pw, $"-o{output}", archive);
    SevenZipOracle.AssertSameTree(this._tree, SevenZipOracle.ReadTree(output), $"{name} (7-Zip extraction)");
  }
}
