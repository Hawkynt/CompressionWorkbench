using System.Diagnostics;
using System.Text;
using Compression.Tests.Csc;
using FileFormat.Zpaq;

namespace Compression.Tests.Zpaq;

/// <summary>
/// Live checks against the reference archiver zpaq 7.15, both directions: zpaq must list and
/// extract byte for byte what <see cref="ZpaqWriter"/> writes, we must read what zpaq writes (every
/// method, several versions, deduplication, streaming), and with the same input our compressed data
/// blocks must equal zpaq's.
/// </summary>
/// <remarks>
/// The tool is <c>$ZPAQ</c> (a path), else <c>zpaq</c> on the host PATH, else <c>zpaq</c> inside WSL.
/// Debian and Ubuntu package zpaq 7.15 (<c>apt-get install zpaq</c>); the Windows build is
/// <c>zpaq64.exe</c> from <see href="http://mattmahoney.net/dc/zpaq715.zip"/>. Without the tool every
/// test is ignored, unless <c>CWB_REQUIRE_ZPAQ=1</c> (set by the CI oracle step) turns that into a failure.
/// </remarks>
[TestFixture]
[Category("ExternalInterop")]
public sealed class ZpaqExternalOracleTests {
  private static readonly Lazy<Func<string, string[], (int ExitCode, string Output)>?> _tool = new(LocateTool);
  private static readonly DateTime When = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
  private string _tmp = null!;

  [SetUp]
  public void Setup() {
    this._tmp = Path.Combine(Path.GetTempPath(), $"cwb_zpaq_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static (string Name, byte[] Data)[] Sample() {
    var text = CscSamples.Text(40_000, 41);
    return [
      ("set/text.txt", text),
      ("set/dup.txt", text),
      ("set/x86.bin", CscSamples.X86(30_000, 42)),
      ("set/random.bin", CscSamples.RandomBytes(12_000, 43)),
      ("set/table.bin", CscSamples.Table(20_000)),
      ("set/empty.dat", []),
      ("set/sub/mixed.bin", CscSamples.Mixed()),
      ("set/sub/big.bin", [.. CscSamples.RandomBytes(700_000, 44), .. CscSamples.Text(300_000, 45)]),
    ];
  }

  private void Materialise(string root, (string Name, byte[] Data)[] files) {
    foreach (var (name, data) in files) {
      var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, data);
    }
  }

  private static byte[] WriteOurs(string method, (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var w = new ZpaqWriter(ms, leaveOpen: true, method) { TransactionDate = When }) {
      w.AddDirectory("set/");
      w.AddDirectory("set/sub/");
      foreach (var (name, data) in files)
        w.AddFile(name, data);
    }
    return ms.ToArray();
  }

  public static IEnumerable<string> Methods() => ["0", "1", "2", "3", "4", "5", "14", "36", "x4.0ci1.1.1.1.2aw2mm16tst", "x4.3ci1"];

  // ── zpaq reads us ────────────────────────────────────────────────────────

