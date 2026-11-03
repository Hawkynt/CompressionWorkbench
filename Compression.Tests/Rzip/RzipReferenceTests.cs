using System.Diagnostics;
using Compression.Tests.CodecProof;
using FileFormat.Rzip;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Rzip;

/// <summary>
/// Third-party proof for rzip 2.x files against Andrew Tridgell's rzip 2.1
/// (https://rzip.samba.org/, GPL-2.0-or-later, built with bzip2 1.0.8 and glibc's
/// <c>random()</c> sequence). The files below were written by <c>rzip</c> at its default level 6
/// and decode here; for input without repeats our file is byte-identical to its. Where
/// <c>rzip</c> is on the PATH or named by <c>CWB_TOOL_RZIP</c>, it also restores the files we
/// write. See <c>docs/RZIP-ON-DISK.md</c>.
/// </summary>
[TestFixture]
public sealed class RzipReferenceTests {

  // rzip CodecProofData.Text(1024): one stored control block, one bzip2 literal block.
  private const string RzipText1K =
    "UlpJUAIBAAAEAAAAAAAAAAAAAAAAAAAAAwAAAAAAAAAAGgAAAAMAAAAAAAAAADEAAAADCgAAAAoAAAAAAAAAAAAEAAAAB3cImgRlAQAAAAQAAA" +
    "AAAABCWmg5MUFZJlNZqPhC3AAA6lmAABBAAH/gP///8EABqVx0CKn7QTKIHqeUMjQAEVT/aYhUjQAAAAjaqNAAmIwAJgIkpPKYk0GQyG0mmnqB" +
    "ODmbwU4YweIdE5W726H4+eV0IG310WDRqUVH1AWKQQyGppuKMyamcZEqc2jPI1JhXVuBjESLIItm9LGGtxHC4UvWXcjMcKbQDmToiuSzDQ8CAR" +
    "o4mGavHZfhvIVYEoqbldbLed0YggK4ZhyxW3QjhWJG2LxW72knqJhbMdudsi2FWL3vfcJzE4KdVLByiewmitDhH7BxVoMIvXkdiqJVSQqpVRlT" +
    "n1V/WMIEkkZJCX2xNIWQhhBkDMHQTYy6STmgEwMVpKJRIAAYAABzJwzapIDcHUTsJcTiD4A/im4TEHODIBwE7i5CYg7C0WRm8FpKjM1chOINhL" +
    "DaB3Q9BKcIaq6g5IhtQ/lPhTep/i7kinChIVHwhbg=";

  // rzip of 2000 zero bytes.
  private const string RzipZeros2000 =
    "UlpJUAIBAAAH0AAAAAAAAAAAAAAAAAAAAwAAAAAAAAAAGgAAAAMAAAAAAAAAADEAAAADCgAAAAoAAAAAAAAAANAHAAAAAAAAAAQtAAAA0AcAAA" +
    "AAAABCWmg5MUFZJlNZIi+TQAAAA8ACwAAIAAAIIAAgqT1BmLageLuSKcKEgRF8mgA=";

  [Test, Category("HappyPath")]
  public void GivenRzipsText_WhenDecoded_ThenItIsTheSample()
    => Assert.That(Decompress(Convert.FromBase64String(RzipText1K)), Is.EqualTo(CodecProofData.Text(1024)));

  [Test, Category("HappyPath")]
  public void GivenRzipsZeros_WhenDecoded_ThenTheyAreRestored()
    => Assert.That(Decompress(Convert.FromBase64String(RzipZeros2000)), Is.EqualTo(new byte[2000]));

  [Test, Category("HappyPath")]
  public void GivenBytesWithoutRepeats_WhenCompressed_ThenTheFileIsWhatRzipWrites() {
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)i;

    var file = Compress(data);
    // Header, two empty stream headers, a stored control block (literal, end, CRC) and a stored
    // literal block: bzip2 would not make either smaller.
    Assert.Multiple(() => {
      Assert.That(file, Has.Length.EqualTo(24 + 13 + 13 + 13 + 10 + 13 + 256));
      Assert.That(file.AsSpan(0, 6).ToArray(), Is.EqualTo("RZIP\u0002\u0001"u8.ToArray()));
      Assert.That(Decompress(file), Is.EqualTo(data));
    });
  }

  [Test, Category("EdgeCase")]
  public void GivenNoBytes_WhenCompressed_ThenTheFileIsTheHeaderAlone() {
    var file = Compress([]);
    Assert.Multiple(() => {
      Assert.That(file, Has.Length.EqualTo(24));
      Assert.That(Decompress(file), Is.Empty);
    });
  }

  [Test, Category("Exception")]
  public void GivenACorruptLiteral_WhenDecoded_ThenTheChunkCrcRefusesIt() {
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)i;
    var file = Compress(data);
    file[^1] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => Decompress(file));
  }

  [Test, Category("Exception")]
  public void GivenATruncatedFile_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Decompress(Convert.FromBase64String(RzipText1K).AsSpan(0, 200).ToArray()));

  [Test, Category("Exception")]
  public void GivenAnUnknownBlockType_WhenDecoded_ThenItIsRefused() {
    var file = Convert.FromBase64String(RzipZeros2000);
    // The first stream-0 block header follows the 24-byte header and two 13-byte stream headers.
    file[24 + 26] = 7;
    Assert.Throws<InvalidDataException>(() => Decompress(file));
  }

  [Test, Category("ArchiveExternal"), Category("HappyPath")]
  public void RzipRestoresOurFiles() {
    var block = new byte[200_000];
    new Random(7).NextBytes(block);
    var repeated = new byte[block.Length * 3];
    for (var i = 0; i < 3; ++i)
      block.CopyTo(repeated, i * block.Length);
    // More than 900 KiB of literals, so each stream spans several linked blocks.
    var random = new byte[2_000_000];
    new Random(11).NextBytes(random);
    foreach (var sample in new[] { CodecProofData.Text(24_000), CodecProofData.Mixed(6_000), new byte[70_000], repeated, random, new byte[] { 0x42 } })
      RzipRestores(Compress(sample), sample);
  }

  private static byte[] Compress(byte[] data) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    RzipStream.Compress(input, output);
    return output.ToArray();
  }

  private static byte[] Decompress(byte[] data) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    RzipStream.Decompress(input, output);
    return output.ToArray();
  }

  private static void RzipRestores(byte[] file, byte[] expected) {
    var tool = Environment.GetEnvironmentVariable("CWB_TOOL_RZIP");
    if (string.IsNullOrEmpty(tool))
      tool = "rzip";
    var dir = Path.Combine(Path.GetTempPath(), "cwb-rzip-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var packedPath = Path.Combine(dir, "packed.rz");
      var unpackedPath = Path.Combine(dir, "unpacked.bin");
      File.WriteAllBytes(packedPath, file);
      var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
      foreach (var argument in new[] { "-d", "-k", "-f", "-o", unpackedPath, packedPath })
        start.ArgumentList.Add(argument);
      Process process;
      try {
        process = Process.Start(start)!;
      } catch (Win32Exception) {
        Assert.Ignore("rzip is not on the PATH (or set CWB_TOOL_RZIP).");
        return;
      }

      using (process) {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0), $"rzip refused our file:\n{stdout.Result}{stderr}");
      }

      Assert.That(File.ReadAllBytes(unpackedPath), Is.EqualTo(expected));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
