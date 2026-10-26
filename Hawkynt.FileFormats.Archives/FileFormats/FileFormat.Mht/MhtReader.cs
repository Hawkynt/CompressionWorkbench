using System.Text;

namespace FileFormat.Mht;

/// <summary>One resource of an MHTML document: its MIME headers, where its bytes sit, and the archive name it maps to.</summary>
public sealed class MhtPart {
  internal MhtPart(IReadOnlyDictionary<string, string> headers, int bodyStart, int bodyLength, int segmentStart, int segmentEnd, bool topLevel) {
    this.Headers = headers;
    this.BodyStart = bodyStart;
    this.BodyLength = bodyLength;
    this.SegmentStart = segmentStart;
    this.SegmentEnd = segmentEnd;
    this.IsTopLevel = topLevel;
  }

  /// <summary>Unfolded headers, keyed case-insensitively.</summary>
  public IReadOnlyDictionary<string, string> Headers { get; }

  /// <summary>Offset of the (still transfer-encoded) body in the document.</summary>
  public int BodyStart { get; }

  /// <summary>Length of the encoded body.</summary>
  public int BodyLength { get; }

  /// <summary>Start of the bytes that removing this part deletes (its delimiter's leading line break).</summary>
  internal int SegmentStart { get; }

  /// <summary>End (exclusive) of the bytes that removing this part deletes.</summary>
  internal int SegmentEnd { get; }

  /// <summary>Whether the part is a direct child of the root multipart (only those can be edited in place).</summary>
  public bool IsTopLevel { get; }

  /// <summary>The archive name assigned by <see cref="MhtReader"/>.</summary>
  public string Name { get; internal set; } = "";

  /// <summary>The Content-Location header, if any.</summary>
  public string? Location => this.Header("Content-Location");

  /// <summary>The media type, lower-cased, without parameters.</summary>
  public string MediaType {
    get {
      var ct = this.Header("Content-Type");
      if (ct == null) return "text/plain";
      var semi = ct.IndexOf(';');
      return (semi >= 0 ? ct[..semi] : ct).Trim().ToLowerInvariant();
    }
  }

  /// <summary>The Content-Transfer-Encoding, lower-cased; "7bit" when absent.</summary>
  public string TransferEncoding => (this.Header("Content-Transfer-Encoding") ?? "7bit").Trim().ToLowerInvariant();

  /// <summary>Header value by name, or null.</summary>
  public string? Header(string name) => this.Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// Reads an MHTML document (RFC 2557, "MIME Encapsulation of Aggregate Documents"): a MIME message,
/// normally <c>multipart/related</c>, whose parts are the page and its resources, each identified by a
/// <c>Content-Location</c> URL (or a <c>Content-ID</c> referenced through <c>cid:</c>).
/// </summary>
/// <remarks>
/// Every part keeps its byte offsets so a part can be decoded on demand and removed or added by
/// splicing between boundary delimiters, leaving every other byte where it was. Bodies are decoded per
/// RFC 2045 (<c>base64</c>, <c>quoted-printable</c>, and the identity encodings <c>7bit</c>, <c>8bit</c>,
/// <c>binary</c>); any other transfer encoding is refused on extraction rather than returned undecoded.
/// </remarks>
public sealed class MhtReader {

  private readonly byte[] _data;

  public MhtReader(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    this._data = data;
    var (headersEnd, bodyStart) = SplitHeaders(data, 0, data.Length);
    this.Headers = ParseHeaders(data, 0, headersEnd);
    if (!this.Headers.ContainsKey("Content-Type") && !this.Headers.ContainsKey("MIME-Version"))
      throw new InvalidDataException("Not an MHTML document: no MIME-Version or Content-Type header.");
    this.LineBreak = DetectLineBreak(data, headersEnd);

    var parts = new List<MhtPart>();
    var contentType = this.Headers.GetValueOrDefault("Content-Type") ?? "";
    if (contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)) {
      this.Boundary = Parameter(contentType, "boundary")
        ?? throw new InvalidDataException("MHTML multipart document without a boundary parameter.");
      this.CloseSegmentStart = SplitMultipart(data, bodyStart, data.Length, this.Boundary, parts, topLevel: true);
    } else {
      // A single-resource document: the message itself is the page.
      parts.Add(new MhtPart(this.Headers, bodyStart, data.Length - bodyStart, 0, data.Length, topLevel: false));
    }

    this.Parts = parts;
    AssignNames(parts);
  }

