#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// dar as the oracle for edits of archives it made from a real ext4 tree: user extended attributes
/// on files and directories, Linux FSA (dar records them for every inode on ext4; <c>chattr +d</c>
/// sets one), a hard-linked file with three names, a symlink, an empty file and an empty directory.
/// We replace a file, add one, remove one and remove the name of the hard-linked file that dar's
/// catalogue carries the inode under; <c>dar -t</c> must accept the result and <c>dar -x</c> must
/// restore a tree whose contents, modes, file times, extended attributes (<c>getfattr</c>), FSA
/// flags (<c>lsattr</c>) and hard-link groups (<c>stat</c>) equal the source tree edited the same
/// way. Needs dar, attr and e2fsprogs, and an ext2/3/4 <c>/tmp</c> (WSL's root file system).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class DarEditAttributesOracleTests {
  private const string Password = "attr-pass";
  private static readonly string DarBinary = Environment.GetEnvironmentVariable("CWB_DAR") is { Length: > 0 } custom ? custom : "dar";
  private static readonly byte[] Added = "added by an edit\n"u8.ToArray();
  private string _tmp = null!;
  private string? _scratch;

  [SetUp]
  public void SetUp() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(DarBinary))
      Assert.Ignore($"'{DarBinary}' is not installed (sudo apt-get install -y dar).");
    foreach (var tool in new[] { "setfattr", "getfattr", "chattr", "lsattr" })
      if (!FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"'{tool}' is not installed (sudo apt-get install -y attr e2fsprogs).");
    this._tmp = DarFixtures.TempDirectory("editattr");
    var made = FsInteropToolbox.RunWsl("mktemp -d /tmp/cwb-darattr-XXXXXX");
    Assert.That(made.ExitCode, Is.Zero, made.StdErr);
    this._scratch = made.StdOut.Trim();
  }

  [TearDown]
  public void TearDown() {
    if (this._scratch is { Length: > 0 } scratch)
      FsInteropToolbox.RunWsl(FsInteropToolbox.RemoveScratch($"'{scratch}'"));
    try { if (this._tmp != null) Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  /// <summary>Runs <paramref name="script"/> through bash from a file, so no shell quoting is in the way.</summary>
  private (string StdOut, string StdErr, int ExitCode) Bash(string script) {
    var path = Path.Combine(this._tmp, $"step-{Guid.NewGuid():N}.sh");
    File.WriteAllText(path, (ManifestFunction + script).Replace("\r\n", "\n"), new UTF8Encoding(false));
    var r = FsInteropToolbox.RunWsl($"bash {FsInteropToolbox.WinToWsl(path)}");
    TestContext.Out.WriteLine($"{script}\n-> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  /// <summary>
  /// <c>manifest dir [regex]</c>: one line per entry below <c>dir</c> — type, mode, link count,
  /// content hash and modification time of files (not for paths matching <c>regex</c>), the
  /// lexically first name of a file's hard-link group, symlink targets, the FSA flags lsattr shows
  /// and the user extended attributes getfattr shows.
  /// </summary>
  private const string ManifestFunction = """
    manifest() {
      ( cd "$1" || exit 1
        find . -mindepth 1 -printf '%P\n' | LC_ALL=C sort | while IFS= read -r p; do
          IFS='|' read -r type mode links mtime <<< "$(stat -c '%F|%a|%h|%Y' -- "$p")"
          line="$p|$type|$mode|$links"
          if [ -L "$p" ]; then
            line="$line|target=$(readlink -- "$p")"
          else
            if [ -f "$p" ]; then
              line="$line|$(sha256sum -- "$p" | cut -c1-16)"
              [[ -n "$2" && "$p" =~ $2 ]] || line="$line|mtime=$mtime"
              [ "$links" -gt 1 ] && line="$line|group=$(find . -samefile "$p" -printf '%P\n' | LC_ALL=C sort | head -1)"
            fi
            line="$line|fsa=$(lsattr -d -- "$p" 2>/dev/null | cut -d' ' -f1)"
            line="$line|ea=$(getfattr -h -d -m '^user\.' -- "$p" 2>/dev/null | grep -v '^#' | grep -v '^$' | LC_ALL=C sort | tr '\n' ';')"
          fi
          echo "$line"
        done )
    }

    """;

  /// <summary>Builds the source tree under the scratch directory; exit 77 when /tmp is not ext2/3/4.</summary>
  private string BuildSource() {
    var src = $"{this._scratch}/src";
    var r = this.Bash($"""
      set -e
      case "$(stat -f -c %T /tmp)" in ext2/ext3) ;; *) echo "/tmp is $(stat -f -c %T /tmp)"; exit 77 ;; esac
      mkdir -p '{src}/sub/deep' '{src}/emptydir'
      cd '{src}'
      printf 'hello dar\n' > a.txt
      setfattr -n user.color -v blue a.txt
      setfattr -n user.empty a.txt
      printf 'to be replaced\n' > hello.txt
      setfattr -n user.old -v 1 hello.txt
      for i in $(seq 1 60); do printf 'The quick brown fox jumps over the lazy dog. '; done > text.txt
      setfattr -n user.gone -v soon text.txt
      head -c 40000 /dev/urandom > big.bin
      printf 'linked content\n' > sub/h1
      ln sub/h1 h2
      ln sub/h1 sub/h3
      setfattr -n user.link -v yes sub/h1
      chattr +d sub/h1
      printf 'plain\n' > sub/plain
      : > sub/deep/empty.bin
      setfattr -n user.kind -v empty sub/deep/empty.bin
      setfattr -n user.dir -v d sub
      chattr +d sub
      ln -s a.txt link
      touch -d '2001-02-03 04:05:06' a.txt big.bin
      """);
    if (r.ExitCode == 77)
      Assert.Ignore($"No ext2/3/4 scratch file system for FSA: {r.StdOut}");
    Assert.That(r.ExitCode, Is.Zero, r.StdErr);
    return src;
  }

  private static string KeyArgument(string options) => options.Contains("-K ") ? $"-K 'aes256:{Password}'" : "";

  [TestCase("", TestName = "GivenDarsDefaultArchiveOfAnExt4TreeWithXattrsFsaAndHardLinks_WhenWeEditIt_ThenDarRestoresContentAndAttributes")]
  [TestCase("-at", TestName = "GivenDarArchiveWithoutTapeMarksOfAnExt4Tree_WhenWeEditIt_ThenDarRestoresContentAndAttributes")]
  [TestCase("-zgzip", TestName = "GivenDarGzipArchiveOfAnExt4Tree_WhenWeEditIt_ThenDarRestoresContentAndAttributes")]
  [TestCase("-at -zbzip2", TestName = "GivenDarBzip2ArchiveWithoutTapeMarksOfAnExt4Tree_WhenWeEditIt_ThenDarRestoresContentAndAttributes")]
  [TestCase("-zzstd:6:16k", TestName = "GivenDarBlockCompressedArchiveOfAnExt4Tree_WhenWeEditIt_ThenDarRestoresContentAndAttributes")]
  [TestCase("-zxz -at", TestName = "GivenDarXzArchiveOfAnExt4Tree_WhenWeEditIt_ThenDarRestoresContentAndAttributes")]
  [TestCase("-s 16k -zlz4", TestName = "GivenDarLz4SliceSetOfAnExt4Tree_WhenWeEditIt_ThenDarRestoresContentAndAttributes")]
  [TestCase("-K aes", TestName = "GivenDarAesArchiveOfAnExt4Tree_WhenWeEditItWithThePassword_ThenDarRestoresContentAndAttributes")]
  public void DarArchiveOfAnExt4TreeEdited(string options) {
    var src = this.BuildSource();
    var basePath = Path.Combine(this._tmp, "theirs");
    var key = KeyArgument(options);
    var darOptions = key.Length > 0 ? key : options;
    var create = this.Bash($"{DarBinary} -Q -c {FsInteropToolbox.WinToWsl(basePath)} {darOptions} -R '{src}'");
    Assert.That(create.ExitCode, Is.Zero, $"dar -c failed:\n{create.StdOut}\n{create.StdErr}");
    var slices = Enumerable.Range(1, 1000).Select(n => $"{basePath}.{n}.dar").TakeWhile(File.Exists).ToArray();
    if (options.Contains("-s "))
      Assert.That(slices, Has.Length.GreaterThan(1), "the archive should span several slices");

    var password = key.Length > 0 ? Password : null;
    string inodeName;
    using (var fs = File.OpenRead(slices[0]))
    using (var set = DarSliceSet.Open(fs)) {
      var entries = DarArchive.Read(set.Archive, password).Entries;
      // what the edit has to carry: EAs, FSA on every inode, one inode under three names
      Assert.That(entries.Single(e => e.Path == "a.txt").HasExtendedAttributes, Is.True);
      Assert.That(entries.Single(e => e.Path == "sub").HasExtendedAttributes, Is.True);
      Assert.That(entries.Where(e => e.Kind != DarEntryKind.Symlink).All(e => e.HasFilesystemAttributes), Is.True);
      Assert.That(entries.Count(e => e.HardLinkLabel is not null), Is.EqualTo(3));
      // the name dar wrote the inode under: removing it moves the inode to another name
      inodeName = entries.Single(e => e.IsHardLinkInode).Path;
    }

    using (var fs = new FileStream(slices[0], FileMode.Open, FileAccess.ReadWrite)) {
      var mutation = new ArchiveMutationOptions { Password = password };
      new DarFormatDescriptor().Add(fs, [ArchiveInputInfo.InMemory("hello.txt", Added), ArchiveInputInfo.InMemory("new/added.txt", Added)], mutation);
      new DarFormatDescriptor().Remove(fs, ["text.txt", inodeName], mutation);
    }

    var darBase = FsInteropToolbox.WinToWsl(basePath);
    var test = this.Bash($"{DarBinary} -Q -t {darBase} {key}");
    Assert.That(test.ExitCode, Is.Zero, $"dar -t refused the edited archive:\n{test.StdOut}\n{test.StdErr}");
    if (!options.Contains("-at") && !options.Contains("-s ")) {
      var sequential = this.Bash($"{DarBinary} -Q -t {darBase} {key} --sequential-read");
      Assert.That(sequential.ExitCode, Is.Zero, $"dar -t --sequential-read refused the edited archive:\n{sequential.StdOut}\n{sequential.StdErr}");
    }
    var restored = $"{this._scratch}/out";
    var extract = this.Bash($"mkdir '{restored}' && {DarBinary} -Q -O -x {darBase} {key} -R '{restored}'");
    Assert.That(extract.ExitCode, Is.Zero, $"dar -x failed on the edited archive:\n{extract.StdOut}\n{extract.StdErr}");

    var compare = this.Bash($"""
      cd '{src}' && rm text.txt '{inodeName}' hello.txt && printf 'added by an edit\n' > hello.txt && mkdir new && printf 'added by an edit\n' > new/added.txt
      skip='^(hello\.txt|new/added\.txt)$'
      manifest '{src}' "$skip" > '{this._scratch}/expected.txt'
      manifest '{restored}' "$skip" > '{this._scratch}/actual.txt'
      cat '{this._scratch}/actual.txt'
      diff '{this._scratch}/expected.txt' '{this._scratch}/actual.txt'
      """);
    Assert.That(compare.ExitCode, Is.Zero, $"the restored tree differs from the edited source (diff expected actual):\n{compare.StdOut}");
    var pair = compare.StdOut.Split('\n').Where(l => l.Contains("|group=")).ToArray();
    Assert.That(pair, Has.Length.EqualTo(2), "the two surviving names of the hard-linked file");
    Assert.That(pair, Has.All.Contain("|regular file|644|2|").And.All.Contain("|fsa=------d").And.All.Contain("user.link=\"yes\""));
    Assert.That(compare.StdOut, Does.Contain("user.color=\"blue\"").And.Contain("sub|directory|755|3|fsa=------d"));
  }
}
