using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Tests.CodecProof;
using FileFormat.Freeze;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Freeze;

/// <summary>
/// Third-party proof for Freeze against Teemu Suutari's ancient (BSD-2-Clause).
/// <list type="bullet">
/// <item>Files freeze 1.x and 2.x wrote (ancient's test corpus, see
/// <c>Freeze/ReferenceVectors/README.md</c>) decode to the Canterbury Corpus text they hold.</item>
/// <item>Where <c>ancient</c> is on the PATH or named by <c>CWB_TOOL_ANCIENT</c>, it verifies the
/// files we write.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class FreezeAncientOracleTests {

  private const string AliceSha256 = "7467306ee0feed4971260f3c87421154a05be571d944e9cb021a5713700c38f0";

  [TestCase("test_C1.F", TestName = "GivenAFreeze2File_WhenDecoded_ThenItIsTheCanterburyText")]
  [TestCase("test_C1_old.F", TestName = "GivenAFreeze1File_WhenDecoded_ThenItIsTheCanterburyText")]
  [Category("HappyPath")]
  public void RealFileDecodes(string name) {
    using var output = new MemoryStream();
    FreezeStream.Decompress(new MemoryStream(Resource(name)), output);
    var data = output.ToArray();
    Assert.Multiple(() => {
      Assert.That(data, Has.Length.EqualTo(152089));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(AliceSha256));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenATruncatedFreeze2File_WhenDecoded_ThenItIsRefused() {
    var file = Resource("test_C1.F");
    using var output = new MemoryStream();
    Assert.That(() => FreezeStream.Decompress(new MemoryStream(file[..^200]), output), Throws.InstanceOf<Exception>());
  }

  [Test, Category("ArchiveExternal"), Category("HappyPath")]
  public void AncientVerifiesOurFiles() {
    foreach (var sample in new[] { CodecProofData.Text(24_000), CodecProofData.Mixed(6_000), new byte[70_000], "abracadabra abracadabra"u8.ToArray() }) {
      using var packed = new MemoryStream();
      FreezeStream.Compress(new MemoryStream(sample), packed);
      AncientVerifies(packed.ToArray(), sample);
    }
  }

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FreezeVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource FreezeVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static void AncientVerifies(byte[] packed, byte[] expected) {
    var tool = Environment.GetEnvironmentVariable("CWB_TOOL_ANCIENT");
    if (string.IsNullOrEmpty(tool))
      tool = "ancient";
    var dir = Path.Combine(Path.GetTempPath(), "cwb-ancient-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var packedPath = Path.Combine(dir, "packed.F");
      var expectedPath = Path.Combine(dir, "expected.bin");
      File.WriteAllBytes(packedPath, packed);
      File.WriteAllBytes(expectedPath, expected);
      var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
      foreach (var argument in new[] { "verify", packedPath, expectedPath })
        start.ArgumentList.Add(argument);
      Process process;
      try {
        process = Process.Start(start)!;
      } catch (Win32Exception) {
        Assert.Ignore("ancient is not on the PATH (or set CWB_TOOL_ANCIENT).");
        return;
      }

      using (process) {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0), $"ancient refused our file:\n{stdout.Result}{stderr}");
      }
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
