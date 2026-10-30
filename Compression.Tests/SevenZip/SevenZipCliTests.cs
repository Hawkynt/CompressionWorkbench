using System.Diagnostics;
using Compression.Lib;

namespace Compression.Tests.SevenZip;

/// <summary>
/// The cwb command line's 7z options: volumes, header compression and encryption on create, and
/// reading a split archive from any volume. Skipped when the CLI host was not built.
/// </summary>
[TestFixture]
public sealed class SevenZipCliTests {
  private string _root = null!;

  [SetUp]
  public void Setup() {
    this._root = Path.Combine(Path.GetTempPath(), $"cwb_7zcli_{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(this._root, "src"));
    File.WriteAllBytes(Path.Combine(this._root, "src", "noise.bin"), SevenZipOracle.Noise(120_000, 3));
    File.WriteAllBytes(Path.Combine(this._root, "src", "text.txt"), SevenZipOracle.Text(40_000, 4));
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
  }

  internal static string LocateCli() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("The CLI project builds the win-x64 host executable.");
#if DEBUG
    const string configuration = "Debug";
#else
    const string configuration = "Release";
#endif
    var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    var path = Path.Combine(root, "Compression.CLI", "bin", configuration, "net10.0", "win-x64", "cwb.exe");
    if (!File.Exists(path))
      Assert.Ignore($"CLI executable was not built at {path}.");
    return path;
  }

  internal static (int ExitCode, string StdOut, string StdErr) RunCli(string cli, string? stdin, params string[] args) {
    var psi = new ProcessStartInfo(cli) {
      RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (var arg in args)
      psi.ArgumentList.Add(arg);
    using var process = Process.Start(psi)!;
    if (stdin != null)
      process.StandardInput.Write(stdin);
    process.StandardInput.Close();
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(120_000)) {
      process.Kill();
      Assert.Fail("cwb timed out");
    }
    return (process.ExitCode, stdout.Result, stderr.Result);
  }

  [Test]
  [Category("HappyPath")]
  public void Given_CreateWithVolumeSize_When_ExtractedFromASecondVolume_Then_TheFilesComeBack() {
    var cli = LocateCli();
    var archive = Path.Combine(this._root, "cli.7z");
    var create = RunCli(cli, null, "create", archive, Path.Combine(this._root, "src", "noise.bin"), Path.Combine(this._root, "src", "text.txt"), "-v", "50k", "-m", "copy", "-hc");
    Assert.That(create.ExitCode, Is.Zero, create.StdErr + create.StdOut);
    Assert.That(create.StdOut, Does.Contain("volume(s)"));
    Assert.That(File.Exists(ArchiveVolumes.VolumePath(archive, 3)), Is.True);

    var output = Path.Combine(this._root, "out");
    var extract = RunCli(cli, null, "extract", ArchiveVolumes.VolumePath(archive, 2), "-o", output);
    Assert.That(extract.ExitCode, Is.Zero, extract.StdErr + extract.StdOut);
    Assert.That(File.ReadAllBytes(Path.Combine(output, "noise.bin")), Is.EqualTo(File.ReadAllBytes(Path.Combine(this._root, "src", "noise.bin"))));
    Assert.That(File.ReadAllBytes(Path.Combine(output, "text.txt")), Is.EqualTo(File.ReadAllBytes(Path.Combine(this._root, "src", "text.txt"))));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AVolumeSizeThatIsNotASize_When_Creating_Then_ItIsRefused() {
    var cli = LocateCli();
    var result = RunCli(cli, null, "create", Path.Combine(this._root, "bad.7z"), Path.Combine(this._root, "src", "text.txt"), "-v", "lots");
    Assert.That(result.ExitCode, Is.Not.Zero);
    Assert.That(result.StdErr, Does.Contain("--volume-size"));
  }
}
