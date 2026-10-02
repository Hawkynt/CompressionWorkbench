using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FileSystem.HfsPlus;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Our HFS+ reader judged by an independent one: libyal's libfshfs (LGPL), through its
/// <c>pyfshfs</c> Python binding (<c>pip install libfshfs-python</c>), over the volume hdiutil
/// and macOS wrote into keramics' <c>hfsplus.sparsebundle</c>. Every entry libfshfs shows must be
/// listed with the same kind and size, and every regular file must read the same bytes — the hard
/// link through both of its names. The metadata directories libfshfs shows are the one intended
/// difference: they are hidden here, their files reached through the links. Ignored where no
/// Python with pyfshfs is on the PATH.
/// </summary>
/// <remarks>
/// Measured with libfshfs-python 20260922 on Windows: <c>volume.open(path)</c> mangles an
/// absolute Windows path, so the script hands it a file object. libfshfs shows a catalog '/' as
/// ':' and U+0000 as U+2400, the convention this reader follows.
/// </remarks>
[TestFixture]
[Category("ArchiveExternal")]
public sealed class HfsPlusLibfshfsOracleTests {

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_hfsfshfs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static readonly Lazy<string?> Python = new(() => {
    foreach (var candidate in new[] { "python3", "python" }) {
      try {
        if (Run(candidate, ["-c", "import pyfshfs"]).ExitCode == 0) return candidate;
      } catch (Win32Exception) {
        // not on PATH
      }
    }
    return null;
  });

  /// <summary>One JSON object per entry: path, kind, size, sha256 of a regular file's bytes.</summary>
  private const string ListScript = """
    import hashlib, json, sys, pyfshfs
    volume = pyfshfs.volume()
    volume.open_file_object(open(sys.argv[1], 'rb'))
    def walk(entry, prefix):
        for index in range(entry.number_of_sub_file_entries):
            sub = entry.get_sub_file_entry(index)
            path = prefix + sub.name
            kind = sub.file_mode & 0o170000
            size = sub.size or 0
            digest = hashlib.sha256(sub.read_buffer(size) if kind == 0o100000 and size else b'').hexdigest()
            print(json.dumps({'path': path, 'dir': kind == 0o040000, 'size': size, 'sha256': digest if kind == 0o100000 else None}))
            if kind == 0o040000:
                walk(sub, path + '/')
    walk(volume.get_root_directory(), '')
    """;

