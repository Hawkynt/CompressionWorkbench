using System.Text;
using System.Text.RegularExpressions;

namespace Compression.Analysis.Statistics;

/// <summary>
/// Extracts and searches for strings in binary data.
/// </summary>
public static class StringExtractor {

  /// <summary>Result of a string extraction or search operation.</summary>
  public sealed record StringResult(long Offset, string OffsetHex, int Length, string Text);

  /// <summary>Extracts printable ASCII string runs of at least minLength bytes.</summary>
  /// <param name="data">Binary data to scan.</param>
  /// <param name="minLength">Minimum string length to include.</param>
  /// <param name="bridgeGap">Maximum non-printable gap to bridge between fragments (default 1).
  /// When > 0, single non-printable bytes between printable runs are replaced with '.' and the string continues.</param>
  public static List<StringResult> ExtractAsciiStrings(byte[] data, int minLength, int bridgeGap = 0) {
    var results = new List<StringResult>();
    var start = -1;
    var sb = new StringBuilder();
    var printableCount = 0;

    for (var i = 0; i <= data.Length; i++) {
      var isPrintable = i < data.Length && data[i] >= 0x20 && data[i] < 0x7F;

      if (isPrintable) {
        if (start < 0) start = i;
        sb.Append((char)data[i]);
        printableCount++;
      }
      else if (start >= 0) {
        // Check if we can bridge: look ahead up to bridgeGap bytes for another printable
        var canBridge = false;
        if (bridgeGap > 0 && i < data.Length) {
          var lookAhead = Math.Min(bridgeGap, data.Length - i);
          for (var j = 0; j < lookAhead; j++) {
            if (data[i + j] >= 0x20 && data[i + j] < 0x7F) {
              canBridge = true;
              // Bridge the gap with '.' placeholders
              for (var k = 0; k <= j; k++)
                sb.Append(data[i + k] >= 0x20 && data[i + k] < 0x7F ? (char)data[i + k] : '.');
              printableCount++; // count the resumed printable char
              i += j; // loop increment will add 1
              break;
            }
          }
        }

        if (!canBridge) {
          // Emit string
          if (printableCount >= minLength) {
            var len = i - start;
            var text = sb.Length > 200 ? sb.ToString(0, 200) : sb.ToString();
            results.Add(new StringResult(start, $"0x{start:X}", len, text));
          }
          start = -1;
          sb.Clear();
          printableCount = 0;
        }
      }
    }

    return results;
  }

  /// <summary>Extracts printable UTF-8 string runs of at least minLength characters.</summary>
  /// <remarks>
  /// Scans for valid UTF-8 multi-byte sequences containing printable characters.
  /// Falls back to ASCII-range detection for single-byte characters.
  /// </remarks>
  public static List<StringResult> ExtractUtf8Strings(byte[] data, int minLength, int bridgeGap = 0) {
    var results = new List<StringResult>();
    var start = -1;
    var sb = new StringBuilder();
    var charCount = 0;

    for (var i = 0; i <= data.Length; i++) {
      var isPrintable = false;
      var byteLen = 1;

      if (i < data.Length) {
        var b = data[i];
        if (b >= 0x20 && b < 0x7F) {
          // ASCII printable
          isPrintable = true;
        }
        else if (b == 0x09 || b == 0x0A || b == 0x0D) {
          // Tab, newline, carriage return — treat as printable whitespace
          isPrintable = true;
        }
        else if (b >= 0xC2 && b <= 0xF4 && i + 1 < data.Length) {
          // Multi-byte UTF-8 start
          byteLen = b < 0xE0 ? 2 : b < 0xF0 ? 3 : 4;
          if (i + byteLen <= data.Length) {
            var valid = true;
            for (var j = 1; j < byteLen; j++) {
              if ((data[i + j] & 0xC0) != 0x80) { valid = false; break; }
            }
            if (valid) {
              try {
                var ch = Encoding.UTF8.GetString(data, i, byteLen);
                if (ch.Length > 0 && !char.IsControl(ch[0]))
                  isPrintable = true;
              }
              catch { /* invalid sequence */ }
            }
          }
        }
      }

      if (isPrintable) {
        if (start < 0) start = i;
        if (i < data.Length) {
          sb.Append(Encoding.UTF8.GetString(data, i, byteLen));
          charCount++;
          i += byteLen - 1; // loop will add 1
        }
      }
      else if (start >= 0) {
        // Bridge gap logic (single-byte only)
        var canBridge = false;
        if (bridgeGap > 0 && i < data.Length) {
          var lookAhead = Math.Min(bridgeGap, data.Length - i);
          for (var j = 0; j < lookAhead; j++) {
            var lb = data[i + j];
            if ((lb >= 0x20 && lb < 0x7F) || lb >= 0xC2) {
              canBridge = true;
              for (var k = 0; k <= j; k++)
                sb.Append(data[i + k] >= 0x20 && data[i + k] < 0x7F ? (char)data[i + k] : '.');
              charCount++;
              i += j; // loop will add 1
              break;
            }
          }
        }

        if (!canBridge) {
          if (charCount >= minLength) {
            var len = i - start;
            var text = sb.Length > 200 ? sb.ToString(0, 200) : sb.ToString();
            results.Add(new StringResult(start, $"0x{start:X}", len, text));
          }
          start = -1;
          sb.Clear();
          charCount = 0;
        }
      }
    }

    return results;
  }