  /// <summary>Top-level message headers.</summary>
  public IReadOnlyDictionary<string, string> Headers { get; }

  /// <summary>The root multipart boundary, or null for a single-resource document.</summary>
  public string? Boundary { get; }

  /// <summary>The document's line break ("\r\n" or "\n").</summary>
  public string LineBreak { get; }

  /// <summary>Offset where the close delimiter's segment starts (the insertion point for new parts).</summary>
  internal int CloseSegmentStart { get; } = -1;

  /// <summary>Every leaf resource, in document order (nested multiparts flattened).</summary>
  public IReadOnlyList<MhtPart> Parts { get; }

  /// <summary>The raw document.</summary>
  internal byte[] Data => this._data;

  /// <summary>Decodes a part's body.</summary>
  public byte[] Extract(MhtPart part) {
    ArgumentNullException.ThrowIfNull(part);
    var body = this._data.AsSpan(part.BodyStart, part.BodyLength);
    return part.TransferEncoding switch {
      "base64" => DecodeBase64(body),
      "quoted-printable" => DecodeQuotedPrintable(body),
      "7bit" or "8bit" or "binary" => body.ToArray(),
      var other => throw new NotSupportedException($"MHTML part '{part.Name}' uses the unsupported transfer encoding '{other}'."),
    };
  }

  /// <summary>
  /// The origin (scheme + authority) of the root resource's location, e.g. <c>https://example.com</c>;
  /// resources on that origin are named by their path alone.
  /// </summary>
  public string? RootOrigin {
    get {
      var root = this.Parts.FirstOrDefault(p => p.Location != null);
      return root != null && Uri.TryCreate(root.Location, UriKind.Absolute, out var u) && !u.IsFile && u.Scheme != "cid" && u.Authority.Length > 0
        ? u.GetLeftPart(UriPartial.Authority)
        : null;
    }
  }

  // ── naming ───────────────────────────────────────────────────────────

