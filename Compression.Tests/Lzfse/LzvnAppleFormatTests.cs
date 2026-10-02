using System.Diagnostics;
using FileFormat.Lzfse;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Lzfse;

/// <summary>
/// LZVN as Apple defines it. The codec used to speak an opcode table of its own: it round-tripped
/// with itself, read no LZVN Apple wrote and wrote none Apple's decoder reads. The vectors here
/// are an afsctool/libcompression stream from keramics' hfsplus.raw, instruction-level streams
/// built from Apple's opcode table, and — where Python with libyal's pyfmos is installed — our
/// encoder's output decoded by an independent decoder.
/// </summary>
[TestFixture]
public sealed class LzvnAppleFormatTests {

  private static byte[] Decode(byte[] stream, int length) {
    var output = new byte[length];
    var written = Lzvn.Decompress(stream, output);
    return output[..written];
  }

  private static byte[] Pattern(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  [Test, Category("HappyPath")]
  public void GivenTheStreamLibcompressionWroteForAfsctool_WhenDecoded_ThenItIsTheFile() {
    // testdir1/compressed3 of keramics' hfsplus.raw (decmpfs method 7): lrg_l of 19, then eos.
    var stream = Convert.FromHexString("e0034d7920636f6d707265737365642066696c650a0600000000000000");
    Assert.That(Decode(stream, 19), Is.EqualTo("My compressed file\n"u8.ToArray()));
  }

  [TestCase("e3414243" + "3003" + "0600000000000000", "ABCABCABCABC", TestName = "GivenSmlD_WhenDecoded_ThenTheMatchRepeatsAtItsDistance")]
  [TestCase("e3414243" + "070300" + "0600000000000000", "ABCABC", TestName = "GivenLrgD_WhenDecoded_ThenTheDistanceIsTheLittleEndianWord")]
  [TestCase("e3414243" + "a00c00" + "0600000000000000", "ABCABC", TestName = "GivenMedD_WhenDecoded_ThenTheDistanceIsTheWordsHighBits")]
  [TestCase("e3414243" + "000346" + "44" + "0600000000000000", "ABCABCDBCD", TestName = "GivenPreD_WhenDecoded_ThenItCarriesALiteralAndReusesTheDistance")]
  [TestCase("e3414243" + "0003" + "f2" + "0600000000000000", "ABCABCAB", TestName = "GivenSmlM_WhenDecoded_ThenItContinuesAtThePreviousDistance")]
  [TestCase("e141" + "0001" + "f001" + "0600000000000000", "AAAAAAAAAAAAAAAAAAAAA", TestName = "GivenLrgM_WhenDecoded_ThenItIsSixteenPlusItsByte")]
  [TestCase("0e" + "16" + "e141" + "0600000000000000", "A", TestName = "GivenNops_WhenDecoded_ThenTheyAreSkipped")]
  [TestCase("0005" + "0600000000000000", "", TestName = "GivenAMatchBeforeAnyOutput_WhenDecoded_ThenItIsInvalid")]
  [Category("BoundaryCase")]
  public void Opcode(string hex, string expected) {
    var stream = Convert.FromHexString(hex);
    if (expected.Length == 0) {
      Assert.Throws<InvalidDataException>(() => Decode(stream, 16));
      return;
    }
    Assert.That(System.Text.Encoding.ASCII.GetString(Decode(stream, expected.Length)), Is.EqualTo(expected));
  }

  [TestCase(0x1E, TestName = "GivenOpcode1E_WhenDecoded_ThenItIsUndefined")]
  [TestCase(0x3E, TestName = "GivenOpcode3E_WhenDecoded_ThenItIsUndefined")]
  [TestCase(0x70, TestName = "GivenOpcode70_WhenDecoded_ThenItIsUndefined")]
  [TestCase(0x7F, TestName = "GivenOpcode7F_WhenDecoded_ThenItIsUndefined")]
  [TestCase(0xD0, TestName = "GivenOpcodeD0_WhenDecoded_ThenItIsUndefined")]
  [TestCase(0xDF, TestName = "GivenOpcodeDF_WhenDecoded_ThenItIsUndefined")]
  [Category("ExceptionalCase")]
  public void Undefined(int opcode)
    => Assert.Throws<InvalidDataException>(() => Decode([0xE1, 0x41, (byte)opcode, 0, 0, 0, 0, 0, 0, 0, 0], 8));

  [TestCase("e541", TestName = "GivenALiteralRunPastTheEnd_WhenDecoded_ThenItIsTruncated")]
  [TestCase("e14107", TestName = "GivenALrgDWithoutItsDistance_WhenDecoded_ThenItIsTruncated")]
  [TestCase("e141a0", TestName = "GivenAMedDWithoutItsWord_WhenDecoded_ThenItIsTruncated")]
  [Category("ExceptionalCase")]
  public void Truncated(string hex) => Assert.Throws<InvalidDataException>(() => Decode(Convert.FromHexString(hex), 64));

  [TestCase(0, TestName = "GivenNoBytes_WhenRoundTripped_ThenItIsJustTheEightByteEos")]
  [TestCase(1, TestName = "GivenOneByte_WhenRoundTripped_ThenItIsThatByte")]
  [TestCase(15, TestName = "GivenFifteenLiterals_WhenRoundTripped_ThenTheyAreOneSmlL")]
  [TestCase(16, TestName = "GivenSixteenLiterals_WhenRoundTripped_ThenTheyAreOneLrgL")]
  [TestCase(271, TestName = "GivenTheLongestLrgL_WhenRoundTripped_ThenItIsOneRun")]
  [TestCase(272, TestName = "GivenOneLiteralMoreThanALrgL_WhenRoundTripped_ThenItSplits")]
  [TestCase(5000, TestName = "GivenIncompressibleData_WhenRoundTripped_ThenItSurvives")]
  [Category("BoundaryCase")]
  public void RandomRoundTrip(int length) {
    var data = Pattern(length, length);
    var stream = Lzvn.Compress(data);
    Assert.Multiple(() => {
      Assert.That(stream[^8..], Is.EqualTo(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0 }));
      Assert.That(Decode(stream, length), Is.EqualTo(data));
    });
  }

