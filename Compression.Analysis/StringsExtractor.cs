#pragma warning disable CS1591
using System.Text;

namespace Compression.Analysis;

/// <summary>
/// Extracts runs of printable characters from arbitrary binary data. Mirrors the
/// Unix <c>strings</c> utility: finds every maximal sequence of printable bytes
/// of at least <see cref="StringsExtractorOptions.MinLength"/> and returns them
/// along with their byte offsets. Used for triaging executables, firmware dumps,
/// memory images, and other opaque payloads.
/// <para>
/// Both ASCII and UTF-16LE scans are run by default so Windows PE string tables
/// and binary blobs that embed wide strings surface too.
/// </para>
/// </summary>
public static class StringsExtractor {

  public enum Encoding { Ascii, Utf16Le, Utf16Be }

  public sealed record FoundString(long Offset, Encoding Encoding, string Value);

  public sealed record StringsExtractorOptions(
    int MinLength = 4,
    bool ScanAscii = true,
    bool ScanUtf16Le = true,
    bool ScanUtf16Be = false,
    int MaxResults = 10_000);

  public static IReadOnlyList<FoundString> Extract(ReadOnlySpan<byte> data, StringsExtractorOptions? options = null) {
    options ??= new StringsExtractorOptions();
    var results = new List<FoundString>();

    if (options.ScanAscii)
      ScanAscii(data, options.MinLength, results, options.MaxResults);
    if (options.ScanUtf16Le && results.Count < options.MaxResults)
      ScanUtf16(data, options.MinLength, littleEndian: true, results, options.MaxResults);
    if (options.ScanUtf16Be && results.Count < options.MaxResults)
      ScanUtf16(data, options.MinLength, littleEndian: false, results, options.MaxResults);

    return results;
  }

  /// <summary>
  /// Extracts strings from <paramref name="data"/> and writes them to
  /// <paramref name="outputPath"/> in <c>strings</c>-compatible form:
  /// <c>offset : encoding : value</c> per line.
  /// </summary>
  public static int WriteToFile(byte[] data, string outputPath, StringsExtractorOptions? options = null) {
    var found = Extract(data, options);
    using var writer = new StreamWriter(outputPath);
    foreach (var s in found)
      writer.WriteLine($"0x{s.Offset:X8} {s.Encoding,-8} {s.Value}");
    return found.Count;
  }

  private static void ScanAscii(ReadOnlySpan<byte> data, int minLen, List<FoundString> results, int max) {
    var runStart = -1;
    var sb = new StringBuilder();
    for (var i = 0; i < data.Length; ++i) {
      var b = data[i];
      if (b >= 0x20 && b < 0x7F) {
        if (runStart < 0) { runStart = i; sb.Clear(); }
        sb.Append((char)b);
      } else {
        if (runStart >= 0 && sb.Length >= minLen) {
          results.Add(new FoundString(runStart, Encoding.Ascii, sb.ToString()));
          if (results.Count >= max) return;
        }
        runStart = -1;
      }
    }
    if (runStart >= 0 && sb.Length >= minLen)
      results.Add(new FoundString(runStart, Encoding.Ascii, sb.ToString()));
  }

  private static void ScanUtf16(ReadOnlySpan<byte> data, int minLen, bool littleEndian,
                                  List<FoundString> results, int max) {
    var runStart = -1;
    var sb = new StringBuilder();
    for (var i = 0; i + 1 < data.Length; i += 2) {
      var c = littleEndian
        ? (char)(data[i] | (data[i + 1] << 8))
        : (char)((data[i] << 8) | data[i + 1]);
      if (c >= 0x20 && c < 0x7F) {
        if (runStart < 0) { runStart = i; sb.Clear(); }
        sb.Append(c);
      } else {
        if (runStart >= 0 && sb.Length >= minLen) {
          results.Add(new FoundString(runStart, littleEndian ? Encoding.Utf16Le : Encoding.Utf16Be, sb.ToString()));
          if (results.Count >= max) return;
        }
        runStart = -1;
      }
    }
    if (runStart >= 0 && sb.Length >= minLen)
      results.Add(new FoundString(runStart, littleEndian ? Encoding.Utf16Le : Encoding.Utf16Be, sb.ToString()));
  }
}
