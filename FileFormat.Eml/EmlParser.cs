#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Eml;

/// <summary>
/// Minimal RFC 822 / MIME parser.  Splits a message into its header block + body,
/// then (for multipart payloads) into nested parts.  Decodes <c>base64</c> and
/// <c>quoted-printable</c> transfer encodings.  This is deliberately shallow —
/// sufficient for surfacing attachments as archive entries, not a full MIME stack.
/// </summary>
public static class EmlParser {

  public sealed class Part {
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required byte[] RawBody { get; init; }
    public required byte[] DecodedBody { get; init; }
    public IReadOnlyList<Part>? SubParts { get; init; }

    public string? ContentType => GetHeader("Content-Type");
    public string? ContentDisposition => GetHeader("Content-Disposition");
    public string? ContentTransferEncoding => GetHeader("Content-Transfer-Encoding");

    public string? GetHeader(string name) =>
      Headers.TryGetValue(name.ToUpperInvariant(), out var v) ? v : null;

    /// <summary>Parse the Content-Type mime type, lowercased (e.g. "text/plain").</summary>
    public string? MimeType {
      get {
        var ct = ContentType;
        if (ct == null) return null;
        var semi = ct.IndexOf(';');
        return (semi >= 0 ? ct[..semi] : ct).Trim().ToLowerInvariant();
      }
    }

    /// <summary>Parse the "name"/"filename" parameter from Content-Disposition or Content-Type.</summary>
    public string? FileName =>
      ParseParameter(ContentDisposition, "filename")
      ?? ParseParameter(ContentType, "name");

    /// <summary>True if this part has Content-Disposition: attachment.</summary>
    public bool IsAttachment =>
      ContentDisposition != null &&
      ContentDisposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>Parse an .eml file from its raw bytes.</summary>
  public static Part Parse(byte[] data) => ParsePart(data.AsSpan());

  private static Part ParsePart(ReadOnlySpan<byte> data) {
    var split = FindHeaderBodySplit(data);
    var headerText = Encoding.Latin1.GetString(data[..split.HeadersEnd]);
    var body = split.BodyStart < data.Length ? data[split.BodyStart..].ToArray() : Array.Empty<byte>();
    var headers = ParseHeaders(headerText);

    var decoded = DecodeTransferEncoding(
      body, headers.TryGetValue("CONTENT-TRANSFER-ENCODING", out var enc) ? enc : null);

    List<Part>? subParts = null;
    if (headers.TryGetValue("CONTENT-TYPE", out var ct) &&
        ct.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)) {
      var boundary = ParseParameter(ct, "boundary");
      if (!string.IsNullOrEmpty(boundary))
        subParts = SplitMultipart(body, boundary);
    }

    return new Part {
      Headers = headers,
      RawBody = body,
      DecodedBody = decoded,
      SubParts = subParts,
    };
  }

  private readonly record struct HeaderBodySplit(int HeadersEnd, int BodyStart);

  private static HeaderBodySplit FindHeaderBodySplit(ReadOnlySpan<byte> data) {
    for (var i = 0; i + 3 < data.Length; i++) {
      if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
        return new HeaderBodySplit(i, i + 4);
    }
    for (var i = 0; i + 1 < data.Length; i++) {
      if (data[i] == '\n' && data[i + 1] == '\n')
        return new HeaderBodySplit(i, i + 2);
    }
    // No blank line found: whole thing is headers, no body.
    return new HeaderBodySplit(data.Length, data.Length);
  }

  private static Dictionary<string, string> ParseHeaders(string text) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var lines = text.Split('\n');
    var current = new StringBuilder();
    string? currentName = null;

    void Flush() {
      if (currentName != null)
        result[currentName.ToUpperInvariant()] = current.ToString().Trim();
      current.Clear();
      currentName = null;
    }

