using System.Text;

namespace FileFormat.Mht;

/// <summary>
/// Writes MHTML documents: a <c>multipart/related</c> message whose first part is the root page and
/// every part carries a <c>Content-Location</c> under one base URL, so relative references in the page
/// resolve to the archived resources (RFC 2557 §8).
/// </summary>
/// <remarks>
/// The default base is <c>https://mhtml.invalid/</c> — the reserved <c>.invalid</c> TLD (RFC 2606)
/// guarantees a browser can only ever satisfy those URLs from the archive. Chromium does not resolve
/// <c>file:</c> subresources out of an MHTML archive, which is why the base is not <c>file:///</c>.
/// Bodies are always base64: its alphabet cannot begin a line with <c>--</c>, so no body can collide
/// with the boundary.
/// </remarks>
public static class MhtWriter {

  /// <summary>The URL every created resource is located under.</summary>
  public const string DefaultBaseUrl = "https://mhtml.invalid/";

  /// <summary>The fixed boundary of documents this writer creates.</summary>
  public const string DefaultBoundary = "----=_NextPart_MhtArchive_000";

  public static void Write(Stream output, IReadOnlyList<(string Name, byte[] Data)> files, string? subject = null, string baseUrl = DefaultBaseUrl) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(files);
    const string lb = "\r\n";
    var sb = new StringBuilder();
    if (!string.IsNullOrEmpty(subject))
      sb.Append("Subject: ").Append(EncodeHeaderText(subject)).Append(lb);
    var rootType = files.Count > 0 ? MhtMediaTypes.MediaTypeFor(files[0].Name) : "text/html";
    sb.Append("MIME-Version: 1.0").Append(lb)
      .Append("Content-Type: multipart/related;").Append(lb)
      .Append("\ttype=\"").Append(rootType).Append("\";").Append(lb)
      .Append("\tboundary=\"").Append(DefaultBoundary).Append('"').Append(lb)
      .Append(lb);
    output.Write(Encoding.ASCII.GetBytes(sb.ToString()));
    foreach (var (name, data) in files)
      output.Write(PartSegment(DefaultBoundary, lb, baseUrl + EscapePath(name), MhtMediaTypes.MediaTypeFor(name), data));
    output.Write(Encoding.ASCII.GetBytes(lb + "--" + DefaultBoundary + "--" + lb));
  }

  /// <summary>
  /// One part as it sits in a multipart body: the delimiter's leading line break, the delimiter line,
  /// the part headers, a blank line and the base64 body (without a trailing line break — that belongs
  /// to the next delimiter).
  /// </summary>
  internal static byte[] PartSegment(string boundary, string lb, string location, string mediaType, byte[] data) {
    ValidateName(location);
    var sb = new StringBuilder();
    sb.Append(lb).Append("--").Append(boundary).Append(lb)
      .Append("Content-Type: ").Append(mediaType).Append(lb)
      .Append("Content-Transfer-Encoding: base64").Append(lb)
      .Append("Content-Location: ").Append(location).Append(lb)
      .Append(lb);
    var b64 = Convert.ToBase64String(data);
    for (var i = 0; i < b64.Length; i += 76) {
      if (i > 0) sb.Append(lb);
      sb.Append(b64, i, Math.Min(76, b64.Length - i));
    }
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  /// <summary>Percent-encodes a slash-separated relative path for use in a URL (RFC 3986 pchar).</summary>
  internal static string EscapePath(string path)
    => string.Join('/', path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

  private static void ValidateName(string location) {
    if (location.Any(c => c is '\r' or '\n' || c > 0x7E))
      throw new ArgumentException($"MHTML location '{location}' is not a header-safe URL.", nameof(location));
  }

  private static string EncodeHeaderText(string text)
    => text.All(c => c is >= ' ' and <= '~')
      ? text
      : "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "?=";
}
