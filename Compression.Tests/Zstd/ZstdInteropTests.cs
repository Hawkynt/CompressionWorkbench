#pragma warning disable CS1591
using System.Diagnostics;
using Compression.Registry;

namespace Compression.Tests.Zstd;

/// <summary>
/// Streams the reference zstd wrote have to decode here byte for byte.
/// </summary>
/// <remarks>
/// <para>Every other zstd test here compresses with our encoder and decompresses
/// with our decoder. That never asked the decoder to read anything our encoder
/// does not produce — and ours emits raw literals, while a real encoder emits
/// Huffman-coded ones for anything that is neither tiny nor incompressible. So
/// the entire Huffman literals path was exercised only by frames that avoided it.
/// </para>
///
/// <para>What that hid: the weight table decoded a symbol short, and the symbol
/// that should have carried the implicit weight was not the one that got it. On a
/// file drawn from <c>{00, 01, DF, ED, FF}</c> every byte came back correctly
/// except <c>FF</c>, which came back as <c>F0</c>. The frame checksum caught it,
/// which is the only reason it looked like an error rather than data.</para>
///
/// <para>The alphabets below are chosen to make the encoder reach for Huffman
/// literals and to vary where the top symbol sits, because that is what decides
/// how many weights the table needs.</para>
/// </remarks>
[TestFixture]
public class ZstdInteropTests {

  private static string? Zstd() {
    foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/local/bin" }) {
      var path = Path.Combine(dir, "zstd");
      if (File.Exists(path)) return path;
    }
    return null;
  }

  private static IEnumerable<TestCaseData> Payloads() {
    // A sparse alphabet whose top symbol is 0xFF: the weight table needs an entry
    // for every symbol below it, so the count is at its largest.
    yield return new TestCaseData(FromAlphabet(new byte[] { 0x00, 0x01, 0xFF, 0xED, 0xDF }, 4_000, 1))
      .SetName("sparse alphabet, top symbol FF");

    // A dense low alphabet: far fewer weights, and the count lands somewhere else
    // entirely. The two together caught a rule that suited one and not the other.
    yield return new TestCaseData(FromAlphabet("abcdefgh"u8.ToArray(), 4_000, 2))
      .SetName("dense alphabet, top symbol 68");

    yield return new TestCaseData(FromAlphabet(new byte[] { 0x00, 0x80 }, 9_000, 3))
      .SetName("two symbols, one of them high");
    yield return new TestCaseData(Ramp(50_000)).SetName("a ramp over every byte value");
    yield return new TestCaseData(Text(40_000)).SetName("English-shaped text");
    yield return new TestCaseData(new byte[30_000]).SetName("all zeros");
    // Past a single block, and large enough that the literals section needs the
    // five-byte header with its eighteen-bit sizes. Packing those four header bits
    // and two eighteen-bit fields into a thirty-two bit word dropped the top of
    // the compressed size, and the Huffman streams then did not add up.
    yield return new TestCaseData(FromAlphabet("abcdefgh"u8.ToArray(), 300_000, 4))
      .SetName("larger than one block, so the sizes need eighteen bits");
    yield return new TestCaseData(FromAlphabet(
        new byte[] { 0x00, 0x01, 0xFF, 0xED, 0xDF }, 300_000, 5))
      .SetName("larger than one block, sparse alphabet");

    yield return new TestCaseData(Array.Empty<byte>()).SetName("empty");
    yield return new TestCaseData(new byte[] { 7 }).SetName("one byte");
  }

  private static byte[] FromAlphabet(byte[] alphabet, int length, int seed) {
    var rng = new Random(seed);
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = alphabet[rng.Next(alphabet.Length)];
    return data;
  }

  private static byte[] Ramp(int n) {
    var d = new byte[n];
    for (var i = 0; i < n; ++i) d[i] = (byte)(i * 7);
    return d;
  }

  private static byte[] Text(int n) {
    const string words = "the quick brown fox jumps over the lazy dog while it rains ";
    var d = new byte[n];
    for (var i = 0; i < n; ++i) d[i] = (byte)words[i % words.Length];
    return d;
  }

  [TestCaseSource(nameof(Payloads)), Category("Interop")]
  public void WeReadWhatZstdWrites(byte[] payload) {
    var zstd = Zstd();
    if (zstd == null) Assert.Ignore("zstd is not installed; nothing to compare against.");

    var work = Path.Combine(Path.GetTempPath(), "cwb_zstd_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var raw = Path.Combine(work, "raw.bin");
      File.WriteAllBytes(raw, payload);

      // Several levels: they choose different literal encodings and different
      // numbers of literal streams for the same input.
      foreach (var level in new[] { 1, 3, 9 }) {
        var compressed = Path.Combine(work, $"theirs{level}.zst");
        var (exit, stderr) = Run(zstd!, $"-q -f -{level} \"{raw}\" -o \"{compressed}\"");
        if (exit != 0) {
          Assert.Ignore($"zstd would not compress this at -{level}: {stderr}");
          return;
        }

        using var input = File.OpenRead(compressed);
        using var output = new MemoryStream();
        StreamOps().Decompress(input, output);
        Assert.That(output.ToArray(), Is.EqualTo(payload).AsCollection,
          $"a stream zstd wrote at -{level} did not decode to what it was given");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static IStreamFormatOperations StreamOps() =>
    FormatRegistry.GetStreamOps("Zstd") ?? throw new NotSupportedException("no Zstd stream ops");

  private static (int Exit, string StdErr) Run(string tool, string arguments) {
    var start = new ProcessStartInfo(tool, arguments) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    };
    using var process = Process.Start(start)!;
    process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit(120_000);
    return (process.HasExited ? process.ExitCode : -1, stderr);
  }
}
