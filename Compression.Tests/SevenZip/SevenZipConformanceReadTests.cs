using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// 7-Zip writes, we read: every coder, filter, solid layout and header profile the real 7-Zip
/// command line produces is listed and extracted here, byte for byte, with every CRC checked,
/// through both the reader and the format descriptor's directory extraction.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[NonParallelizable]
public sealed class SevenZipConformanceReadTests {
  private string _root = null!;
  private string _source = null!;
  private SortedDictionary<string, byte[]?> _tree = null!;

  [OneTimeSetUp]
  public void BuildTree() {
    this._root = Path.Combine(Path.GetTempPath(), $"cwb_7zconf_{Guid.NewGuid():N}");
    this._source = Path.Combine(this._root, "src");
    this._tree = SevenZipOracle.WriteStandardTree(this._source);
  }

  [OneTimeTearDown]
  public void Cleanup() {
    try { Directory.Delete(this._root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
  }

  /// <summary>Every 7-Zip profile: name, then the switches handed to <c>7z a</c>.</summary>
  public static IEnumerable<TestCaseData> Profiles() {
    static TestCaseData P(string name, params string[] switches)
      => new TestCaseData(name, switches).SetName($"Given_7ZipArchive_{name}_When_Read_Then_EveryEntryMatches");

    yield return P("default");
    yield return P("copy", "-m0=Copy");
    yield return P("mx0", "-mx0");
    yield return P("lzma", "-m0=LZMA");
    yield return P("lzma-eos", "-m0=LZMA:eos");
    yield return P("lzma-lc4lp0pb0", "-m0=LZMA:lc4:lp0:pb0");
    yield return P("lzma2", "-m0=LZMA2");
    yield return P("lzma2-mx1", "-m0=LZMA2", "-mx1");
    yield return P("lzma2-mx9", "-m0=LZMA2", "-mx9");
    yield return P("lzma2-mt-blocks", "-m0=LZMA2:d=64k:c=128k", "-mmt=4");
    yield return P("ppmd", "-m0=PPMd");
    yield return P("ppmd-o2", "-m0=PPMd:o=2:mem=1m");
    yield return P("ppmd-o32", "-m0=PPMd:o=32:mem=64m");
    yield return P("bzip2", "-m0=BZip2");
    yield return P("bzip2-mt", "-m0=BZip2", "-mmt=4");
    yield return P("deflate", "-m0=Deflate");
    yield return P("deflate64", "-m0=Deflate64");
    yield return P("bcj", "-mf=BCJ");
    yield return P("bcj2", "-mf=BCJ2");
    yield return P("bcj2-explicit", "-m0=BCJ2", "-m1=LZMA2:d=1m", "-m2=LZMA:d=64k", "-m3=LZMA:d=64k", "-mb0:1", "-mb0s1:2", "-mb0s2:3");
    yield return P("arm", "-mf=ARM");
    yield return P("armt", "-mf=ARMT");
    yield return P("arm64", "-mf=ARM64");
    yield return P("riscv", "-mf=RISCV");
    yield return P("ppc", "-mf=PPC");
    yield return P("ia64", "-mf=IA64");
    yield return P("sparc", "-mf=SPARC");
    yield return P("delta4", "-mf=Delta:4");
    yield return P("swap2", "-m0=Swap2", "-m1=LZMA2");
    yield return P("swap4", "-m0=Swap4", "-m1=LZMA2");
    yield return P("delta-copy", "-m0=Delta:2", "-m1=Copy");
    yield return P("solid-off", "-ms=off");
    yield return P("solid-on", "-ms=on");
    yield return P("solid-64k", "-ms=64k");
    yield return P("solid-2f", "-ms=2f");
    yield return P("solid-ext", "-ms=e");
    yield return P("hc-off", "-mhc=off");
    yield return P("hc-off-copy", "-mhc=off", "-m0=Copy");
    yield return P("times-all", "-mtc=on", "-mta=on");
    yield return P("times-none", "-mtm=off", "-mtc=off", "-mta=off");
    yield return P("aes", "-pS3cret!");
    yield return P("aes-copy", "-pS3cret!", "-m0=Copy");
    yield return P("aes-bcj2", "-pS3cret!", "-mf=BCJ2");
    yield return P("aes-solid-off", "-pS3cret!", "-ms=off");
    yield return P("aes-mhe", "-pS3cret!", "-mhe=on");
    yield return P("aes-mhe-hc-off", "-pS3cret!", "-mhe=on", "-mhc=off");
  }

  /// <summary>
  /// Profiles we cannot read yet, with the reason. A gap that starts passing fails the test, so
  /// the list cannot go stale.
  /// </summary>
  private static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal) {
    ["ppmd"] = "PPMd var.H (method 030401) belongs to the PPMd work (codec-proof audit, stacked on #560).",
    ["ppmd-o2"] = "PPMd var.H (method 030401) belongs to the PPMd work (codec-proof audit, stacked on #560).",
    ["ppmd-o32"] = "PPMd var.H (method 030401) belongs to the PPMd work (codec-proof audit, stacked on #560).",
  };

  [TestCaseSource(nameof(Profiles))]
  public void Given_7ZipArchive_When_Read_Then_EveryEntryMatches(string name, string[] switches) {
    if (!KnownGaps.TryGetValue(name, out var gap)) {
      this.ReadAndCompare(name, switches);
      return;
    }
    try {
      this.ReadAndCompare(name, switches);
    } catch (Exception e) when (e is NotSupportedException or InvalidDataException or AssertionException) {
      Assert.Ignore($"Known gap: {gap} ({e.Message})");
    }
    Assert.Fail($"'{name}' now reads correctly: remove it from KnownGaps ({gap}).");
  }

  private void ReadAndCompare(string name, string[] switches) {
    var archive = Path.Combine(this._root, $"{name}.7z");
    SevenZipOracle.RunOk(this._source, ["a", "-y", "-r", archive, .. switches, "*"]);
    var password = switches.FirstOrDefault(s => s.StartsWith("-p", StringComparison.Ordinal))?[2..];

    // The reader: every entry, in index order.
    var fromReader = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal);
    using (var stream = File.OpenRead(archive))
    using (var reader = new SevenZipReader(stream, password: password))
      for (var i = 0; i < reader.Entries.Count; ++i) {
        var entry = reader.Entries[i];
        fromReader[entry.Name.Replace('\\', '/')] = entry.IsDirectory ? null : reader.Extract(i);
      }
    SevenZipOracle.AssertSameTree(this._tree, fromReader, $"{name} (reader)");

    // The descriptor: directory extraction, the path the CLI and UI take.
    var output = Path.Combine(this._root, $"{name}.out");
    using (var stream = File.OpenRead(archive))
      new SevenZipFormatDescriptor().Extract(stream, output, password, null);
    SevenZipOracle.AssertSameTree(this._tree, SevenZipOracle.ReadTree(output), $"{name} (descriptor)");
  }

