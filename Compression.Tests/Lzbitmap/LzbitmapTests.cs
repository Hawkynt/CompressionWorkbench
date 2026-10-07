using System.Diagnostics;
using Compression.Core.Dictionary.Lzbitmap;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Lzbitmap;

using Lzb = Compression.Core.Dictionary.Lzbitmap.Lzbitmap;

/// <summary>
/// Apple's LZBITMAP, judged against the pairs of libzbitmap's test corpus (Lzbitmap/ReferenceVectors,
/// provenance in its README) and — where Python with dissect.util is installed — against an
/// independent decoder reading what our compressor writes.
/// </summary>
[TestFixture]
public sealed class LzbitmapTests {

  private const int Chunk = 0x8000;

  private static byte[] Vector(string name) {
    using var stream = typeof(LzbitmapTests).Assembly.GetManifestResourceStream("LzbitmapVectors." + name)
                       ?? throw new InvalidOperationException($"{name} is not embedded.");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static readonly int[] Corpus = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 19];

  private static IEnumerable<TestCaseData> CorpusCases()
    => Corpus.Select(i => new TestCaseData(i).SetName($"GivenCorpusPair{i:D3}_WhenDecompressed_ThenItIsTheOriginal"));

  [TestCaseSource(nameof(CorpusCases)), Category("HappyPath")]
  public void CorpusDecodes(int i)
    => Assert.That(Lzb.Decompress(Vector($"{i:D3}-compressed")), Is.EqualTo(Vector($"{i:D3}-original")));

  private static IEnumerable<TestCaseData> CorpusRoundTrips()
    => Corpus.Select(i => new TestCaseData(i).SetName($"GivenCorpusOriginal{i:D3}_WhenRoundTripped_ThenItSurvives"));

  [TestCaseSource(nameof(CorpusRoundTrips)), Category("RoundTrip")]
  public void CorpusRoundTrips_(int i) {
    var original = Vector($"{i:D3}-original");
    Assert.That(Lzb.Decompress(Lzb.Compress(original)), Is.EqualTo(original));
  }