  /// <summary>Extracts printable UTF-16 string runs of at least minLength characters.</summary>
  /// <param name="data">Binary data to scan.</param>
  /// <param name="minLength">Minimum string length (in characters) to include.</param>
  /// <param name="littleEndian">True for UTF-16 LE, false for UTF-16 BE.</param>
  public static List<StringResult> ExtractUtf16Strings(byte[] data, int minLength, bool littleEndian = true) {
    var results = new List<StringResult>();
    var start = -1;
    var sb = new StringBuilder();

    for (var i = 0; i <= data.Length - 1; i += 2) {
      if (i + 1 >= data.Length) break;

      var ch = littleEndian
        ? (char)(data[i] | (data[i + 1] << 8))
        : (char)((data[i] << 8) | data[i + 1]);

      var isPrintable = ch >= 0x20 && ch < 0xFFFE && !char.IsControl(ch);

      if (isPrintable) {
        if (start < 0) start = i;
        sb.Append(ch);
      }
      else if (start >= 0) {
        if (sb.Length >= minLength) {
          var len = i - start;
          var text = sb.Length > 200 ? sb.ToString(0, 200) : sb.ToString();
          results.Add(new StringResult(start, $"0x{start:X}", len, text));
        }
        start = -1;
        sb.Clear();
      }
    }

    // Flush remaining
    if (start >= 0 && sb.Length >= minLength) {
      var len = data.Length - start;
      var text = sb.Length > 200 ? sb.ToString(0, 200) : sb.ToString();
      results.Add(new StringResult(start, $"0x{start:X}", len, text));
    }

    return results;
  }

  /// <summary>Searches for a query string (plain text or regex) in the data.</summary>
  public static List<StringResult> Search(byte[] data, string query, Encoding encoding) {
    var results = new List<StringResult>();
    var text = encoding.GetString(data);

    try {
      var regex = new Regex(query, RegexOptions.Compiled, TimeSpan.FromSeconds(5));
      foreach (Match m in regex.Matches(text)) {
        var byteOffset = encoding.GetByteCount(text.AsSpan(0, m.Index));
        var byteLen = encoding.GetByteCount(m.Value);
        var display = m.Value.Length > 200 ? m.Value[..200] + "..." : m.Value;
        results.Add(new StringResult(byteOffset, $"0x{byteOffset:X}", byteLen, display));
      }
    }
    catch (RegexParseException) {
      var idx = 0;
      while ((idx = text.IndexOf(query, idx, StringComparison.Ordinal)) >= 0) {
        var byteOffset = encoding.GetByteCount(text.AsSpan(0, idx));
        var byteLen = encoding.GetByteCount(query);
        results.Add(new StringResult(byteOffset, $"0x{byteOffset:X}", byteLen, query));
        idx += query.Length;
      }
    }

    return results;
  }
}