  [TestCase(1, 4, TestName = "GivenARunAtDistanceOne_WhenRoundTripped_ThenTheOverlapRepeats")]
  [TestCase(1535, 10, TestName = "GivenTheFarthestSmlDDistance_WhenRoundTripped_ThenItSurvives")]
  [TestCase(1536, 10, TestName = "GivenTheNearestLrgDDistance_WhenRoundTripped_ThenItSurvives")]
  [TestCase(40000, 600, TestName = "GivenAFarLongMatch_WhenRoundTripped_ThenItContinuesWithLrgM")]
  [Category("BoundaryCase")]
  public void MatchRoundTrip(int distance, int matchLength) {
    var head = Pattern(distance, 7);
    var data = head.Concat(head.Take(matchLength)).Concat(Pattern(5, 8)).ToArray();
    if (distance == 1) data = Enumerable.Repeat((byte)0x5A, 300).ToArray();
    Assert.That(Decode(Lzvn.Compress(data), data.Length), Is.EqualTo(data));
  }

  // ── Independent decoder: libyal libfmos (pyfmos) ───────────────────

  private static readonly Lazy<string?> Python = new(() => {
    foreach (var candidate in new[] { "python3", "python" }) {
      try {
        var start = new ProcessStartInfo(candidate) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import pyfmos");
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode == 0) return candidate;
      } catch (Win32Exception) {
        // not on PATH
      }
    }
    return null;
  });

  private static byte[] Pyfmos(string function, byte[] compressed, int length) {
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with pyfmos on the PATH (`pip install libfmos-python`).");
    var input = Path.GetTempFileName();
    try {
      File.WriteAllBytes(input, compressed);
      var start = new ProcessStartInfo(python!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
      start.ArgumentList.Add("-c");
      start.ArgumentList.Add($"import sys, pyfmos; sys.stdout.write(pyfmos.{function}(open(sys.argv[1], 'rb').read(), int(sys.argv[2])).hex())");
      start.ArgumentList.Add(input);
      start.ArgumentList.Add(length.ToString(System.Globalization.CultureInfo.InvariantCulture));
      using var process = Process.Start(start)!;
      var stdout = process.StandardOutput.ReadToEndAsync();
      var stderr = process.StandardError.ReadToEnd();
      process.WaitForExit();
      Assert.That(process.ExitCode, Is.EqualTo(0), $"pyfmos.{function} refused our stream:\n{stderr}");
      return Convert.FromHexString(stdout.Result);
    } finally {
      File.Delete(input);
    }
  }

  private static byte[] Text(int length) {
    var words = "the quick brown fox jumps over the lazy dog while apple compresses files "u8.ToArray();
    var random = new Random(length);
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = random.Next(10) == 0 ? (byte)random.Next(256) : words[(i * 7 + i / 13) % words.Length];
    return data;
  }

  [TestCase(1, TestName = "GivenOneByte_WhenOurLzvnIsDecodedByLibfmos_ThenItIsTheInput")]
  [TestCase(300, TestName = "GivenShortText_WhenOurLzvnIsDecodedByLibfmos_ThenItIsTheInput")]
  [TestCase(65536, TestName = "GivenAFullDecmpfsChunk_WhenOurLzvnIsDecodedByLibfmos_ThenItIsTheInput")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void LibfmosReadsOurLzvn(int length) {
    var data = Text(length);
    Assert.That(Pyfmos("lzvn_decompress", Lzvn.Compress(data), length), Is.EqualTo(data));
  }

  [TestCase(200, TestName = "GivenSmallInput_WhenOurLzfseIsDecodedByLibfmos_ThenItIsTheInput")]
  [TestCase(200_000, TestName = "GivenLargeInput_WhenOurLzfseIsDecodedByLibfmos_ThenItIsTheInput")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void LibfmosReadsOurLzfse(int length) {
    var data = Text(length);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(new MemoryStream(data), compressed);
    Assert.That(Pyfmos("lzfse_decompress", compressed.ToArray(), length), Is.EqualTo(data));
  }
}
