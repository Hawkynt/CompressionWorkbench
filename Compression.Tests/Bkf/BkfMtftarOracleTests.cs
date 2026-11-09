using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Bkf;

namespace Compression.Tests.Bkf;

/// <summary>
/// Third-party proof for BKF in both directions. mtftar (the MTF-to-tar filter, GPL, run only
/// as an external program) converts what we write and what we edit into a tar that must hold
/// exactly our folders and files with their bytes; and a backup NTBackup wrote on Windows 2000
/// must read the same through our reader as through mtftar.
/// </summary>
/// <remarks>
/// <para>mtftar runs from the PATH, inside WSL, or from <c>CWB_MTFTAR</c>. Built from
/// https://github.com/sjmurdoch/mtftar; with MinGW it needs <c>-D__APPLE__ -DO_NOCTTY=0</c>,
/// empty <c>features.h</c>/<c>sys/ioctl.h</c> stubs and a force-included header that adds
/// <c>_O_BINARY</c> to <c>O_RDONLY</c>/<c>O_WRONLY</c> (in text mode a 0x1A byte ends the
/// read).</para>
/// <para>The NTBackup file is <c>axp64-boot.bkf</c> from the Internet Archive item
/// <c>royt-axp64-backup-files</c> (1,064,960 bytes, SHA-256 b4b34ab0…ad7e46): it holds
/// operating-system files, so it is not committed. Point <c>CWB_BKF_SAMPLE</c> at it. The
/// manifest below is what mtftar extracts from it.</para>
/// </remarks>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
public sealed class BkfMtftarOracleTests {
  private const string SampleSha256 = "b4b34ab05b183db4d3ac7a9d4d8a40e584bcedb2ea9f306a51acc82dedad7e46";

  /// <summary>mtftar's view of axp64-boot.bkf: path below the volume, size, SHA-256.</summary>
  private static readonly (string Path, long Size, string Sha256)[] SampleManifest = [
    ("os/winnt50C/a121165.p64", 38400, "55fdf20cdc23e4fade36a3c38cf77ce503a11427004d1e7face1d2378baccc01"),
    ("os/winnt50C/a221164.p64", 38912, "8d2b932f3ecba6c2262418fff43e20b9e276d929af97eab958b8f6c5ef239a6b"),
    ("os/winnt50C/a221264.p64", 41984, "b62dcb064a836d1ddd079fdc2c8843b2daa076c70b6a0cacdf6379d476db3952"),
    ("os/winnt50C/osloader.exe", 633856, "d5d7ceb44df1f6a5b4c8561654d363ebd101fdb6175ca588f3189357a9269187"),
    ("os/winnt50C/hal.dll", 294272, "1f1b9f4ef5356407de55411e0d61cd252952ff924db6e2722edd8f94cb2bc256"),
  ];

  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_bkfmtf_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static byte[] Pattern(int size, int seed) {
    var d = new byte[size];
    new Random(seed).NextBytes(d);
    return d;
  }

