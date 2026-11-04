using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Tests.CodecProof;
using FileFormat.PowerPacker;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.PowerPacker;

/// <summary>
/// Third-party proof for PP20 against Teemu Suutari's ancient (BSD-2-Clause), whose PowerPacker
/// decoder reads the files the Amiga packer wrote.
/// <list type="bullet">
/// <item>A PP20 file from ancient's test corpus decodes to the Canterbury Corpus text it holds.</item>
/// <item>Where <c>ancient</c> is on the PATH or named by <c>CWB_TOOL_ANCIENT</c>, it verifies the
/// files we write at every efficiency.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class PowerPackerAncientOracleTests {

  private const string AliceSha256 = "7467306ee0feed4971260f3c87421154a05be571d944e9cb021a5713700c38f0";

  // Written by our encoder for "ABCABCABC" (a stream that ends with a match) and checked with
  // `ancient verify`.
  private const string AbcAbcAbc = "50503230090A0B0BC40E090B0900000000000917";

  [Test, Category("HappyPath")]
  public void GivenAncientsPp20File_WhenDecoded_ThenItIsTheCanterburyText() {
    var data = PowerPackerStream.Decompress(Resource("test_C1.pp"));
    Assert.Multiple(() => {
      Assert.That(data, Has.Length.EqualTo(152089));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(AliceSha256));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAStreamEndingWithAMatch_WhenCompressed_ThenTheFinalLiteralFlagIsWritten()
    => Assert.That(PowerPackerStream.Compress("ABCABCABC"u8), Is.EqualTo(Convert.FromHexString(AbcAbcAbc)));

  [Test, Category("HappyPath")]
  public void GivenAStreamEndingWithAMatch_WhenDecoded_ThenItIsRestored()
    => Assert.That(PowerPackerStream.Decompress(Convert.FromHexString(AbcAbcAbc)), Is.EqualTo("ABCABCABC"u8.ToArray()));

  [Test, Category("ExceptionalCase")]
  public void GivenAStreamThatOmitsTheFinalLiteralFlag_WhenDecoded_ThenItIsRefused()
    // Our encoder's output before it wrote the final flag; ancient rejects it too.
    => Assert.Throws<InvalidDataException>(() => PowerPackerStream.Decompress(Convert.FromHexString("50503230090A0B0B881C12161200000000000918")));

  [Test, Category("ExceptionalCase")]
  public void GivenNoBytes_WhenCompressed_ThenItIsRefusedBecausePp20CannotHoldThem()
    => Assert.Throws<ArgumentException>(() => PowerPackerStream.Compress(ReadOnlySpan<byte>.Empty));

  [Test, Category("ExceptionalCase")]
  public void GivenAZeroOriginalSize_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => PowerPackerStream.Decompress(Convert.FromHexString("50503230090A0B0B0000000000000000")));

  [TestCase(PowerPackerEfficiency.Fast)]
  [TestCase(PowerPackerEfficiency.Mediocre)]
  [TestCase(PowerPackerEfficiency.Good)]
  [TestCase(PowerPackerEfficiency.VeryGood)]
  [TestCase(PowerPackerEfficiency.Best)]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void AncientVerifiesOurFile(PowerPackerEfficiency efficiency) {
    foreach (var sample in new[] { CodecProofData.Text(24_000), CodecProofData.Mixed(6_000), new byte[70_000], "abracadabra abracadabra"u8.ToArray() })
      AncientVerifies(PowerPackerStream.Compress(sample, efficiency), sample, ".pp");
  }

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PowerPackerVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource PowerPackerVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static void AncientVerifies(byte[] packed, byte[] expected, string extension) {
    var tool = Environment.GetEnvironmentVariable("CWB_TOOL_ANCIENT");
    if (string.IsNullOrEmpty(tool))
      tool = "ancient";
    var dir = Path.Combine(Path.GetTempPath(), "cwb-ancient-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var packedPath = Path.Combine(dir, "packed" + extension);
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
