using System.Text;
using Compression.Analysis.Statistics;

namespace Compression.Tests.Analysis;

[TestFixture]
public class StringSearchTests {

  [Test, Category("HappyPath")]
  public void ExtractStrings_FindsAsciiRuns() {
    var data = new byte[100];
    var hello = Encoding.ASCII.GetBytes("Hello World");
    Array.Copy(hello, 0, data, 10, hello.Length);
    var results = StringExtractor.ExtractAsciiStrings(data, 4);
    Assert.That(results.Count, Is.GreaterThanOrEqualTo(1));
    Assert.That(results[0].Text, Does.Contain("Hello World"));
    Assert.That(results[0].Offset, Is.EqualTo(10));
  }

  [Test, Category("HappyPath")]
  public void ExtractStrings_RespectsMinLength() {
    // Build data with explicit null separators
    var data = new byte[] {
      (byte)'A', (byte)'B', 0,
      (byte)'C', (byte)'D', (byte)'E', (byte)'F', 0,
      (byte)'G', (byte)'H'
    };
    // "AB" = 2, "CDEF" = 4, "GH" = 2
    var results4 = StringExtractor.ExtractAsciiStrings(data, 4);
    Assert.That(results4.Count, Is.EqualTo(1)); // Only "CDEF"
    Assert.That(results4[0].Text, Is.EqualTo("CDEF"));

    var results2 = StringExtractor.ExtractAsciiStrings(data, 2);
    Assert.That(results2.Count, Is.EqualTo(3)); // "AB", "CDEF", "GH"
  }

  [Test, Category("HappyPath")]
  public void SearchStrings_FindsUtf8() {
    var text = "The quick brown fox jumps over the lazy dog";
    var data = Encoding.UTF8.GetBytes(text);
    var results = StringExtractor.Search(data, "fox", Encoding.UTF8);
    Assert.That(results.Count, Is.EqualTo(1));
    Assert.That(results[0].Text, Is.EqualTo("fox"));
  }

  [Test, Category("HappyPath")]
  public void ExtractUtf16LE_FindsStrings() {
    // Build UTF-16 LE data: NUL bytes then "Hello" in UTF-16 LE then NUL bytes
    var prefix = new byte[8];
    var hello = Encoding.Unicode.GetBytes("Hello World");
    var suffix = new byte[8];
    var data = new byte[prefix.Length + hello.Length + suffix.Length];
    Array.Copy(prefix, 0, data, 0, prefix.Length);
    Array.Copy(hello, 0, data, prefix.Length, hello.Length);
    Array.Copy(suffix, 0, data, prefix.Length + hello.Length, suffix.Length);

    var results = StringExtractor.ExtractUtf16Strings(data, 4, littleEndian: true);
    Assert.That(results.Count, Is.GreaterThanOrEqualTo(1));
    Assert.That(results[0].Text, Does.Contain("Hello World"));
  }

  [Test, Category("HappyPath")]
  public void ExtractUtf16BE_FindsStrings() {
    var hello = Encoding.BigEndianUnicode.GetBytes("TestString");
    var data = new byte[4 + hello.Length + 4];
    Array.Copy(hello, 0, data, 4, hello.Length);

    var results = StringExtractor.ExtractUtf16Strings(data, 4, littleEndian: false);
    Assert.That(results.Count, Is.GreaterThanOrEqualTo(1));
    Assert.That(results[0].Text, Does.Contain("TestString"));
  }

  [Test, Category("HappyPath")]
  public void ExtractUtf8_FindsMultiByteChars() {
    // UTF-8 string with multi-byte characters
    var text = "cafe\u0301 na\u00EFve";  // café naïve with combining accent
    var data = Encoding.UTF8.GetBytes(text);
    var results = StringExtractor.ExtractUtf8Strings(data, 4);
    Assert.That(results.Count, Is.GreaterThanOrEqualTo(1));
    Assert.That(results[0].Text, Does.Contain("caf"));
  }
}
