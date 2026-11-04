using System.Diagnostics;
using Compression.Tests.CodecProof;
using FileFormat.Mcm;

namespace Compression.Tests.Mcm;

/// <summary>
/// mcm 0.84 itself as the oracle, both ways: we extract what it writes and write what it writes,
/// byte for byte, at every level it can write; and it extracts what we write. The binary comes
/// from <c>CWB_MCM</c> (a Windows or Linux mcm 0.84 executable); skipped when unset unless
/// <c>CWB_REQUIRE_MCM=1</c>. mcm extracts into its working directory, so each run gets its own.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
[Category("ThirdPartyProof")]
public class McmOracleTests {
  private static string RequireMcm() {
    var tool = Environment.GetEnvironmentVariable("CWB_MCM");
    if (!string.IsNullOrEmpty(tool) && File.Exists(tool))
      return tool;
    const string hint = "mcm 0.84 not configured: set CWB_MCM to an mcm executable built from github.com/mathieuchartier/mcm.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_MCM") == "1")
      Assert.Fail(hint);
    Assert.Ignore(hint);
    return "";
  }

  private static void Run(string tool, string workingDirectory, params string[] arguments) {
    var start = new ProcessStartInfo(tool) {
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(120_000)) {
      process.Kill(true);
      Assert.Fail($"mcm {string.Join(' ', arguments)} timed out");
    }
    Assert.That(process.ExitCode, Is.Zero, $"mcm {string.Join(' ', arguments)}\n{stdout.Result}\n{stderr.Result}");
  }

  private static string NewDirectory() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-mcm-oracle-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static (string Name, byte[] Data) Input(string kind) => kind switch {
    "text" => ("text.txt", CodecProofData.Text(60_000)),
    "mixed" => ("mixed.bin", CodecProofData.Mixed(24_000)),
    "random" => ("random.bin", CodecProofData.Random(40_000)),
    _ => ("tone.wav", McmVectors.Wave(20_000)),
  };

  [TestCase("text", "-t0", (int)McmCompressor.Turbo, 0)]
  [TestCase("text", "-f3", (int)McmCompressor.Fast, 3)]
  [TestCase("text", "-m1", (int)McmCompressor.Mid, 1)]
  [TestCase("text", "-h2", (int)McmCompressor.High, 2)]
  [TestCase("text", "-s1", (int)McmCompressor.Simple, 1)]
  [TestCase("text", "-store", (int)McmCompressor.Store, 6)]
  [TestCase("mixed", "-t1", (int)McmCompressor.Turbo, 1)]
  [TestCase("mixed", "-f0", (int)McmCompressor.Fast, 0)]
  [TestCase("mixed", "-m2", (int)McmCompressor.Mid, 2)]
  [TestCase("mixed", "-h0", (int)McmCompressor.High, 0)]
  [TestCase("mixed", "-s0", (int)McmCompressor.Simple, 0)]
  public void Given_McmsArchive_When_WeExtractAndRewriteIt_Then_BothMatch(string kind, string option, int compressor, int memory) {
    var mcm = RequireMcm();
    var dir = NewDirectory();
    try {
      var (name, data) = Input(kind);
      File.WriteAllBytes(Path.Combine(dir, name), data);
      Run(mcm, dir, option, name, "theirs.mcm");
      var theirs = File.ReadAllBytes(Path.Combine(dir, "theirs.mcm"));
      var index = McmArchive.ReadIndex(theirs);
      Assert.That(McmArchive.Extract(theirs, index)[0], Is.EqualTo(data));
      Assert.That(McmArchiveWriter.Write([new(name, data)], (McmCompressor)compressor, memory), Is.EqualTo(theirs));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [TestCase("text", (int)McmCompressor.High, 1)]
  [TestCase("mixed", (int)McmCompressor.Fast, 0)]
  [TestCase("random", (int)McmCompressor.Turbo, 0)]
  [TestCase("wave", (int)McmCompressor.Fast, 0)]
  [TestCase("wave", (int)McmCompressor.Store, 0)]
  public void Given_OurArchive_When_McmExtractsIt_Then_TheBytesMatch(string kind, int compressor, int memory) {
    var mcm = RequireMcm();
    var dir = NewDirectory();
    try {
      var (name, data) = Input(kind);
      File.WriteAllBytes(Path.Combine(dir, "ours.mcm"), McmArchiveWriter.Write([new(name, data)], (McmCompressor)compressor, memory));
      Run(mcm, dir, "d", "ours.mcm");
      Assert.That(File.ReadAllBytes(Path.Combine(dir, name)), Is.EqualTo(data));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_ADirectory_When_BothWriteIt_Then_TheArchivesMatchAndMcmExtractsOurs() {
    var mcm = RequireMcm();
    var dir = NewDirectory();
    try {
      var tree = McmVectors.Tree();
      foreach (var entry in tree)
        if (entry.IsDirectory)
          Directory.CreateDirectory(Path.Combine(dir, entry.Name));
        else
          File.WriteAllBytes(Path.Combine(dir, entry.Name), entry.Data);
      Run(mcm, dir, "-m1", "tree", "theirs.mcm");
      var ours = McmArchiveWriter.Write(tree, McmCompressor.Mid, 1);
      Assert.That(ours, Is.EqualTo(File.ReadAllBytes(Path.Combine(dir, "theirs.mcm"))));
      var extract = Path.Combine(dir, "x");
      Directory.CreateDirectory(extract);
      File.WriteAllBytes(Path.Combine(extract, "ours.mcm"), ours);
      Run(mcm, extract, "d", "ours.mcm");
      foreach (var entry in tree.Where(e => !e.IsDirectory))
        Assert.That(File.ReadAllBytes(Path.Combine(extract, entry.Name)), Is.EqualTo(entry.Data), entry.Name);
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