  [TestCase("-pS3cret!")]
  [TestCase("-pS3cret!", "-mhe=on")]
  [TestCase("-pS3cret!", "-m0=Copy")]
  public void Given_7ZipEncryptedArchive_When_ReadWithWrongPassword_Then_ItRefusesWithoutGarbage(params string[] switches) {
    var archive = Path.Combine(this._root, $"wrongpw_{string.Join("", switches).GetHashCode():X8}.7z");
    SevenZipOracle.RunOk(this._source, ["a", "-y", "-r", archive, .. switches, "*"]);

    Assert.That(() => {
      using var stream = File.OpenRead(archive);
      using var reader = new SevenZipReader(stream, password: "not-the-password");
      for (var i = 0; i < reader.Entries.Count; ++i)
        _ = reader.Extract(i);
    }, Throws.InstanceOf<InvalidDataException>().Or.InstanceOf<UnauthorizedAccessException>());
  }

  [Test]
  public void Given_7ZipEncryptedArchive_When_ReadWithoutPassword_Then_ItAsksForOne() {
    var archive = Path.Combine(this._root, "nopw.7z");
    SevenZipOracle.RunOk(this._source, ["a", "-y", "-r", archive, "-pS3cret!", "-mhe=on", "*"]);

    Assert.That(() => {
      using var stream = File.OpenRead(archive);
      using var reader = new SevenZipReader(stream);
    }, Throws.InstanceOf<UnauthorizedAccessException>().Or.InstanceOf<InvalidOperationException>().Or.InstanceOf<InvalidDataException>());
  }

  [Test]
  public void Given_7ZipEmptyArchive_When_Read_Then_ItListsNothing() {
    var archive = Path.Combine(this._root, "empty.7z");
    SevenZipOracle.RunOk(this._source, ["a", "-y", archive, "deep/small.txt"]);
    SevenZipOracle.RunOk(this._source, ["d", "-y", archive, "deep/small.txt"]);

    using var stream = File.OpenRead(archive);
    using var reader = new SevenZipReader(stream);
    Assert.That(reader.Entries, Is.Empty);
  }

  [Test]
  public void Given_7ZipArchiveWithManyFiles_When_Read_Then_EveryEntryMatches() {
    var source = Path.Combine(this._root, "many");
    var tree = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal);
    for (var d = 0; d < 20; ++d) {
      tree[$"d{d:D2}"] = null;
      for (var f = 0; f < 250; ++f)
        tree[$"d{d:D2}/file_{f:D4}_with_a_longer_name.txt"] = System.Text.Encoding.ASCII.GetBytes($"{d}/{f} {new string('x', f % 37)}");
    }
    SevenZipOracle.Materialize(source, tree);
    var archive = Path.Combine(this._root, "many.7z");
    SevenZipOracle.RunOk(source, ["a", "-y", "-r", archive, "*"]);

