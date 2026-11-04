using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Tests.CodecProof;
using FileFormat.IcePacker;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.IcePacker;

/// <summary>
/// Third-party proof for Pack-Ice against Teemu Suutari's ancient (BSD-2-Clause), whose decoder our
/// reader is converted from.
/// <list type="bullet">
/// <item>Files Pack-Ice 1.13, 2.11 and 2.34 wrote (ancient's test corpus, see
/// <c>IcePacker/ReferenceVectors/README.md</c>) decode to ancient's originals, picture mode
/// included.</item>
/// <item>Where <c>ancient</c> is on the PATH or named by <c>CWB_TOOL_ANCIENT</c>, it verifies the
/// <c>ICE!</c> files we write.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class IcePackerAncientOracleTests {

  private const string AliceSha256 = "7467306ee0feed4971260f3c87421154a05be571d944e9cb021a5713700c38f0";
  private const string NeoSha256 = "f78527e1be365857d00c36d5a7a027fab6f22a94ae28210478c775d460869f15";
  private const string Neo2Sha256 = "d3d2a5150624c218ba710729d098d3a98b3d1cd64151f46582f93e639b4bc08c";

  [TestCase("test_C1.ice_113", 152089, AliceSha256, TestName = "GivenAPackIce113File_WhenDecoded_ThenItIsTheCanterburyText")]
  [TestCase("test_C1.ice_24", 152089, AliceSha256, TestName = "GivenAPackIce24File_WhenDecoded_ThenItIsTheCanterburyText")]
  [TestCase("test_neo.ice_211p", 32128, NeoSha256, TestName = "GivenAPackIce211Picture_WhenDecoded_ThenThePlanesAreRestored")]
  [TestCase("test_neo.ice_234p", 32128, NeoSha256, TestName = "GivenAPackIce234Picture_WhenDecoded_ThenThePlanesAreRestored")]
  [TestCase("test_neo2.ice_234p", 16128, Neo2Sha256, TestName = "GivenAPackIce234PictureWithAStoredSize_WhenDecoded_ThenThePlanesAreRestored")]
  [Category("HappyPath")]
  public void RealFileDecodes(string name, int length, string sha256) {
    var data = IcePackerStream.Decompress(Resource(name));
    Assert.Multiple(() => {
      Assert.That(data, Has.Length.EqualTo(length));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(sha256));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenOurFile_WhenInspected_ThenItHasThe231HeaderWithItsOwnSize() {
    var data = CodecProofData.Text(4096);
    var file = IcePackerStream.Compress(data);
    Assert.Multiple(() => {
      Assert.That(file[..4], Is.EqualTo("ICE!"u8.ToArray()));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4)), Is.EqualTo(file.Length));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(8)), Is.EqualTo(data.Length));
      Assert.That(IcePackerStream.Decompress(file), Is.EqualTo(data));
    });
  }

  [TestCase(1, TestName = "GivenOneByte_WhenRoundTripped_ThenTheAnchorHoldsOnlyTheFlags")]
  [TestCase(2, TestName = "GivenTwoBytes_WhenRoundTripped_ThenTheyAreOneLiteralRun")]
  [TestCase(40_000, TestName = "GivenRandomBytes_WhenRoundTripped_ThenShortMatchesBreakTheRuns")]
  [Category("BoundaryCase")]
  public void RoundTrips(int length) {
    var data = CodecProofData.Random(length);
    Assert.That(IcePackerStream.Decompress(IcePackerStream.Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("BoundaryCase")]
  public void GivenTheLongestLiteralRun_WhenRoundTripped_ThenItIsRestored() {
    var data = DeBruijn()[..33_037];
    Assert.That(IcePackerStream.Decompress(IcePackerStream.Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenMoreBytesThanOneRunWithoutAnyMatch_WhenCompressed_ThenItIsRefused()
    => Assert.Throws<NotSupportedException>(() => IcePackerStream.Compress(DeBruijn()[..33_038]));

  [Test, Category("BoundaryCase")]
  public void GivenALongRun_WhenRoundTripped_ThenDistanceOneMatchesCoverIt() {
    var data = new byte[5000];
    Assert.That(IcePackerStream.Decompress(IcePackerStream.Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenNoBytes_WhenCompressed_ThenItIsRefused()
    => Assert.Throws<ArgumentException>(() => IcePackerStream.Compress(ReadOnlySpan<byte>.Empty));

  [Test, Category("ExceptionalCase")]
  public void GivenAPackedSizeBeyondTheFile_WhenDecoded_ThenItIsRefused() {
    var file = Resource("test_neo.ice_234p");
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), (uint)file.Length + 1);
    Assert.Throws<InvalidDataException>(() => IcePackerStream.Decompress(file));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenATruncatedStream_WhenDecoded_ThenItIsRefused() {
    var file = Resource("test_neo.ice_234p");
    var cut = file[..^100];
    BinaryPrimitives.WriteUInt32BigEndian(cut.AsSpan(4), (uint)cut.Length);
    Assert.Throws<InvalidDataException>(() => IcePackerStream.Decompress(cut));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnUnknownHeader_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => IcePackerStream.Decompress("NOPE\0\0\0\x10\0\0\0\x01xxxx"u8));

  [Test, Category("ArchiveExternal"), Category("HappyPath")]
  public void AncientVerifiesOurFiles() {
    foreach (var sample in new[] { CodecProofData.Text(24_000), CodecProofData.Mixed(6_000), new byte[70_000], "abracadabra abracadabra"u8.ToArray(), CodecProofData.Random(3_000) })
      AncientVerifies(IcePackerStream.Compress(sample), sample);
  }

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"IcePackerVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource IcePackerVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  /// <summary>A byte sequence in which no two-byte string repeats, so no match exists.</summary>
  private static byte[] DeBruijn() {
    var sequence = new List<byte>(65_536);
    var a = new int[3];
    void Visit(int t, int p) {
      if (t > 2) {
        if (2 % p == 0)
          for (var i = 1; i <= p; ++i)
            sequence.Add((byte)a[i]);
        return;
      }

      a[t] = a[t - p];
      Visit(t + 1, p);
      for (var j = a[t - p] + 1; j < 256; ++j) {
        a[t] = j;
        Visit(t + 1, t);
      }
    }

    Visit(1, 1);
    return [.. sequence];
  }

  private static void AncientVerifies(byte[] packed, byte[] expected) {
    var tool = Environment.GetEnvironmentVariable("CWB_TOOL_ANCIENT");
    if (string.IsNullOrEmpty(tool))
      tool = "ancient";
    var dir = Path.Combine(Path.GetTempPath(), "cwb-ancient-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var packedPath = Path.Combine(dir, "packed.ice");
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
