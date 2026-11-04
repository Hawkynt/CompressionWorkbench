using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Core.Streams;
using Compression.Tests.CodecProof;
using FileFormat.Crunch;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Crunch;

/// <summary>
/// Third-party proof for CP/M CRUNCH (see <c>docs/CRUNCH-ON-DISK.md</c>).
/// <list type="bullet">
/// <item>Real CRUNCH 1.x and 2.x files from the Walnut Creek CP/M CD decode to what lbrate 1.1
/// extracts (see <c>Crunch/ReferenceVectors/README.md</c>).</item>
/// <item>Where <c>lbrate</c> is on the PATH or named by <c>CWB_TOOL_LBRATE</c>, it extracts the
/// files we write, checksum included.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class CrunchReferenceTests {

  [TestCase("zsystem.izs", "ZSYSTEM.INS", 2304, "6961a0d3842bf1794671922e4150453934d24560fb4ad37b4beff0f20f11f641", TestName = "GivenACrunch1File_WhenDecoded_ThenItIsWhatLbrateExtracts")]
  [TestCase("mxform.bzs", "MX80FORM.BAS", 7424, "03038ef64ebc734ebcac79934349cd6d7334f3dfbc0c41b1a50c3aa3dfb7112e", TestName = "GivenACrunch2File_WhenDecoded_ThenItIsWhatLbrateExtracts")]
  [TestCase("delbr.czm", "DELBR.COM", 13184, "5c6dc209bf85de6571c3b8307f63328f63f65cfd0a401b85335cf002454cdf9c", TestName = "GivenACrunch2FileThatOutgrowsItsTable_WhenDecoded_ThenItIsWhatLbrateExtracts")]
  [Category("HappyPath")]
  public void RealFileDecodes(string file, string name, int length, string sha256) {
    using var stream = new CrunchStream(new MemoryStream(Resource(file)), CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    stream.CopyTo(output);
    var data = output.ToArray();
    Assert.Multiple(() => {
      Assert.That(stream.OriginalName, Is.EqualTo(name));
      Assert.That(data, Has.Length.EqualTo(length));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(sha256));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenARealFileWithADamagedCode_WhenDecoded_ThenItIsRefused() {
    var file = Resource("mxform.bzs");
    // One flipped bit in the code stream: the codes or the checksum reject it.
    file[40] ^= 0x10;
    Assert.Throws<InvalidDataException>(() => Decode(file));
  }

  [TestCase(0, TestName = "GivenNoBytes_WhenRoundTripped_ThenOnlyTheEndCodeIsWritten")]
  [TestCase(300_000, TestName = "GivenMoreCodesThanOneTable_WhenRoundTripped_ThenResetsKeepItDecodable")]
  [Category("BoundaryCase")]
  public void RoundTrips(int length) {
    var data = CodecProofData.Text(length);
    Assert.That(Decode(Encode(data, "TEXT.TXT")), Is.EqualTo(data));
  }

  [Test, Category("BoundaryCase")]
  public void GivenRunsAndTheEscapeByte_WhenRoundTripped_ThenTheRleLayerKeepsThem() {
    var data = new byte[] { 0x90, 0x90, 0x90, 1, 1, 1, 1, 0x90, 2, 2, 0x90, 0 }
      .Concat(Enumerable.Repeat((byte)7, 600)).ToArray();
    Assert.That(Decode(Encode(data, "RLE.DAT")), Is.EqualTo(data));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnUnknownStreamVersion_WhenDecoded_ThenItIsRefused() {
    var file = Encode("abc"u8.ToArray(), "X");
    file[5] = 0x30; // version byte after "X\0" and the revision byte
    Assert.Throws<InvalidDataException>(() => Decode(file));
  }

  [TestCase(1)]
  [TestCase(24_000)]
  [TestCase(300_000)]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void LbrateExtractsOurFile(int length) {
    var tool = Environment.GetEnvironmentVariable("CWB_TOOL_LBRATE");
    if (string.IsNullOrEmpty(tool))
      tool = "lbrate";
    var data = CodecProofData.Text(length);
    var dir = Path.Combine(Path.GetTempPath(), "cwb-crunch-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      File.WriteAllBytes(Path.Combine(dir, "test.tzt"), Encode(data, "TEST.TXT"));
      var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
      start.ArgumentList.Add("test.tzt");
      Process process;
      try {
        process = Process.Start(start)!;
      } catch (Win32Exception) {
        Assert.Ignore("lbrate is not on the PATH (or set CWB_TOOL_LBRATE).");
        return;
      }

      using (process) {
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0), output);
      }

      // lbrate lower-cases the stored name.
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "test.txt")), Is.EqualTo(data));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  private static byte[] Encode(byte[] data, string name) {
    using var output = new MemoryStream();
    using (var stream = new CrunchStream(output, CompressionStreamMode.Compress, name, leaveOpen: true))
      stream.Write(data);
    return output.ToArray();
  }

  private static byte[] Decode(byte[] file) {
    using var stream = new CrunchStream(new MemoryStream(file), CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    stream.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CrunchVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource CrunchVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }
}
