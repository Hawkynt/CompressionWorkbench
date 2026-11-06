using System.Text;
using Compression.Registry;
using FileFormat.StuffIt;

namespace Compression.Tests.StuffIt;

/// <summary>
/// StuffIt method 15 (Arsenic) writing. Our decoder is checked on archives StuffIt itself wrote
/// (StuffItCorpusTests); The Unarchiver's unar is the oracle for what we write here.
/// </summary>
[TestFixture]
public class StuffItArsenicEncoderTests {

  private static byte[] Text(int length, int seed = 1) {
    var words = "it was the best of times it was the worst of times the age of wisdom the age of foolishness".Split(' ');
    var r = new Random(seed);
    var sb = new StringBuilder();
    while (sb.Length < length) sb.Append(words[r.Next(words.Length)]).Append(r.Next(11) == 0 ? ",\n" : " ");
    return Encoding.ASCII.GetBytes(sb.ToString(0, length));
  }

  private static byte[] Noise(int length, int seed = 2) {
    var b = new byte[length];
    new Random(seed).NextBytes(b);
    return b;
  }

  private static byte[] Runs(int length) {
    var b = new byte[length];
    var r = new Random(3);
    for (var i = 0; i < length;) {
      var run = r.Next(1, 700);
      var v = (byte)r.Next(4);
      for (var k = 0; k < run && i < length; ++k) b[i++] = v;
    }
    return b;
  }