  private static (int ExitCode, string StdOut, string StdErr) Run(string file, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(file) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result, stderr);
  }

  private sealed record OracleEntry(string Path, bool Dir, long Size, string? Sha256);

  private static List<OracleEntry> Libfshfs(string volume) {
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with pyfshfs on the PATH (`pip install libfshfs-python`).");
    var (code, stdout, stderr) = Run(python!, ["-c", ListScript, volume]);
    Assert.That(code, Is.EqualTo(0), $"libfshfs could not read the volume:\n{stderr}");
    return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(static line => JsonDocument.Parse(line).RootElement)
      .Select(static e => new OracleEntry(e.GetProperty("path").GetString()!, e.GetProperty("dir").GetBoolean(),
        e.GetProperty("size").GetInt64(), e.GetProperty("sha256").GetString()))
      .ToList();
  }

  /// <summary>Every entry libfshfs shows (metadata directories aside) is ours, with the same kind, size and bytes.</summary>
  private static void AssertSameAsLibfshfs(string volume, bool keramicsHardLink = true) {
    var oracle = Libfshfs(volume)
      .Where(static e => !e.Path.StartsWith("\u2400\u2400\u2400\u2400HFS+ Private Data", StringComparison.Ordinal)
                         && !e.Path.StartsWith(".HFS+ Private Directory Data\r", StringComparison.Ordinal))
      .ToList();
    using var reader = new HfsPlusReader(File.OpenRead(volume));
    var ours = reader.Entries.ToDictionary(static e => e.FullPath, StringComparer.Ordinal);

    Assert.That(ours.Keys, Is.EquivalentTo(oracle.Select(static e => e.Path)), "listing");
    Assert.Multiple(() => {
      foreach (var expected in oracle) {
        var entry = ours[expected.Path];
        Assert.That(entry.IsDirectory, Is.EqualTo(expected.Dir), expected.Path);
        if (!expected.Dir) Assert.That(entry.Size, Is.EqualTo(expected.Size), expected.Path);
        if (expected.Sha256 is { } sha256)
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.Extract(entry))), Is.EqualTo(sha256), expected.Path);
      }
      if (keramicsHardLink)
        Assert.That(oracle.Single(static e => e.Path == "file_hardlink1").Sha256,
          Is.EqualTo(oracle.Single(static e => e.Path == "testdir1/testfile1").Sha256), "libfshfs resolves the hard link too");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheHdiutilVolume_WhenLibfshfsReadsIt_ThenOurReaderListsAndReadsTheSame() {
    var volume = Path.Combine(this._tmp, "hfsplus.img");
    File.WriteAllBytes(volume, HfsPlusLinksAndNamesTests.KeramicsVolume(this._tmp));
    AssertSameAsLibfshfs(volume);
  }

  [Test, Category("HappyPath")]
  public void GivenTheVolumeWithDecmpfsFiles_WhenLibfshfsReadsIt_ThenOurReaderDecodesTheSameBytes() {
    var volume = Path.Combine(this._tmp, "hfsplus.raw");
    File.WriteAllBytes(volume, HfsPlusDecmpfsTests.KeramicsRaw());
    AssertSameAsLibfshfs(volume);
  }

  /// <summary>
  /// Our writer's transparently compressed files, read by libfshfs: every file must list with its
  /// real size and read its exact bytes, inline and chunked, stored and encoded chunks alike.
  /// libfshfs 20260922 implements decmpfs methods 3/4, 7/8, 9/10, 11/12 and 5; it has no method 1
  /// and no LZBITMAP (13/14), so those two writer modes have no independent HFS+ reader here —
  /// see <see cref="GivenAMethodLibfshfsDoesNotImplement_WhenItReadsOurVolume_ThenItDoesNotClaimTheContent"/>.
  /// </summary>
  [TestCase(HfsPlusCompression.Zlib, TestName = "GivenOurZlibVolume_WhenLibfshfsReadsIt_ThenEveryFileIsItsContent")]
  [TestCase(HfsPlusCompression.Lzvn, TestName = "GivenOurLzvnVolume_WhenLibfshfsReadsIt_ThenEveryFileIsItsContent")]
  [TestCase(HfsPlusCompression.Lzfse, TestName = "GivenOurLzfseVolume_WhenLibfshfsReadsIt_ThenEveryFileIsItsContent")]
  [TestCase(HfsPlusCompression.Raw, TestName = "GivenOurRawVolume_WhenLibfshfsReadsIt_ThenEveryFileIsItsContent")]
  [Category("HappyPath")]
  public void LibfshfsReadsOurCompressedVolume(HfsPlusCompression compression) {
    var volume = Path.Combine(this._tmp, $"{compression}.img");
    File.WriteAllBytes(volume, HfsPlusDecmpfsMethodsTests.CompressedVolume(compression));
    var oracle = Libfshfs(volume).Where(static e => !e.Dir).ToDictionary(static e => e.Path);
    Assert.Multiple(() => {
      foreach (var (name, content) in HfsPlusDecmpfsMethodsTests.Files()) {
        Assert.That(oracle.TryGetValue(name, out var entry), Is.True, name);
        Assert.That(entry!.Size, Is.EqualTo(content.Length), name);
        Assert.That(entry.Sha256, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(content))), name);
      }
    });
    AssertSameAsLibfshfs(volume, keramicsHardLink: false);
  }

  /// <summary>
  /// For a method libfshfs does not implement it must not report content it cannot decode:
  /// either it refuses the file or the bytes differ. This records the absence of an oracle rather
  /// than claiming one.
  /// </summary>
  [TestCase(HfsPlusCompression.Lzbitmap, TestName = "GivenOurLzbitmapVolume_WhenLibfshfsReadsIt_ThenItHasNoDecoderForIt")]
  [TestCase(HfsPlusCompression.InlineUncompressed, TestName = "GivenOurMethodOneVolume_WhenLibfshfsReadsIt_ThenItHasNoDecoderForIt")]
  [Category("BoundaryCase")]
  public void GivenAMethodLibfshfsDoesNotImplement_WhenItReadsOurVolume_ThenItDoesNotClaimTheContent(HfsPlusCompression compression) {
    var volume = Path.Combine(this._tmp, $"{compression}.img");
    File.WriteAllBytes(volume, HfsPlusDecmpfsMethodsTests.CompressedVolume(compression));
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with pyfshfs on the PATH (`pip install libfshfs-python`).");
    var (code, stdout, _) = Run(python!, ["-c", ListScript, volume]);
    var content = HfsPlusDecmpfsMethodsTests.Files().Single(static f => f.Name == "inline.txt").Content;
    var expected = Convert.ToHexStringLower(SHA256.HashData(content));
    Assert.That(code != 0 || !stdout.Contains(expected, StringComparison.Ordinal), Is.True,
      "libfshfs now decodes this method: turn this test into a byte-for-byte oracle comparison");
  }
}
