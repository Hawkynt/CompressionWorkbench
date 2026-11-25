#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// dar as the oracle for in-place edits: archives dar wrote (from a source on the Windows side,
/// without EAs or FSAs; <see cref="DarEditAttributesOracleTests"/> covers those) and archives we
/// wrote are edited — a file replaced, one
/// added, one removed — and must then pass <c>dar -t</c> (sequentially too when they carry tape
/// marks) and restore the edited tree byte for byte with <c>dar -x</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class DarEditOracleTests {
  private const string Password = "s3cret pass";
  private static readonly string DarBinary = Environment.GetEnvironmentVariable("CWB_DAR") is { Length: > 0 } custom ? custom : "dar";
  private static readonly byte[] Added = "added by an edit\n"u8.ToArray();
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(DarBinary))
      Assert.Ignore($"'{DarBinary}' is not installed (sudo apt-get install -y dar).");
    this._tmp = DarFixtures.TempDirectory("editoracle");
  }

  [TearDown]
  public void TearDown() {
    try { if (this._tmp != null) Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static (string StdOut, string StdErr, int ExitCode) Dar(string arguments) {
    var r = FsInteropToolbox.RunWsl($"{DarBinary} -Q {arguments}");
    TestContext.Out.WriteLine($"{DarBinary} -Q {arguments} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  /// <summary>The source tree on the Windows side (no FSA in WSL), with a sparse file for dar's hole records.</summary>
  private string Source() {
    var src = Path.Combine(this._tmp, "src");
    foreach (var item in DarEncryptionTests.Items().Where(i => i.Path != "hard.txt")) {
      var full = Path.Combine(src, item.Path);
      Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      using var input = item.Open!();
      using var output = File.Create(full);
      input.CopyTo(output);
    }
    Directory.CreateDirectory(Path.Combine(src, "emptydir"));
    return src;
  }

  /// <summary>The source after the edit: text.txt gone, hello.txt replaced, new/added.txt added.</summary>
  private static void ApplyEditTo(string src) {
    File.Delete(Path.Combine(src, "text.txt"));
    File.WriteAllBytes(Path.Combine(src, "hello.txt"), Added);
    Directory.CreateDirectory(Path.Combine(src, "new"));
    File.WriteAllBytes(Path.Combine(src, "new", "added.txt"), Added);
  }

  private static void EditArchive(string slicePath, string? password) {
    using var fs = new FileStream(slicePath, FileMode.Open, FileAccess.ReadWrite);
    var options = new ArchiveMutationOptions { Password = password };
    new DarFormatDescriptor().Add(fs, [ArchiveInputInfo.InMemory("hello.txt", Added), ArchiveInputInfo.InMemory("new/added.txt", Added)], options);
    new DarFormatDescriptor().Remove(fs, ["text.txt"], options);
  }

  private void AssertDarRestores(string basePath, string expectedRoot, string key, bool sequential) {
    var darBase = FsInteropToolbox.WinToWsl(basePath);
    var test = Dar($"-t {darBase} {key}");
    Assert.That(test.ExitCode, Is.Zero, $"dar -t refused the edited archive:\n{test.StdOut}\n{test.StdErr}");
    if (sequential) {
      var seq = Dar($"-t {darBase} {key} --sequential-read");
      Assert.That(seq.ExitCode, Is.Zero, $"dar -t --sequential-read refused the edited archive:\n{seq.StdOut}\n{seq.StdErr}");
    }
    var outDir = Path.Combine(this._tmp, "out");
    Directory.CreateDirectory(outDir);
    var extract = Dar($"-O -x {darBase} {key} -R {FsInteropToolbox.WinToWsl(outDir)}");
    Assert.That(extract.ExitCode, Is.Zero, $"dar -x failed on the edited archive:\n{extract.StdOut}\n{extract.StdErr}");
    var expected = Directory.EnumerateFiles(expectedRoot, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(expectedRoot, f)).Order(StringComparer.Ordinal).ToArray();
    var actual = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(outDir, f)).Order(StringComparer.Ordinal).ToArray();
    Assert.That(actual, Is.EqualTo(expected));
    foreach (var rel in expected)
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, rel)), Is.EqualTo(File.ReadAllBytes(Path.Combine(expectedRoot, rel))), rel);
    Assert.That(Directory.Exists(Path.Combine(outDir, "emptydir")), "the empty directory");
  }

  [TestCase("", TestName = "GivenDarsDefaultArchive_WhenWeEditIt_ThenDarTestsAndRestoresTheEdit")]
  [TestCase("-at -zgzip", TestName = "GivenDarGzipArchiveWithoutTapeMarks_WhenWeEditIt_ThenDarRestoresTheEdit")]
  [TestCase("-zzstd:6:16k", TestName = "GivenDarBlockCompressedArchive_WhenWeEditIt_ThenDarRestoresTheEdit")]
  [TestCase("-zxz", TestName = "GivenDarXzArchive_WhenWeEditIt_ThenDarRestoresTheEdit")]
  [TestCase("-s 16k -S 25k -zbzip2", TestName = "GivenDarSliceSet_WhenWeEditIt_ThenDarRestoresTheEdit")]
  [TestCase("-K aes256:" + Password, TestName = "GivenDarAesArchive_WhenWeEditItWithThePassword_ThenDarRestoresTheEdit")]
  [TestCase("-K scrambling:" + Password + " -s 20k", TestName = "GivenDarScrambledSliceSet_WhenWeEditItWithThePassword_ThenDarRestoresTheEdit")]
  public void DarArchiveEdited(string options) {
    var src = this.Source();
    var basePath = Path.Combine(this._tmp, "theirs");
    // the password holds a space: quote the -K value for the shell
    var keyed = options;
    var keyArgument = "";
    if (options.Contains("-K ")) {
      var start = options.IndexOf("-K ", StringComparison.Ordinal) + 3;
      var end = options.IndexOf(" -", start, StringComparison.Ordinal);
      var value = end < 0 ? options[start..] : options[start..end];
      keyArgument = $"-K '{value}'";
      keyed = options.Replace("-K " + value, keyArgument, StringComparison.Ordinal);
    }
    var create = Dar($"-c {FsInteropToolbox.WinToWsl(basePath)} {keyed} -R {FsInteropToolbox.WinToWsl(src)}");
    Assert.That(create.ExitCode, Is.Zero, $"dar -c failed:\n{create.StdOut}\n{create.StdErr}");
    var slices = Enumerable.Range(1, 1000).Select(n => $"{basePath}.{n}.dar").TakeWhile(File.Exists).ToArray();
    if (options.Contains("-s "))
      Assert.That(slices, Has.Length.GreaterThan(2), "the archive should span several slices");

    using (var fs = File.OpenRead(slices[0]))
    using (var set = DarSliceSet.Open(fs)) {
      // dar finds the zeros of sparse.bin and stores hole records: the edit carries them over as they are
      var sparse = DarArchive.Read(set.Archive, keyArgument.Length > 0 ? Password : null).Entries.Single(e => e.Path == "sparse.bin");
      Assert.That(sparse.IsSparse, Is.True);
    }
    EditArchive(slices[0], keyArgument.Length > 0 ? Password : null);
    ApplyEditTo(src);
    this.AssertDarRestores(basePath, src, keyArgument, sequential: !options.Contains("-at") && !options.Contains("-s "));
  }

  [TestCase('z', true, 0)]
  [TestCase('d', false, 0)]
  [TestCase('n', true, 6000)]
  public void GivenOurArchive_WhenWeEditIt_ThenDarTestsAndRestoresTheEdit(char algorithm, bool tapeMarks, int sliceSize) {
    var src = this.Source();
    var inputs = Directory.EnumerateFileSystemEntries(src, "*", SearchOption.AllDirectories)
      .Select(p => new ArchiveInputInfo(p, Path.GetRelativePath(src, p).Replace('\\', '/'), Directory.Exists(p))).ToList();
    var basePath = Path.Combine(this._tmp, "ours");
    using (var fs = File.Create(basePath + ".1.dar"))
      new DarFormatDescriptor().Create(fs, inputs, new FormatCreateOptions {
        MethodName = algorithm switch { 'z' => "gzip", 'd' => "zstd", _ => "stored" },
        Password = Password, EncryptionMethod = "camellia256",
        FormatSpecific = {
          [DarFormatDescriptor.TapeMarksOption] = tapeMarks.ToString(),
          [DarFormatDescriptor.SliceSizeOption] = sliceSize > 0 ? sliceSize.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
          [DarFormatDescriptor.KdfHashOption] = "sha1", [DarFormatDescriptor.KdfIterationsOption] = "1000",
        },
      });
    EditArchive(basePath + ".1.dar", Password);
    ApplyEditTo(src);
    this.AssertDarRestores(basePath, src, $"-K 'camellia256:{Password}'", sequential: tapeMarks && sliceSize == 0);
  }
}
