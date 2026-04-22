#pragma warning disable CS1591
using System.Text;
using Compression.Analysis;

namespace Compression.Tests.Analysis;

[TestFixture]
public class StringsExtractorTests {

  [Test]
  public void ExtractsAsciiRunOfMinLength() {
    var data = "\x01\x02Hello, World!\x00\x03"u8.ToArray();
    var result = StringsExtractor.Extract(data);
    Assert.That(result.Any(s => s.Value == "Hello, World!"), Is.True);
  }

  [Test]
  public void SkipsShortRuns() {
    var data = "\x00abc\x00hello\x00xy"u8.ToArray();
    var result = StringsExtractor.Extract(data);
    // "abc" (3 chars) < min length 4 → skipped
    // "hello" (5 chars) → kept
    // "xy" (2 chars) → skipped
    Assert.That(result.Select(s => s.Value).ToArray(), Is.EqualTo(new[] { "hello" }));
  }

  [Test]
  public void FindsUtf16LeRun() {
    // "ABC" in UTF-16LE: 41 00 42 00 43 00 44 00
    var data = new byte[] { 0x00, 0x00, 0x41, 0x00, 0x42, 0x00, 0x43, 0x00, 0x44, 0x00, 0xFF };
    var result = StringsExtractor.Extract(data);
    Assert.That(result.Any(s => s.Encoding == StringsExtractor.Encoding.Utf16Le && s.Value == "ABCD"), Is.True);
  }

  [Test]
  public void RespectsMaxResults() {
    var sb = new StringBuilder();
    for (var i = 0; i < 100; ++i) sb.Append("abcd\0");
    var result = StringsExtractor.Extract(Encoding.ASCII.GetBytes(sb.ToString()),
      new StringsExtractor.StringsExtractorOptions(MaxResults: 5));
    Assert.That(result.Count, Is.LessThanOrEqualTo(5));
  }
}
