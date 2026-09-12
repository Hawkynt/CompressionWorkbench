#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Mbox;

/// <summary>
/// Reader for Unix mbox mailboxes.  Splits a byte stream into individual RFC 822
/// messages, each beginning with a "From " separator line at column 0.  The
/// separator belongs to the message that follows it and is included in the
/// extracted <see cref="MboxMessage.RawBytes"/>.
/// </summary>
public sealed class MboxReader {

  /// <summary>
  /// Parse all messages from a byte buffer.
  /// </summary>
  public static IReadOnlyList<MboxMessage> ReadAll(ReadOnlySpan<byte> data) {
    var starts = FindFromLineOffsets(data);
    var result = new List<MboxMessage>(starts.Count);
    for (var i = 0; i < starts.Count; i++) {
      var start = starts[i];
      var end = i + 1 < starts.Count ? starts[i + 1] : data.Length;
      var slice = data[start..end].ToArray();
      result.Add(MboxMessage.Parse(slice));
    }
    return result;
  }

  /// <summary>Find byte offsets of every "From " line starting at column 0.</summary>
  private static List<int> FindFromLineOffsets(ReadOnlySpan<byte> data) {
    var offsets = new List<int>();
    // First check: does the file itself start with "From "?
    if (StartsWithFromSpace(data, 0))
      offsets.Add(0);

    for (var i = 0; i < data.Length - 1; i++) {
      if (data[i] != (byte)'\n') continue;
      var lineStart = i + 1;
      if (StartsWithFromSpace(data, lineStart))
        offsets.Add(lineStart);
    }
    return offsets;
  }

  private static bool StartsWithFromSpace(ReadOnlySpan<byte> data, int offset) {
    if (offset + 5 > data.Length) return false;
    return data[offset] == 'F' && data[offset + 1] == 'r' && data[offset + 2] == 'o'
        && data[offset + 3] == 'm' && data[offset + 4] == ' ';
  }
}

/// <summary>One RFC 822 message pulled from an mbox stream.</summary>
public sealed class MboxMessage {

  /// <summary>The full bytes of the record including the leading "From " separator line.</summary>
  public required byte[] RawBytes { get; init; }

  /// <summary>Bytes of the message without the leading "From " separator (suitable as a standalone .eml).</summary>
  public required byte[] EmlBytes { get; init; }

  /// <summary>Subject header value, or null if absent.</summary>
  public string? Subject { get; init; }

  /// <summary>From header value, or null if absent.</summary>
  public string? From { get; init; }

  /// <summary>Date header value, or null if absent.</summary>
  public string? Date { get; init; }

  /// <summary>Split one mbox record into its separator line, headers, and body.</summary>
  public static MboxMessage Parse(byte[] raw) {
    // Strip the leading "From " separator line.
    var firstNewline = Array.IndexOf(raw, (byte)'\n');
    var eml = firstNewline >= 0 && raw.Length >= 5 &&
              raw[0] == 'F' && raw[1] == 'r' && raw[2] == 'o' && raw[3] == 'm' && raw[4] == ' '
      ? raw.AsSpan(firstNewline + 1).ToArray()
      : raw;

    // Parse minimal headers: first blank line ends the header block.
    string? subject = null, from = null, date = null;
    var headerText = SliceHeaders(eml);
    foreach (var line in UnfoldHeaders(headerText)) {
      var colon = line.IndexOf(':');
      if (colon < 0) continue;
      var name = line[..colon].Trim();
      var value = line[(colon + 1)..].Trim();
      if (name.Equals("Subject", StringComparison.OrdinalIgnoreCase)) subject = value;
      else if (name.Equals("From", StringComparison.OrdinalIgnoreCase)) from = value;
      else if (name.Equals("Date", StringComparison.OrdinalIgnoreCase)) date = value;
    }

    return new MboxMessage {
      RawBytes = raw,
      EmlBytes = eml,
      Subject = subject,
      From = from,
      Date = date,
    };
  }

  private static string SliceHeaders(byte[] eml) {
    // Find the first occurrence of "\r\n\r\n" or "\n\n".
    var end = eml.Length;
    for (var i = 0; i + 1 < eml.Length; i++) {
      if (eml[i] == '\n' && eml[i + 1] == '\n') { end = i; break; }
      if (i + 3 < eml.Length && eml[i] == '\r' && eml[i + 1] == '\n' &&
          eml[i + 2] == '\r' && eml[i + 3] == '\n') { end = i; break; }
    }
    return Encoding.Latin1.GetString(eml, 0, end);
  }

  private static IEnumerable<string> UnfoldHeaders(string text) {
    var current = new StringBuilder();
    foreach (var line in text.Split('\n')) {
      var clean = line.TrimEnd('\r');
      if (clean.Length > 0 && (clean[0] == ' ' || clean[0] == '\t')) {
        current.Append(' ').Append(clean.Trim());
        continue;
      }
      if (current.Length > 0) yield return current.ToString();
      current.Clear();
      current.Append(clean);
    }
    if (current.Length > 0) yield return current.ToString();
  }
}
