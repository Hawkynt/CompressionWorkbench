using System.Diagnostics;
using Compression.Registry;
using Compression.Tests.CodecProof;
using FileFormat.BinaryII;
using FileFormat.Squeeze;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Squeeze;

/// <summary>
/// Third-party proof for Squeeze (SQ, <c>0xFF76</c>) against CiderPress II 1.2.0 (fadden,
/// Apache-2.0), which squeezes the files it adds to Binary II archives.
/// <list type="bullet">
/// <item>A Binary II archive <c>cp2 add</c> wrote, both members squeezed, extracts with ours and its
/// first member's SQ stream decodes with <see cref="SqueezeStream"/>.</item>
/// <item>Where <c>cp2</c> is on the PATH or named by <c>CWB_TOOL_CP2</c>, it tests (checksums
/// included) and extracts a Binary II archive whose members we squeezed.</item>
/// </list>
/// lbrate 1.1 (Russell Marks, GPL-2.0, run as a black box) also passed <c>-t</c> on our standalone
/// streams; it would not extract them only because our stream format writes an empty original
/// name.
/// </summary>
[TestFixture]
public sealed class SqueezeCiderPressOracleTests {

  private const string Cp2BinaryII =
    "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" +
    "AAAAAAAAAAAAAAAAAAAAAAAAAA";

  [TestCase("TEXT", TestName = "GivenCiderPressSqueezedText_WhenExtracted_ThenItIsTheSample")]
  [TestCase("ZEROS", TestName = "GivenCiderPressSqueezedZeros_WhenExtracted_ThenTheyAreZeros")]
  [Category("HappyPath")]
  public void CiderPressSqueezedMemberExtracts(string name) {
    var expected = name == "TEXT" ? CodecProofData.Text(4096) : new byte[2000];
    var data = new BinaryIIFormatDescriptor().ExtractEntryToMemory(new MemoryStream(Convert.FromBase64String(Cp2BinaryII)), name, null);
    Assert.That(data, Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void GivenCiderPressSqueezeStream_WhenDecodedAlone_ThenItIsTheSample() {
    // The first member's data starts after its 128-byte Binary II header.
    var archive = Convert.FromBase64String(Cp2BinaryII);
    using var output = new MemoryStream();
    SqueezeStream.Decompress(new MemoryStream(archive, 128, archive.Length - 128), output);
    Assert.That(output.ToArray()[..4096], Is.EqualTo(CodecProofData.Text(4096)));
  }

  [Test, Category("ArchiveExternal"), Category("HappyPath")]
  public void CiderPressReadsOurSqueezedBinaryII() {
    var cp2 = FindCp2();
    if (cp2 is null) Assert.Ignore("CiderPress II's cp2 is not on the PATH (or set CWB_TOOL_CP2).");
    var files = new Dictionary<string, byte[]> {
      ["TEXT"] = CodecProofData.Text(24_000),
      ["MIXED"] = CodecProofData.Mixed(6_000),
      ["ZEROS"] = new byte[70_000],
      ["SMALL"] = "abracadabra abracadabra"u8.ToArray(),
    };
    var dir = Path.Combine(Path.GetTempPath(), "cwb-sq-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var archive = Path.Combine(dir, "ours.bny");
      using (var output = File.Create(archive))
        new BinaryIIFormatDescriptor().Create(output, [.. files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value))], new FormatCreateOptions { MethodName = "squeeze" });

      Assert.That(Run(cp2!, dir, "test", archive), Does.Contain("no failures"));
      Run(cp2!, dir, "extract", archive);
      foreach (var (name, content) in files)
        Assert.That(File.ReadAllBytes(Path.Combine(dir, name)), Is.EqualTo(content), name);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  private static string? FindCp2() {
    var configured = Environment.GetEnvironmentVariable("CWB_TOOL_CP2");
    if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
      return configured;
    try {
      Run("cp2", Path.GetTempPath(), "version");
      return "cp2";
    } catch (Win32Exception) {
      return null;
    }
  }

  private static string Run(string tool, string workingDirectory, params string[] arguments) {
    var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.EqualTo(0), $"cp2 {string.Join(' ', arguments)} failed:\n{stderr}");
    return stdout.Result;
  }
}