  private void AssignNames(List<MhtPart> parts) {
    var origin = this.RootOrigin;
    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < parts.Count; ++i) {
      var p = parts[i];
      var name = NameFor(p.Location, p.Header("Content-ID"), origin, p.MediaType) ?? $"part_{i:D2}{MhtMediaTypes.ExtensionFor(p.MediaType)}";
      var unique = name;
      for (var n = 2; !used.Add(unique); ++n) {
        var ext = Path.GetExtension(name);
        unique = $"{name[..^ext.Length]}~{n}{ext}";
      }
      p.Name = unique;
    }
  }

  /// <summary>Maps a Content-Location (or Content-ID) to a relative archive path.</summary>
  internal static string? NameFor(string? location, string? contentId, string? rootOrigin, string mediaType) {
    if (string.IsNullOrWhiteSpace(location)) {
      var id = contentId?.Trim().Trim('<', '>');
      return string.IsNullOrEmpty(id) ? null : "cid/" + Sanitize(id);
    }
    location = location.Trim();
    if (location.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
      return "cid/" + Sanitize(location[4..]);

    string path;
    if (HasScheme(location) && Uri.TryCreate(location, UriKind.Absolute, out var u)) {
      var local = Uri.UnescapeDataString(u.AbsolutePath).TrimStart('/');
      if (u.Query.Length > 0) local += "_" + Uri.UnescapeDataString(u.Query[1..]);
      var origin = u.Authority.Length > 0 ? u.GetLeftPart(UriPartial.Authority) : null;
      if (u.IsFile || origin == null)
        path = (u.Host.Length > 0 ? u.Host + "/" : "") + local;
      else if (string.Equals(origin, rootOrigin, StringComparison.OrdinalIgnoreCase))
        path = local;
      else
        path = u.Authority + "/" + local;
    } else {
      path = Uri.UnescapeDataString(location).TrimStart('/');
    }

    if (path.Length == 0 || path.EndsWith('/'))
      path += "index" + MhtMediaTypes.ExtensionFor(mediaType);
    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
      .Where(s => s is not ("." or ".."))
      .Select(Sanitize)
      .Where(s => s.Length > 0)
      .ToArray();
    return segments.Length == 0 ? null : string.Join('/', segments);
  }

  /// <summary>An RFC 3986 scheme of two or more characters (a single letter is a Windows drive).</summary>
  private static bool HasScheme(string location) {
    var colon = location.IndexOf(':');
    if (colon < 2 || !char.IsAsciiLetter(location[0])) return false;
    for (var i = 1; i < colon; ++i)
      if (!(char.IsAsciiLetterOrDigit(location[i]) || location[i] is '+' or '-' or '.')) return false;
    return true;
  }

  private static string Sanitize(string segment) {
    var sb = new StringBuilder(segment.Length);
    foreach (var c in segment)
      sb.Append(c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\' or '/' || char.IsControl(c) ? '_' : c);
    return sb.ToString().TrimEnd('.', ' ');
  }

  // ── MIME structure ───────────────────────────────────────────────────

  /// <summary>
  /// Splits a multipart body into parts, recursing into nested multiparts. Returns the offset at
  /// which the close delimiter's segment (its leading line break) starts.
  /// </summary>
  private static int SplitMultipart(byte[] data, int start, int end, string boundary, List<MhtPart> parts, bool topLevel) {
    var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
    var delimiters = new List<(int LineStart, int SegmentStart, int AfterLine, bool Close)>();
    for (var i = start; i + delimiter.Length <= end; ++i) {
      if (i != start && data[i - 1] != '\n') {
        var next = Array.IndexOf(data, (byte)'\n', i, end - i);
        if (next < 0) break;
        i = next;
        continue;
      }
      if (!data.AsSpan(i, delimiter.Length).SequenceEqual(delimiter)) continue;
      var after = i + delimiter.Length;
      var close = after + 1 < end && data[after] == '-' && data[after + 1] == '-';
      var lineEnd = Array.IndexOf(data, (byte)'\n', after, end - after);
      var afterLine = lineEnd < 0 ? end : lineEnd + 1;
      // Only transport padding may follow the delimiter (RFC 2046 §5.1.1); "--boundaryX" is content.
      var tail = data.AsSpan(close ? after + 2 : after, (lineEnd < 0 ? end : lineEnd) - (close ? after + 2 : after));
      if (tail.TrimEnd((byte)'\r').ContainsAnyExcept((byte)' ', (byte)'\t')) continue;
      // RFC 2046: the line break preceding a delimiter belongs to the delimiter.
      var segmentStart = i;
      if (segmentStart > start && data[segmentStart - 1] == '\n') {
        --segmentStart;
        if (segmentStart > start && data[segmentStart - 1] == '\r') --segmentStart;
      }
      delimiters.Add((i, segmentStart, afterLine, close));
      if (close) break;
      i = afterLine - 1;
    }

    if (delimiters.Count == 0)
      throw new InvalidDataException($"MHTML multipart body has no '--{boundary}' delimiter.");
    if (!delimiters[^1].Close)
      throw new InvalidDataException($"MHTML multipart body is truncated: no closing '--{boundary}--' delimiter.");

    for (var k = 0; k + 1 < delimiters.Count; ++k) {
      var partStart = delimiters[k].AfterLine;
      var partEnd = delimiters[k + 1].SegmentStart;
      if (partEnd < partStart) partEnd = partStart;
      var (headersEnd, bodyStart) = SplitHeaders(data, partStart, partEnd);
      var headers = ParseHeaders(data, partStart, headersEnd);
      var ct = headers.GetValueOrDefault("Content-Type") ?? "";
      if (ct.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) && Parameter(ct, "boundary") is { } inner)
        SplitMultipart(data, bodyStart, partEnd, inner, parts, topLevel: false);
      else
        parts.Add(new MhtPart(headers, bodyStart, partEnd - bodyStart, delimiters[k].SegmentStart, delimiters[k + 1].SegmentStart, topLevel));
    }
    return delimiters[^1].SegmentStart;
  }

  /// <summary>Finds the blank line ending a header block: returns (end of headers, start of body).</summary>
  private static (int HeadersEnd, int BodyStart) SplitHeaders(byte[] data, int start, int end) {
    if (start < end && data[start] == '\n') return (start, start + 1);
    if (start + 1 < end && data[start] == '\r' && data[start + 1] == '\n') return (start, start + 2);
    for (var i = start; i < end; ++i) {
      if (data[i] != '\n') continue;
      if (i + 1 < end && data[i + 1] == '\n') return (i + 1, i + 2);
      if (i + 2 < end && data[i + 1] == '\r' && data[i + 2] == '\n') return (i + 1, i + 3);
    }
    return (end, end);
  }

  private static Dictionary<string, string> ParseHeaders(byte[] data, int start, int end) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? name = null;
    var value = new StringBuilder();
    foreach (var raw in Encoding.Latin1.GetString(data, start, end - start).Split('\n')) {
      var line = raw.TrimEnd('\r');
      if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t')) {
        if (name != null) value.Append(' ').Append(line.Trim());
        continue;
      }
      if (name != null) result.TryAdd(name, value.ToString().Trim());
      name = null;
      value.Clear();
      var colon = line.IndexOf(':');
      if (colon <= 0) continue;
      name = line[..colon].Trim();
      value.Append(line[(colon + 1)..]);
    }
    if (name != null) result.TryAdd(name, value.ToString().Trim());
    return result;
  }

  /// <summary>A header parameter (e.g. <c>boundary</c>), unquoted.</summary>
  internal static string? Parameter(string header, string parameter) {
    var pos = 0;
    while ((pos = header.IndexOf(';', pos)) >= 0) {
      ++pos;
      var eq = header.IndexOf('=', pos);
      if (eq < 0) return null;
      var key = header[pos..eq].Trim();
      var vStart = eq + 1;
      while (vStart < header.Length && header[vStart] == ' ') ++vStart;
      string val;
      int next;
      if (vStart < header.Length && header[vStart] == '"') {
        var close = header.IndexOf('"', vStart + 1);
        if (close < 0) close = header.Length;
        val = header[(vStart + 1)..close];
        next = close;
      } else {
        var semi = header.IndexOf(';', vStart);
        next = semi < 0 ? header.Length : semi;
        val = header[vStart..next].Trim();
      }
      if (key.Equals(parameter, StringComparison.OrdinalIgnoreCase)) return val;
      pos = next;
    }
    return null;
  }

  private static string DetectLineBreak(byte[] data, int headersEnd) {
    var lf = Array.IndexOf(data, (byte)'\n', 0, Math.Max(0, Math.Min(headersEnd + 2, data.Length)));
    return lf > 0 && data[lf - 1] == '\r' ? "\r\n" : lf < 0 ? "\r\n" : "\n";
  }

  // ── transfer decodings (RFC 2045 §6.7, §6.8) ─────────────────────────

  private static byte[] DecodeBase64(ReadOnlySpan<byte> body) {
    var chars = new char[body.Length];
    var n = 0;
    foreach (var b in body)
      if (b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'+' or (byte)'/' or (byte)'=')
        chars[n++] = (char)b;
      else if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
        throw new InvalidDataException($"MHTML base64 body contains the illegal byte 0x{b:X2}.");
    try {
      return Convert.FromBase64CharArray(chars, 0, n);
    } catch (FormatException ex) {
      throw new InvalidDataException("MHTML base64 body is malformed.", ex);
    }
  }

  private static byte[] DecodeQuotedPrintable(ReadOnlySpan<byte> body) {
    var result = new byte[body.Length];
    var n = 0;
    for (var i = 0; i < body.Length; ++i) {
      var b = body[i];
      if (b == '=') {
        // Soft line break: '=' followed by optional transport padding and a line break.
        var j = i + 1;
        while (j < body.Length && body[j] is (byte)' ' or (byte)'\t') ++j;
        if (j < body.Length && body[j] == '\n') { i = j; continue; }
        if (j + 1 < body.Length && body[j] == '\r' && body[j + 1] == '\n') { i = j + 1; continue; }
        if (j == body.Length) { i = j; continue; }
        if (i + 2 < body.Length && Hex(body[i + 1]) is var hi and >= 0 && Hex(body[i + 2]) is var lo and >= 0) {
          result[n++] = (byte)(hi << 4 | lo);
          i += 2;
          continue;
        }
        // RFC 2045 §6.7 note (1): a robust decoder passes a malformed '=' through.
        result[n++] = b;
        continue;
      }
      if (b is (byte)' ' or (byte)'\t') {
        // Trailing whitespace before a line break was added in transport and is removed.
        var j = i;
        while (j < body.Length && body[j] is (byte)' ' or (byte)'\t') ++j;
        if (j == body.Length || body[j] == '\n' || (body[j] == '\r' && j + 1 < body.Length && body[j + 1] == '\n')) { i = j - 1; continue; }
      }
      result[n++] = b;
    }
    return result.AsSpan(0, n).ToArray();
  }

  private static int Hex(byte b) => b switch {
    >= (byte)'0' and <= (byte)'9' => b - '0',
    >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
    >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
    _ => -1,
  };
}
