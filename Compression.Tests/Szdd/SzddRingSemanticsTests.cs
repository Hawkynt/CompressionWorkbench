using System.Diagnostics;
using FileFormat.Szdd;

namespace Compression.Tests.Szdd;

/// <summary>
/// The SZDD encoder against the ring a decoder actually keeps. A decoder copies a match byte by
/// byte through its 4096-byte ring, so a match that runs into slots it has just written reads
/// the new bytes there. The encoder used to compare against the ring as it stood before the
/// match, so it chose matches that decode to other bytes. On 60 KB of text, <c>expand.exe</c>
/// got 853 bytes wrong, and so did our own decoder. All encoders also avoid the 16 ring slots
/// whose initial content <c>expand.exe</c> (zeros) and libmspack (spaces) disagree about.
/// </summary>
[TestFixture]
public sealed class SzddRingSemanticsTests {

  private static byte[] Text(int length) {
    var words = "the quick brown fox jumps over the lazy dog 40303\njumps mode lazy "u8.ToArray();
    var random = new Random(length);
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = random.Next(7) == 0 ? (byte)random.Next(32, 127) : words[(i * 5 + i / 7) % words.Length];
    return data;
  }

  private static IEnumerable<TestCaseData> Inputs() {
    yield return new TestCaseData("text-60k", Text(60_000));
    yield return new TestCaseData("text-200k", Text(200_000));
    yield return new TestCaseData("runs", Enumerable.Range(0, 40_000).Select(i => (byte)(i / 997 % 3 + 'a')).ToArray());
    yield return new TestCaseData("spaces", Enumerable.Repeat((byte)' ', 9_000).ToArray());
  }

  [TestCaseSource(nameof(Inputs)), Category("Regression"), Category("RoundTrip")]
  public void GivenInput_WhenCompressedAndDecoded_ThenItIsTheInput(string name, byte[] data) {
    Assert.Multiple(() => {
      Assert.That(SzddStream.Decompress(SzddStream.Compress(data)), Is.EqualTo(data), name + " (greedy)");
      Assert.That(SzddStream.Decompress(SzddOptimizer.Compress(data)), Is.EqualTo(data), name + " (optimal)");
    });
  }

  [TestCaseSource(nameof(Inputs)), Category("ArchiveExternal"), Category("EndToEnd")]
  public void GivenOurSzdd_WhenExpandedByWindows_ThenItIsTheInput(string name, byte[] data) {
    var expand = Path.Combine(Environment.SystemDirectory, "expand.exe");
    if (!OperatingSystem.IsWindows() || !File.Exists(expand))
      Assert.Ignore("expand.exe is the oracle here.");

    var work = Path.Combine(Path.GetTempPath(), $"cwb_szdd_{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try {
      foreach (var (parser, stream) in new[] { ("greedy", SzddStream.Compress(data)), ("optimal", SzddOptimizer.Compress(data)) })
        AssertExpandRestores(expand, work, parser, stream, data, name);
    } finally {
      Directory.Delete(work, true);
    }
  }

  private static void AssertExpandRestores(string expand, string work, string parser, byte[] stream, byte[] data, string name) {
    var packed = Path.Combine(work, parser + ".tx_");
    var unpacked = Path.Combine(work, parser + ".txt");
    File.WriteAllBytes(packed, stream);
    var start = new ProcessStartInfo(expand) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(packed);
    start.ArgumentList.Add(unpacked);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, stdout);
    Assert.That(File.ReadAllBytes(unpacked), Is.EqualTo(data), $"{name} ({parser})");
  }
}