    var actual = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal);
    using (var stream = File.OpenRead(archive))
    using (var reader = new SevenZipReader(stream))
      for (var i = 0; i < reader.Entries.Count; ++i)
        actual[reader.Entries[i].Name.Replace('\\', '/')] = reader.Entries[i].IsDirectory ? null : reader.Extract(i);
    SevenZipOracle.AssertSameTree(tree, actual, "many files");
  }

  [Test]
  public void Given_7ZipArchive_When_Read_Then_TimesAndAttributesMatchTheSource() {
    var source = Path.Combine(this._root, "meta");
    SevenZipOracle.Materialize(source, new Dictionary<string, byte[]?> { ["ro.txt"] = "read only"u8.ToArray(), ["dir"] = null, ["dir/plain.txt"] = "plain"u8.ToArray() });
    var ro = Path.Combine(source, "ro.txt");
    var plain = Path.Combine(source, "dir", "plain.txt");
    var mtime = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234567);
    var ctime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(7654321);
    var atime = new DateTime(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc).AddTicks(1111111);
    foreach (var f in new[] { ro, plain }) {
      File.SetLastWriteTimeUtc(f, mtime);
      File.SetCreationTimeUtc(f, ctime);
      File.SetLastAccessTimeUtc(f, atime);
    }
    File.SetAttributes(ro, FileAttributes.ReadOnly | FileAttributes.Archive);
    var archive = Path.Combine(this._root, "meta.7z");
    SevenZipOracle.RunOk(source, ["a", "-y", "-r", archive, "-mtc=on", "-mta=on", "*"]);
    File.SetAttributes(ro, FileAttributes.Normal);

    using var stream = File.OpenRead(archive);
    using var reader = new SevenZipReader(stream);
    var byName = reader.Entries.ToDictionary(e => e.Name.Replace('\\', '/'));
    Assert.That(byName["ro.txt"].LastWriteTime, Is.EqualTo(mtime));
    Assert.That(byName["dir/plain.txt"].LastWriteTime, Is.EqualTo(mtime));
    if (OperatingSystem.IsWindows()) {
      Assert.That(byName["ro.txt"].CreationTime, Is.EqualTo(ctime));
      Assert.That(byName["ro.txt"].Attributes & 0xFFFF, Is.EqualTo((uint)(FileAttributes.ReadOnly | FileAttributes.Archive)));
      Assert.That(byName["dir"].Attributes & (uint)FileAttributes.Directory, Is.EqualTo((uint)FileAttributes.Directory));
    }
    Assert.That(byName["ro.txt"].LastAccessTime, Is.Not.Null);
  }

  [Test]
  public void Given_7ZipSfx_When_Read_Then_TheArchiveBehindTheStubExtracts() {
    var module = Path.Combine(Path.GetDirectoryName(SevenZipOracle.Require())!, "7zCon.sfx");
    if (!File.Exists(module))
      Assert.Ignore("7zCon.sfx is not shipped with this 7-Zip.");
    var archive = Path.Combine(this._root, "sfx.exe");
    SevenZipOracle.RunOk(this._source, ["a", "-y", "-r", $"-sfx{module}", archive, "*"]);

    var actual = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal);
    using (var stream = File.OpenRead(archive))
    using (var reader = new SevenZipReader(stream))
      for (var i = 0; i < reader.Entries.Count; ++i)
        actual[reader.Entries[i].Name.Replace('\\', '/')] = reader.Entries[i].IsDirectory ? null : reader.Extract(i);
    SevenZipOracle.AssertSameTree(this._tree, actual, "sfx");
  }

  [Test]
  public void Given_7ZipUpdateArchive_When_Read_Then_AntiItemsAreReportedAndNotExtracted() {
    var source = Path.Combine(this._root, "anti");
    SevenZipOracle.Materialize(source, new Dictionary<string, byte[]?> { ["keep.txt"] = "keep"u8.ToArray(), ["gone.txt"] = "gone"u8.ToArray(), ["gonedir"] = null });
    var basis = Path.Combine(this._root, "anti-base.7z");
    SevenZipOracle.RunOk(source, ["a", "-y", "-r", basis, "*"]);
    File.Delete(Path.Combine(source, "gone.txt"));
    Directory.Delete(Path.Combine(source, "gonedir"));
    File.WriteAllBytes(Path.Combine(source, "new.txt"), "new"u8.ToArray());
    var diff = Path.Combine(this._root, "anti-diff.7z");
    // Differential update: write only what changed into diff, with anti-items for what vanished.
    SevenZipOracle.RunOk(source, ["u", "-y", "-r", basis, "-u-", $"-up0q3r2x2y2z0w2!{diff}", "*"]);

    using var stream = File.OpenRead(diff);
    using var reader = new SevenZipReader(stream);
    var byName = reader.Entries.ToDictionary(e => e.Name.Replace('\\', '/'));
    Assert.That(byName.Keys, Does.Contain("gone.txt").And.Contain("gonedir").And.Contain("new.txt"));
    Assert.That(byName["gone.txt"].IsAnti, Is.True);
    Assert.That(byName["gonedir"].IsAnti, Is.True);
    Assert.That(byName["new.txt"].IsAnti, Is.False);
    var index = reader.Entries.ToList().FindIndex(e => e.Name == "new.txt");
    Assert.That(reader.Extract(index), Is.EqualTo("new"u8.ToArray()));
  }
}