  /// <summary>Equivalence classes across the stages: one byte, periodic, long runs (past the 4+255 cap), text, noise, all bytes.</summary>
  public static IEnumerable<TestCaseData> Inputs() {
    yield return new TestCaseData(new byte[] { 65 }, 9).SetName("OneByte_Block9");
    yield return new TestCaseData("abababababababab"u8.ToArray(), 9).SetName("Periodic_Block9");
    yield return new TestCaseData(Enumerable.Repeat((byte)0, 300).ToArray(), 9).SetName("ZeroRunPastCap_Block9");
    yield return new TestCaseData(Enumerable.Repeat((byte)'z', 5_000).ToArray(), 9).SetName("LongRunAcrossBlocks_Block9");
    yield return new TestCaseData(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), 9).SetName("EveryByte_Block9");
    yield return new TestCaseData(Text(3_000), 9).SetName("Text_ManyBlocks9");
    yield return new TestCaseData(Text(30_000), 12).SetName("Text_Block12");
    yield return new TestCaseData(Noise(20_000), 16).SetName("Noise_Block16");
    yield return new TestCaseData(Runs(100_000), 14).SetName("Runs_Block14");
    yield return new TestCaseData(Text(300_000, 7), 17).SetName("Text_ThreeBlocks17");
  }

  [TestCaseSource(nameof(Inputs)), Category("RoundTrip")]
  public void GivenInput_WhenEncodedAndDecoded_ThenBytesAreIdentical(byte[] data, int blockBits) {
    var packed = StuffItArsenicEncoder.Encode(data, blockBits);
    Assert.That(StuffItArsenicDecoder.Decode(packed, data.Length), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void GivenBanana_WhenTransformed_ThenLastColumnAndIndexAreTheTextbookOnes() {
    Span<byte> last = stackalloc byte[6];
    var index = StuffItArsenicEncoder.Bwt("banana"u8, last);
    // sorted rotations: abanan, anaban, ananab, banana, nabana, nanaba
    Assert.That(Encoding.ASCII.GetString(last), Is.EqualTo("nnbaaa"));
    Assert.That(index, Is.EqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void GivenText_WhenEncoded_ThenArsenicBeatsMethod13() {
    var data = Text(60_000);
    Assert.That(StuffItArsenicEncoder.Encode(data).Length, Is.LessThan(StuffIt13Encoder.Encode(data).Length));
  }

  [Test, Category("Exception")]
  public void GivenEmptyInputOrBadBlockSize_WhenEncoded_ThenRefused() {
    Assert.That(() => StuffItArsenicEncoder.Encode([]), Throws.ArgumentException);
    Assert.That(() => StuffItArsenicEncoder.Encode([1], 8), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => StuffItArsenicEncoder.Encode([1], 25), Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  [Test, Category("Boundary")]
  public void GivenLengths_WhenBlockBitsAreChosen_ThenTheyStayBetween9AndTheDefault() {
    Assert.That(StuffItArsenicEncoder.BlockBitsFor(1), Is.EqualTo(9));
    Assert.That(StuffItArsenicEncoder.BlockBitsFor(3_000), Is.EqualTo(12));
    Assert.That(StuffItArsenicEncoder.BlockBitsFor(10_000_000), Is.EqualTo(StuffItArsenicEncoder.DefaultBlockBits));
  }

  [TestCase(false), TestCase(true), Category("RoundTrip")]
  public void GivenSmallestCompression_WhenTextIsWritten_ThenArsenicIsChosenAndReadBack(bool stuffIt5) {
    var data = Text(40_000);
    var archive = Write(stuffIt5, StuffItCompression.Smallest, ("a.txt", data));
    using var r = new StuffItReader(new MemoryStream(archive));
    var e = r.Entries.Single();
    Assert.That(e.DataMethod, Is.EqualTo(15));
    Assert.That(r.Extract(e), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void GivenDescriptorCompressionOption_WhenCreating_ThenTheChosenMethodIsUsed() {
    var options = new FormatCreateOptions { FormatSpecific = new() { ["Compression"] = "LzHuffman" } };
    using var ms = new MemoryStream();
    new StuffItFormatDescriptor().Create(ms, [new ArchiveInputInfo("t.txt", "t.txt", false, Text(9_000))], options);
    ms.Position = 0;
    Assert.That(new StuffItFormatDescriptor().List(ms, null).Single().Method, Is.EqualTo("LZ+Huffman"));
  }

  private static byte[] Write(bool stuffIt5, StuffItCompression compression, params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    if (stuffIt5) {
      using var w = new StuffIt5Writer(ms, compression, leaveOpen: true);
      foreach (var f in files) w.AddFile(f.Name, f.Data);
    } else {
      using var w = new StuffItWriter(ms, compression, leaveOpen: true);
      foreach (var f in files) w.AddFile(f.Name, f.Data);
    }
    return ms.ToArray();
  }

  // ── The Unarchiver ───────────────────────────────────────────────────────

  [TestCase(false), TestCase(true), Category("ExternalInterop")]
  public void GivenOurArsenicArchive_WhenUnarExtractsIt_ThenEveryFileIsByteIdentical(bool stuffIt5) {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    // Lengths pick block sizes from 2^9 to 2^17, and the largest spans several blocks.
    var files = new (string Name, byte[] Data)[] {
      ("tiny.txt", Text(300, 11)),
      ("periodic.bin", Enumerable.Range(0, 4_000).Select(i => (byte)(i % 7)).ToArray()),
      ("runs.bin", Runs(60_000)),
      ("text.txt", Text(40_000, 12)),
      ("noisy.bin", [.. Text(5_000, 13), .. Noise(2_000), .. Text(5_000, 14)]),
      ("big.txt", Text(400_000, 15)),
    };
    var archive = Write(stuffIt5, StuffItCompression.Arsenic, files);
    using (var r = new StuffItReader(new MemoryStream(archive)))
      Assert.That(r.Entries.All(e => e.DataMethod == 15), Is.True, "every file must be Arsenic-coded");

    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sit15_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tmp);
      var path = Path.Combine(tmp, stuffIt5 ? "ars5.sit" : "ars.sit");
      File.WriteAllBytes(path, archive);
      var outDir = Path.Combine(tmp, "out");
      var run = FsInteropToolbox.RunWsl($"unar -q -f -k visible -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(run.ExitCode, Is.EqualTo(0), run.StdErr);
      foreach (var f in files) Assert.That(File.ReadAllBytes(Path.Combine(outDir, f.Name)), Is.EqualTo(f.Data), f.Name);
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }
}
