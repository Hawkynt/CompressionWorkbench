using System.Diagnostics;
using Compression.Registry;
using Compression.Tests.CodecProof;
using FileFormat.Szdd;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Szdd;

/// <summary>
/// Third-party proof for the old Microsoft <c>"SZ "</c> COMPRESS format (<c>SzCompress</c>)
/// against libmspack (Stuart Caie, LGPL-2.1, revision
/// <c>55d501976171397ccd5d5a7a1ca7da065b1d9a06</c>), whose SZDD decompressor reads this
/// variant as <c>MSSZDD_FMT_QBASIC</c>. Its <c>msexpand</c> example restores every file we
/// write, with either parser. No real <c>"SZ "</c> file was found to prove the reading direction
/// beyond that: the LZSS body is the one KWAJ method 2 carries, which
/// <c>KwajLibmspackInteropTests</c> checks the same way.
/// </summary>
[TestFixture]
public sealed class SzCompressLibmspackOracleTests {

  // Our "SZ " file for "ABCABCABC"; msexpand restores it.
  private const string AbcAbcAbc = "535A2088F02733D10900000007414243EEF3";

  [Test, Category("HappyPath")]
  public void GivenARepeat_WhenCompressed_ThenItIsTheFileMsexpandRestores()
    => Assert.That(SzddStream.CompressQBasic("ABCABCABC"u8), Is.EqualTo(Convert.FromHexString(AbcAbcAbc)));

  [Test, Category("HappyPath")]
  public void GivenThatFile_WhenDecoded_ThenItIsTheRepeat()
    => Assert.That(SzddStream.Decompress(Convert.FromHexString(AbcAbcAbc)), Is.EqualTo("ABCABCABC"u8.ToArray()));

  [TestCase("Greedy")]
  [TestCase("Optimal")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void MsexpandRestoresOurFiles(string parser) {
    var descriptor = new SzCompressFormatDescriptor();
    foreach (var sample in new[] { CodecProofData.Text(24_000), CodecProofData.Mixed(6_000), new byte[70_000], "abracadabra abracadabra"u8.ToArray(), new byte[] { 0x42 }, Array.Empty<byte>() }) {
      using var input = new MemoryStream(sample);
      using var output = new MemoryStream();
      descriptor.Compress(input, output, new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Parser"] = parser } });
      MsexpandRestores(output.ToArray(), sample);
    }
  }

  private static void MsexpandRestores(byte[] file, byte[] expected) {
    var tool = Environment.GetEnvironmentVariable("CWB_TOOL_MSEXPAND");
    if (string.IsNullOrEmpty(tool))
      tool = "msexpand";
    var dir = Path.Combine(Path.GetTempPath(), "cwb-msexpand-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var packedPath = Path.Combine(dir, "packed.sz_");
      var unpackedPath = Path.Combine(dir, "unpacked.bin");
      File.WriteAllBytes(packedPath, file);
      var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
      start.ArgumentList.Add(packedPath);
      start.ArgumentList.Add(unpackedPath);
      Process process;
      try {
        process = Process.Start(start)!;
      } catch (Win32Exception) {
        Assert.Ignore("libmspack's msexpand is not on the PATH (or set CWB_TOOL_MSEXPAND).");
        return;
      }

      using (process) {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0), $"msexpand refused our file:\n{stdout.Result}{stderr}");
      }

      Assert.That(File.ReadAllBytes(unpackedPath), Is.EqualTo(expected));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