  [Test]
  public void GivenABackupWeWroteAndEditedInFolders_WhenMtftarConvertsIt_ThenTheTarHoldsExactlyOurTree() {
    var mtftar = this.RequireMtftar();
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["top.txt"] = "top level"u8.ToArray(),
      ["docs/readme.txt"] = "inside docs"u8.ToArray(),
      ["docs/deep/data.bin"] = Pattern(70_000, 1),
      ["docs/deep/empty.bin"] = [],
      ["other/gone.txt"] = "removed later"u8.ToArray(),
      ["Mixed Case Folder/Long Name With Spaces.txt"] = "spaces"u8.ToArray(),
    };
    var bkf = new BkfFormatDescriptor();
    using var image = new MemoryStream();
    bkf.Create(image, [.. expected.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value))], new FormatCreateOptions());

    bkf.Add(image, [ArchiveInputInfo.InMemory("docs/deep/added.txt", "added later"u8.ToArray())]);
    bkf.Remove(image, ["other"]);
    expected["docs/deep/added.txt"] = "added later"u8.ToArray();
    expected.Remove("other/gone.txt");

    AssertTar(mtftar(this.Save("ours.bkf", image.ToArray())), expected, "mtftar on our backup");
  }

  [Test]
  public void GivenAnNtbackupLikeMediumWeEdited_WhenMtftarConvertsIt_ThenItReadsTheKeptAndTheNewFiles() {
    var mtftar = this.RequireMtftar();
    var m = new MtfFixture();
    m.Set(1, s => s.Volb("C:").Dirb("\0", security: true).File("boot.ini", "[boot loader]"u8.ToArray(), security: true)
      .Dirb("docs\0", security: true).File("a.txt", Pattern(5000, 2), security: true));
    using var image = new MemoryStream();
    image.Write(m.Build());

    BkfEditor.AddFile(image, "docs/new.txt", "new"u8.ToArray());
    BkfEditor.Remove(image, "boot.ini");

    AssertTar(mtftar(this.Save("edited.bkf", image.ToArray())), new Dictionary<string, byte[]> {
      ["docs/a.txt"] = Pattern(5000, 2),
      ["docs/new.txt"] = "new"u8.ToArray(),
    }, "mtftar on an edited medium");
  }

  [Test]
  public void GivenTheNtbackupSample_WhenReadByUsAndByMtftar_ThenBothMatchTheManifest() {
    var sample = RequireSample();
    using (var stream = File.OpenRead(sample))
    using (var reader = new BkfReader(stream)) {
      var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(files.Select(f => f.Name), Is.EqualTo(SampleManifest.Select(m => m.Path)), "our listing");
      foreach (var (path, size, sha) in SampleManifest) {
        var data = reader.Extract(files.Single(f => f.Name == path));
        Assert.That((data.LongLength, Convert.ToHexStringLower(SHA256.HashData(data))), Is.EqualTo((size, sha)), path);
      }
      Assert.That(reader.Entries.Where(e => e.IsDirectory).Select(e => e.Name), Is.EqualTo(new[] { "os", "os/winnt50C" }));
    }

    if (this.TryMtftar() is not { } mtftar) return;
    var tar = mtftar(sample);
    Assert.That(tar.Where(e => e.IsFile).Select(e => (Relative(e.Name), (long)e.Data.Length, Convert.ToHexStringLower(SHA256.HashData(e.Data)))),
      Is.EqualTo(SampleManifest.Select(m => (m.Path, m.Size, m.Sha256))), "mtftar on the sample");
  }

  [Test]
  public void GivenTheNtbackupSampleWeEdited_WhenMtftarConvertsIt_ThenItHoldsTheKeptAndTheAddedFiles() {
    var mtftar = this.RequireMtftar();
    var sample = RequireSample();
    using var image = new MemoryStream();
    image.Write(File.ReadAllBytes(sample));
    BkfEditor.AddFile(image, "os/added by edit.txt", "added"u8.ToArray());
    BkfEditor.Remove(image, "os/winnt50C/hal.dll");

    var tar = mtftar(this.Save("edited-sample.bkf", image.ToArray()));
    var files = tar.Where(e => e.IsFile).ToDictionary(e => Relative(e.Name), e => e.Data, StringComparer.Ordinal);
    Assert.That(files.Keys, Is.EquivalentTo(SampleManifest.Where(m => !m.Path.EndsWith("hal.dll", StringComparison.Ordinal)).Select(m => m.Path).Append("os/added by edit.txt")));
    foreach (var (path, size, sha) in SampleManifest.Where(m => files.ContainsKey(m.Path)))
      Assert.That((files[path].LongLength, Convert.ToHexStringLower(SHA256.HashData(files[path]))), Is.EqualTo((size, sha)), path);
    Assert.That(files["os/added by edit.txt"], Is.EqualTo("added"u8.ToArray()));
  }

  // ── Helpers ─────────────────────────────────────────────────────────────

  private sealed record Entry(string Name, bool IsFile, byte[] Data);

  private static string Relative(string name) {
    var n = name.Replace('\\', '/').TrimStart('/');
    var colon = n.IndexOf(':');
    return (colon >= 0 ? n[(colon + 1)..] : n).TrimStart('/');
  }

  private static void AssertTar(List<Entry> tar, IReadOnlyDictionary<string, byte[]> expected, string what) {
    var files = tar.Where(e => e.IsFile).ToDictionary(e => Relative(e.Name), e => e.Data, StringComparer.Ordinal);
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys), $"{what}:\n{string.Join("\n", tar.Select(e => e.Name))}");
    foreach (var (name, data) in expected)
      if (files.TryGetValue(name, out var got))
        Assert.That(got, Is.EqualTo(data), $"{what}: {name}");
  }

  private string Save(string name, byte[] bytes) {
    var path = Path.Combine(this._dir, name);
    File.WriteAllBytes(path, bytes);
    return path;
  }

  private static string RequireSample() {
    var path = Environment.GetEnvironmentVariable("CWB_BKF_SAMPLE");
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
      Assert.Ignore("CWB_BKF_SAMPLE does not name the NTBackup sample (axp64-boot.bkf).");
    using (var s = File.OpenRead(path))
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(s)), Is.EqualTo(SampleSha256), "the sample is axp64-boot.bkf");
    return path;
  }

  private Func<string, List<Entry>> RequireMtftar()
    => this.TryMtftar() ?? throw new IgnoreException("mtftar not found (PATH, WSL or CWB_MTFTAR).");

  private Func<string, List<Entry>>? TryMtftar() {
    var tool = Environment.GetEnvironmentVariable("CWB_MTFTAR");
    Func<string, string, (int ExitCode, string Error)>? run = null;
    if (!string.IsNullOrEmpty(tool) && File.Exists(tool)) run = (input, output) => Run(tool, input, output);
    else if (FindOnPath("mtftar") is { } onPath) run = (input, output) => Run(onPath, input, output);
    else if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("mtftar"))
      run = (input, output) => {
        var r = FsInteropToolbox.RunWsl($"mtftar -f {FsInteropToolbox.WinToWsl(input)} -o {FsInteropToolbox.WinToWsl(output)}");
        return (r.ExitCode, r.StdOut + r.StdErr);
      };
    if (run == null) return null;

    return input => {
      var output = Path.Combine(this._dir, Path.GetFileNameWithoutExtension(input) + ".tar");
      var (exit, error) = run(input, output);
      Assert.That(exit, Is.Zero, $"mtftar: {error}");
      var entries = new List<Entry>();
      using var reader = new TarReader(File.OpenRead(output));
      while (reader.GetNextEntry() is { } e) {
        using var data = new MemoryStream();
        e.DataStream?.CopyTo(data);
        entries.Add(new Entry(e.Name, e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile, data.ToArray()));
      }
      return entries;
    };
  }

  private static (int ExitCode, string Error) Run(string exe, string input, string output) {
    var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var a in new[] { "-f", input, "-o", output }) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var err = p.StandardError.ReadToEndAsync();
    var outText = p.StandardOutput.ReadToEndAsync();
    if (!p.WaitForExit(120_000)) {
      p.Kill(true);
      return (-1, "mtftar timed out");
    }
    return (p.ExitCode, err.Result + outText.Result);
  }

  private static string? FindOnPath(string name) {
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
      foreach (var candidate in new[] { name, name + ".exe" }) {
        try {
          var full = Path.Combine(dir, candidate);
          if (File.Exists(full)) return full;
        } catch (ArgumentException) {
          // a malformed PATH entry
        }
      }
    }
    return null;
  }
}