  private static byte[] Random(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static byte[] Text(int length) {
    var words = "lzbitmap groups eight bytes and copies the rest from one period back "u8.ToArray();
    var data = new byte[length];
    var random = new Random(length);
    for (var i = 0; i < length; ++i) data[i] = random.Next(12) == 0 ? (byte)random.Next(256) : words[(i * 5 + i / 17) % words.Length];
    return data;
  }

  [TestCase(0, TestName = "GivenNoBytes_WhenRoundTripped_ThenItIsTheMagicAndTheEndChunk")]
  [TestCase(1, TestName = "GivenOneByte_WhenRoundTripped_ThenItIsStoredVerbatim")]
  [TestCase(14, TestName = "GivenFourteenBytes_WhenRoundTripped_ThenTheyAreTooFewToCompress")]
  [TestCase(15, TestName = "GivenFifteenBytes_WhenRoundTripped_ThenTheyFitACompressedHeader")]
  [TestCase(Chunk - 1, TestName = "GivenOneByteLessThanAChunk_WhenRoundTripped_ThenItSurvives")]
  [TestCase(Chunk, TestName = "GivenExactlyOneChunk_WhenRoundTripped_ThenItIsOneChunk")]
  [TestCase(Chunk + 1, TestName = "GivenOneByteIntoTheSecondChunk_WhenRoundTripped_ThenItSurvives")]
  [TestCase(5 * Chunk + 123, TestName = "GivenSixChunks_WhenRoundTripped_ThenPeriodsReachAcrossChunks")]
  [Category("BoundaryCase")]
  public void TextRoundTrip(int length) {
    var data = Text(length);
    var compressed = Lzb.Compress(data);
    Assert.Multiple(() => {
      Assert.That(compressed.AsSpan(0, 4).ToArray(), Is.EqualTo("ZBM\x09"u8.ToArray()));
      Assert.That(compressed.AsSpan(compressed.Length - 6).ToArray(), Is.EqualTo(new byte[] { 6, 0, 0, 0, 0, 0 }), "the empty end chunk");
      Assert.That(Lzb.Decompress(compressed), Is.EqualTo(data));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenIncompressibleData_WhenCompressed_ThenEveryChunkIsStoredAndNothingGrowsBeyondItsHeader() {
    var data = Random(3 * Chunk + 10, 1);
    var compressed = Lzb.Compress(data);
    Assert.Multiple(() => {
      Assert.That(compressed, Has.Length.EqualTo(4 + 4 * 6 + data.Length + 6));
      Assert.That(Lzb.Decompress(compressed), Is.EqualTo(data));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenAllZeros_WhenCompressed_ThenRepetitionCountsCarryItAndItSurvives() {
    var data = new byte[2 * Chunk];
    var compressed = Lzb.Compress(data);
    Assert.Multiple(() => {
      Assert.That(compressed.Length, Is.LessThan(400), "two 32 KiB chunks of 4096 groups each, carried by repetition counts");
      Assert.That(Lzb.Decompress(compressed), Is.EqualTo(data));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenARepeatFartherThanAOneBytePeriod_WhenRoundTripped_ThenTheTwoBytePeriodSurvives() {
    var block = Random(1000, 2);
    var data = block.Concat(block).Concat(block).ToArray();
    var compressed = Lzb.Compress(data);
    Assert.Multiple(() => {
      Assert.That(compressed.Length, Is.LessThan(1200));
      Assert.That(Lzb.Decompress(compressed), Is.EqualTo(data));
    });
  }

  [TestCase("", TestName = "GivenNothing_WhenDecompressed_ThenTheMagicIsMissing")]
  [TestCase("5a424d08", TestName = "GivenTheWrongMagic_WhenDecompressed_ThenItIsRejected")]
  [TestCase("5a424d09", TestName = "GivenOnlyTheMagic_WhenDecompressed_ThenTheChunkHeaderIsMissing")]
  [TestCase("5a424d09" + "100000050000" + "41", TestName = "GivenAChunkLongerThanTheStream_WhenDecompressed_ThenItIsRejected")]
  [TestCase("5a424d09" + "060080010000", TestName = "GivenAChunkOfMoreThan32KiB_WhenDecompressed_ThenItIsRejected")]
  [TestCase("5a424d09" + "000000000000", TestName = "GivenAChunkClaimingNoLength_WhenDecompressed_ThenItIsRejected")]
  [Category("ExceptionalCase")]
  public void Malformed(string hex) => Assert.Throws<InvalidDataException>(() => Lzb.Decompress(Convert.FromHexString(hex)));

  [Test, Category("ExceptionalCase")]
  public void GivenAPeriodReachingBeforeTheOutput_WhenDecompressed_ThenItIsRejected() {
    var stream = Lzb.Compress(Text(400));
    // Corrupt the first chunk's first period byte (metadata area 1) to point far back.
    var meta1 = stream[4 + 6] | (stream[4 + 7] << 8) | (stream[4 + 8] << 16);
    var meta2 = stream[4 + 9] | (stream[4 + 10] << 8) | (stream[4 + 11] << 16);
    Assume.That(meta2, Is.GreaterThan(meta1), "the chunk carries at least one period byte");
    stream[4 + meta1] = 0xFF;
    Assert.Throws<InvalidDataException>(() => Lzb.Decompress(stream));
  }

  [Test, Category("HappyPath")]
  public void GivenTheBuildingBlock_WhenUsed_ThenItIsLzbitmap() {
    var block = new LzbitmapBuildingBlock();
    var data = Text(5000);
    Assert.Multiple(() => {
      Assert.That(block.Id, Is.EqualTo("BB_Lzbitmap"));
      Assert.That(block.Decompress(block.Compress(data)), Is.EqualTo(data));
    });
  }

  // ── Independent decoder: dissect.util (Apache-2.0) ─────────────────

  private static readonly Lazy<string?> Python = new(() => {
    foreach (var candidate in new[] { "python3", "python" }) {
      try {
        var start = new ProcessStartInfo(candidate) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import dissect.util.compression.lzbitmap");
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode == 0) return candidate;
      } catch (Win32Exception) {
        // not on PATH
      }
    }
    return null;
  });

  /// <summary>
  /// dissect.util 3.24 reads the nibble after a chunk's last group even when fewer than nine
  /// bytes remain, where libzbitmap (and the corpus) read none, and fails when the stream holds
  /// no such nibble. A zero byte appended to each compressed chunk's nibble stream (the chunk
  /// length grown by one) gives it that nibble; a conforming reader never looks at it, and our
  /// own decoder still reads the padded stream identically.
  /// </summary>
  internal static byte[] PadForDissect(byte[] stream) {
    using var output = new MemoryStream();
    output.Write(stream, 0, 4);
    for (var pos = 4; ;) {
      var length = stream[pos] | (stream[pos + 1] << 8) | (stream[pos + 2] << 16);
      var decompressed = stream[pos + 3] | (stream[pos + 4] << 8) | (stream[pos + 5] << 16);
      var chunk = stream.AsSpan(pos, length).ToArray();
      pos += length;
      if (decompressed == 0 || length == decompressed + 6) {
        output.Write(chunk);
        if (decompressed == 0) return output.ToArray();
        continue;
      }
      byte[] padded = [.. chunk[..^17], 0, .. chunk[^17..]];
      padded[0] = (byte)(length + 1);
      padded[1] = (byte)((length + 1) >> 8);
      padded[2] = (byte)((length + 1) >> 16);
      output.Write(padded);
    }
  }

  internal static byte[] DissectDecompress(byte[] stream) {
    var padded = PadForDissect(stream);
    Assert.That(Lzb.Decompress(padded), Is.EqualTo(Lzb.Decompress(stream)), "padding is invisible to a conforming reader");
    stream = padded;
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with dissect.util on the PATH (`pip install dissect.util`).");
    var input = Path.GetTempFileName();
    try {
      File.WriteAllBytes(input, stream);
      var start = new ProcessStartInfo(python!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
      start.ArgumentList.Add("-c");
      start.ArgumentList.Add("import sys; from dissect.util.compression import lzbitmap; sys.stdout.write(lzbitmap.decompress(open(sys.argv[1], 'rb').read()).hex())");
      start.ArgumentList.Add(input);
      using var process = Process.Start(start)!;
      var stdout = process.StandardOutput.ReadToEndAsync();
      var stderr = process.StandardError.ReadToEnd();
      process.WaitForExit();
      Assert.That(process.ExitCode, Is.EqualTo(0), $"dissect.util refused our stream:\n{stderr}");
      return Convert.FromHexString(stdout.Result);
    } finally {
      File.Delete(input);
    }
  }

  [TestCase(1, TestName = "GivenOneByte_WhenOurLzbitmapIsDecodedByDissect_ThenItIsTheInput")]
  [TestCase(400, TestName = "GivenShortText_WhenOurLzbitmapIsDecodedByDissect_ThenItIsTheInput")]
  [TestCase(Chunk + 1, TestName = "GivenTwoChunks_WhenOurLzbitmapIsDecodedByDissect_ThenItIsTheInput")]
  [TestCase(200_000, TestName = "GivenLargeText_WhenOurLzbitmapIsDecodedByDissect_ThenItIsTheInput")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void DissectReadsOurStream(int length) {
    var data = Text(length);
    Assert.That(DissectDecompress(Lzb.Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("ArchiveExternal"), Category("HappyPath")]
  public void GivenIncompressibleAndZeroRuns_WhenOurLzbitmapIsDecodedByDissect_ThenItIsTheInput() {
    var data = Random(Chunk + 77, 3).Concat(new byte[3 * Chunk]).ToArray();
    Assert.That(DissectDecompress(Lzb.Compress(data)), Is.EqualTo(data));
  }
}