    foreach (var raw in lines) {
      var line = raw.TrimEnd('\r');
      if (line.Length == 0) { Flush(); continue; }
      if (line[0] == ' ' || line[0] == '\t') {
        current.Append(' ').Append(line.Trim());
        continue;
      }
      Flush();
      var colon = line.IndexOf(':');
      if (colon < 0) continue;
      currentName = line[..colon].Trim();
      current.Append(line[(colon + 1)..].Trim());
    }
    Flush();
    return result;
  }

  internal static string? ParseParameter(string? header, string paramName) {
    if (header == null) return null;
    var parts = header.Split(';');
    foreach (var p in parts.Skip(1)) {
      var trimmed = p.Trim();
      var eq = trimmed.IndexOf('=');
      if (eq < 0) continue;
      var key = trimmed[..eq].Trim();
      var val = trimmed[(eq + 1)..].Trim();
      if (val.Length >= 2 && val[0] == '"' && val[^1] == '"') val = val[1..^1];
      if (key.Equals(paramName, StringComparison.OrdinalIgnoreCase))
        return val;
    }
    return null;
  }

  private static List<Part> SplitMultipart(byte[] body, string boundary) {
    var parts = new List<Part>();
    var delim = Encoding.ASCII.GetBytes("--" + boundary);
    var positions = new List<int>();
    for (var i = 0; i + delim.Length <= body.Length; i++) {
      if (body.AsSpan(i, delim.Length).SequenceEqual(delim)) {
        // Boundary must be at the start of a line: either start of body or preceded by LF/CRLF.
        var atLineStart = i == 0 || body[i - 1] == '\n';
        if (atLineStart) positions.Add(i);
      }
    }
    for (var i = 0; i < positions.Count - 1; i++) {
      var start = positions[i] + delim.Length;
      // After delim, skip optional "--" closing marker and CRLF.
      if (start + 2 <= body.Length && body[start] == '-' && body[start + 1] == '-') break;
      while (start < body.Length && (body[start] == '\r' || body[start] == '\n')) start++;
      var end = positions[i + 1];
      // Trim trailing CRLF before next boundary.
      while (end > start && (body[end - 1] == '\r' || body[end - 1] == '\n')) end--;
      if (end <= start) continue;
      parts.Add(ParsePart(body.AsSpan(start, end - start)));
    }
    return parts;
  }

  private static byte[] DecodeTransferEncoding(byte[] body, string? encoding) {
    if (string.IsNullOrEmpty(encoding)) return body;
    return encoding.Trim().ToLowerInvariant() switch {
      "base64" => DecodeBase64(body),
      "quoted-printable" => DecodeQuotedPrintable(body),
      _ => body,
    };
  }

  private static byte[] DecodeBase64(byte[] body) {
    try {
      // Strip whitespace and decode.
      var sb = new StringBuilder(body.Length);
      foreach (var b in body) {
        if (b > ' ') sb.Append((char)b);
      }
      return Convert.FromBase64String(sb.ToString());
    } catch {
      return body;
    }
  }

  private static byte[] DecodeQuotedPrintable(byte[] body) {
    var result = new List<byte>(body.Length);
    for (var i = 0; i < body.Length; i++) {
      var b = body[i];
      if (b != '=') { result.Add(b); continue; }
      if (i + 2 >= body.Length) { result.Add(b); continue; }
      var a = body[i + 1];
      var c = body[i + 2];
      // Soft line break: "=CRLF" or "=LF" — skip.
      if (a == '\n') { i += 1; continue; }
      if (a == '\r' && c == '\n') { i += 2; continue; }
      if (TryHex(a, out var hi) && TryHex(c, out var lo)) {
        result.Add((byte)((hi << 4) | lo));
        i += 2;
      } else {
        result.Add(b);
      }
    }
    return result.ToArray();
  }

  private static bool TryHex(byte b, out int v) {
    if (b >= '0' && b <= '9') { v = b - '0'; return true; }
    if (b >= 'A' && b <= 'F') { v = b - 'A' + 10; return true; }
    if (b >= 'a' && b <= 'f') { v = b - 'a' + 10; return true; }
    v = 0; return false;
  }
}