  [Test, TestCaseSource(nameof(Methods))]
  public void OurArchive_IsListedAndExtractedByZpaq(string method) {
    var tool = RequireTool();
    var files = Sample();
    File.WriteAllBytes(Path.Combine(this._tmp, "ours.zpaq"), WriteOurs(method, files));

    var list = tool(this._tmp, ["l", "ours.zpaq"]);
    var extract = tool(this._tmp, ["x", "ours.zpaq", "-to", "out"]);

    Assert.Multiple(() => {
      Assert.That(list.ExitCode, Is.Zero, list.Output);
      Assert.That(list.Output, Does.Contain("1 versions, 10 files"), list.Output);
      Assert.That(extract.ExitCode, Is.Zero, extract.Output);
      foreach (var (name, data) in files)
        Assert.That(File.ReadAllBytes(Path.Combine(this._tmp, "out", name.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(data), name);
      Assert.That(Directory.Exists(Path.Combine(this._tmp, "out", "set", "sub")), "directory entry restored");
    });
  }

  // ── we read zpaq ─────────────────────────────────────────────────────────

  [Test, TestCaseSource(nameof(Methods))]
  public void ZpaqArchive_IsReadByUs(string method) {
    var tool = RequireTool();
    var files = Sample();
    this.Materialise(this._tmp, files);
    var add = tool(this._tmp, ["a", "theirs.zpaq", "set", "-method", method]);
    Assert.That(add.ExitCode, Is.Zero, add.Output);

    using var r = new ZpaqReader(File.OpenRead(Path.Combine(this._tmp, "theirs.zpaq")));
    var entries = r.Entries.ToDictionary(e => e.FileName);
    Assert.Multiple(() => {
      Assert.That(r.Errors, Is.Zero);
      Assert.That(entries.Keys, Is.SupersetOf(new[] { "set/", "set/sub/" }));
      foreach (var (name, data) in files)
        Assert.That(r.ExtractBytes(entries[name]), Is.EqualTo(data), name);
    });
  }

  [Test]
  public void ZpaqVersions_UpdatesAndDeletions_AreReadByUs() {
    var tool = RequireTool();
    var root = this._tmp;
    this.Materialise(root, [("v/a.txt", CscSamples.Text(9000, 46)), ("v/b.bin", CscSamples.RandomBytes(5000, 47))]);
    Assert.That(tool(root, ["a", "v.zpaq", "v"]).ExitCode, Is.Zero);
    Thread.Sleep(1100);  // zpaq versions need distinct second-resolution dates
    this.Materialise(root, [("v/b.bin", CscSamples.RandomBytes(5000, 48)), ("v/c.txt", CscSamples.Text(4000, 49))]);
    Assert.That(tool(root, ["a", "v.zpaq", "v", "-method", "3"]).ExitCode, Is.Zero);
    Thread.Sleep(1100);
    File.Delete(Path.Combine(root, "v", "a.txt"));
    Assert.That(tool(root, ["a", "v.zpaq", "v", "-method", "5"]).ExitCode, Is.Zero);

    using var r = new ZpaqReader(File.OpenRead(Path.Combine(root, "v.zpaq")));
    var v1 = r.GetEntries(1).ToDictionary(e => e.FileName);
    var v3 = r.Entries.ToDictionary(e => e.FileName);
    Assert.Multiple(() => {
      Assert.That(r.VersionCount, Is.EqualTo(3));
      Assert.That(r.ExtractBytes(v1["v/b.bin"]), Is.EqualTo(CscSamples.RandomBytes(5000, 47)));
      Assert.That(r.ExtractBytes(v1["v/a.txt"]), Is.EqualTo(CscSamples.Text(9000, 46)));
      Assert.That(r.ExtractBytes(v3["v/b.bin"]), Is.EqualTo(CscSamples.RandomBytes(5000, 48)));
      Assert.That(r.ExtractBytes(v3["v/c.txt"]), Is.EqualTo(CscSamples.Text(4000, 49)));
      Assert.That(v3.ContainsKey("v/a.txt"), Is.False, "deleted in version 3");
    });
  }

  [Test]
  public void ZpaqStreamingArchive_IsReadByUs([Values("s4.3ci1", "s1", "s0.0c0.0.255.255i2")] string method) {
    var tool = RequireTool();
    var files = Sample().Where(f => f.Data.Length > 0).ToArray();
    this.Materialise(this._tmp, files);
    Assert.That(tool(this._tmp, ["a", "s.zpaq", "set", "-method", method]).ExitCode, Is.Zero);

    using var r = new ZpaqReader(File.OpenRead(Path.Combine(this._tmp, "s.zpaq")));
    var entries = r.Entries.ToDictionary(e => e.FileName);
    Assert.Multiple(() => {
      foreach (var (name, data) in files)
        Assert.That(r.ExtractBytes(entries[name]), Is.EqualTo(data), name);
    });
  }

  [Test]
  public void OurBuildingBlockStream_IsExtractedByZpaq() {
    var tool = RequireTool();
    var data = CscSamples.Mixed();
    File.WriteAllBytes(Path.Combine(this._tmp, "bb.zpaq"), new ZpaqBuildingBlock().Compress(data));
    var extract = tool(this._tmp, ["x", "bb.zpaq", "-to", "out"]);
    Assert.That(extract.ExitCode, Is.Zero, extract.Output);
    var produced = Directory.GetFiles(Path.Combine(this._tmp, "out"), "*", SearchOption.AllDirectories);
    Assert.That(produced.Select(File.ReadAllBytes), Has.Some.EqualTo(data));
  }

  // ── byte equality ────────────────────────────────────────────────────────

  [Test]
  public void OurDataBlocks_EqualZpaqs([Values("0", "1", "2", "3", "4", "5")] string method) {
    var tool = RequireTool();
    var banner = tool(this._tmp, []).Output;
    if (!banner.Contains("v7.15"))
      Assert.Ignore("Data-block equality is pinned against zpaq v7.15; found: " + banner.Split('\n')[0]);
    var files = Sample();
    this.Materialise(this._tmp, files);
    Assert.That(tool(this._tmp, ["a", "theirs.zpaq", "set", "-method", method]).ExitCode, Is.Zero);
    var theirs = File.ReadAllBytes(Path.Combine(this._tmp, "theirs.zpaq"));
    var ours = WriteOurs(method, files);

    Assert.That(DataRegion(ours), Is.EqualTo(DataRegion(theirs)), "d blocks with the transaction date masked");
  }

  /// <summary>The bytes from the first d block to the first h block, with every journaling date zeroed.</summary>
  private static byte[] DataRegion(byte[] archive) {
    var s = Encoding.Latin1.GetString(archive);
    var d = JournalName(s, 'd');
    var h = JournalName(s, 'h');
    if (d < 0)
      return [];
    var tag = Encoding.Latin1.GetString(ZpaqCompressorTag);
    var start = s.LastIndexOf(tag, d, StringComparison.Ordinal);
    var end = s.LastIndexOf(tag, h, StringComparison.Ordinal);
    var region = archive[start..end];
    var text = Encoding.Latin1.GetString(region);
    for (var i = text.IndexOf("jDC", StringComparison.Ordinal); i >= 0; i = text.IndexOf("jDC", i + 3, StringComparison.Ordinal))
      if (i + 17 <= region.Length && text.AsSpan(i + 3, 14).IndexOfAnyExceptInRange('0', '9') < 0)
        region.AsSpan(i + 3, 14).Fill((byte)'0');
    return region;
  }

  private static int JournalName(string s, char type) {
    for (var i = s.IndexOf("jDC", StringComparison.Ordinal); i >= 0; i = s.IndexOf("jDC", i + 3, StringComparison.Ordinal))
      if (i + 18 <= s.Length && s[i + 17] == type && s.AsSpan(i + 3, 14).IndexOfAnyExceptInRange('0', '9') < 0)
        return i;
    return -1;
  }

  private static readonly byte[] ZpaqCompressorTag = [0x37, 0x6b, 0x53, 0x74, 0xa0, 0x31, 0x83, 0xd3, 0x8c, 0xb2, 0x28, 0xb0, 0xd3];

  // ── tool location ────────────────────────────────────────────────────────

  private static Func<string, string[], (int ExitCode, string Output)> RequireTool() {
    var tool = _tool.Value;
    if (tool is null) {
      if (Environment.GetEnvironmentVariable("CWB_REQUIRE_ZPAQ") == "1")
        Assert.Fail("CWB_REQUIRE_ZPAQ=1 but zpaq was not found ($ZPAQ, PATH, WSL).");
      Assert.Ignore("zpaq not found: set ZPAQ or install zpaq 7.15 (see the class remarks).");
    }
    return tool!;
  }

  private static Func<string, string[], (int ExitCode, string Output)>? LocateTool() {
    var configured = Environment.GetEnvironmentVariable("ZPAQ");
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
      return (dir, args) => Run(configured, dir, args);
    if (FsInteropToolbox.HostPathHasTool("zpaq"))
      return (dir, args) => Run("zpaq", dir, args);
    if (OperatingSystem.IsWindows() && FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("zpaq"))
      return (dir, args) => {
        var line = string.Join(' ', args.Select(a => "'" + a.Replace("'", "'\\''") + "'"));
        var (stdOut, stdErr, exitCode) = FsInteropToolbox.RunWsl($"cd '{FsInteropToolbox.WinToWsl(dir)}' && zpaq {line}");
        return (exitCode, stdOut + stdErr);
      };
    return null;
  }

  private static (int ExitCode, string Output) Run(string executable, string workingDirectory, string[] arguments) {
    var start = new ProcessStartInfo(executable) {
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdOut = process.StandardOutput.ReadToEndAsync();
    var stdErr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, stdOut.Result + stdErr.Result);
  }
}
