#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Eml;

/// <summary>
/// In-place modifier for RFC 822 / MIME messages with a multipart body. Add
/// appends a fresh MIME part immediately before the closing
/// <c>--&lt;boundary&gt;--</c> delimiter; Remove deletes the byte range of a
/// single part between two <c>--&lt;boundary&gt;</c> markers. No re-encoding
/// of surviving parts: every byte that wasn't directly touched by the
/// requested change is preserved verbatim at its original offset.
///
/// <para>Add requires the message to already be a <c>multipart/*</c> body
/// (we need the boundary marker to splice against). Single-part messages
/// can't be promoted to multipart in place without rewriting the top-level
/// Content-Type header — which would cascade through the body offsets — so
/// the modifier rejects that case with <see cref="NotSupportedException"/>.</para>
///
/// <para>Remove identifies the target part by attachment filename (from
/// <c>Content-Disposition: attachment; filename="…"</c>) or by zero-based
/// MIME part index. The bytes between the leading <c>--&lt;boundary&gt;</c>
/// delimiter that introduces the part and the next <c>--&lt;boundary&gt;</c>
/// delimiter (or the closing <c>--&lt;boundary&gt;--</c>) are spliced out.</para>
///
/// <para>Out of scope: editing single-part messages, editing the top-level
/// header block, editing nested multipart bodies (only the top-level
/// multipart container is mutated), changing Content-Transfer-Encoding on
/// surviving parts. Touching any of those would require offset cascades
/// the "in-place at fixed offset" surface honestly can't deliver.</para>
///
/// <para>Spec source: RFC 2045 (MIME headers), RFC 2046 (multipart bodies),
/// RFC 5322 (RFC 822 successor).</para>
/// </summary>
public static class EmlInPlaceModifier {

  /// <summary>
  /// Splices a fresh MIME part into a multipart message immediately before
  /// the closing <c>--&lt;boundary&gt;--</c> delimiter. <paramref name="content"/>
  /// is base64-encoded so it survives the transfer encoding round-trip; the
  /// Content-Disposition is set to <c>attachment</c> so the reader exposes
  /// the new part under <c>attachments/&lt;name&gt;</c>.
  /// </summary>
  /// <exception cref="ArgumentNullException">Any argument is null.</exception>
  /// <exception cref="InvalidDataException">The archive is not a parseable
  /// MIME message.</exception>
  /// <exception cref="NotSupportedException">The message is not multipart, or
  /// the closing boundary marker can't be located.</exception>
  public static void AddAttachment(Stream archive, string fileName, byte[] content, string? contentType = null) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(content);

    var blob = ReadAll(archive);
    var (boundary, bodyStart) = FindMultipartBoundary(blob);

    var closing = FindClosingBoundary(blob, boundary, bodyStart);
    if (closing < 0)
      throw new NotSupportedException(
        "EML: closing '--boundary--' delimiter not found; can't splice before it without rewriting the body.");

    // Build the new part: boundary marker + minimal MIME headers + blank line
    // + base64-encoded content + CRLF. The trailing CRLF separates us from the
    // closing delimiter that already sits at byte offset 'closing'.
    var part = BuildAttachmentPart(boundary, fileName, content, contentType);

    var grown = new byte[blob.Length + part.Length];
    blob.AsSpan(0, closing).CopyTo(grown);
    part.CopyTo(grown.AsSpan(closing));
    blob.AsSpan(closing, blob.Length - closing).CopyTo(grown.AsSpan(closing + part.Length));

    WriteBlob(archive, grown);
  }

  /// <summary>
  /// Removes a MIME part from a multipart message by attachment filename. The
  /// byte range from the part's leading <c>--&lt;boundary&gt;</c> delimiter
  /// (inclusive) to the next delimiter (exclusive) is spliced out.
  /// </summary>
  /// <exception cref="ArgumentNullException">Any argument is null.</exception>
  /// <exception cref="InvalidDataException">The archive is not a parseable
  /// MIME message.</exception>
  /// <exception cref="NotSupportedException">The message is not multipart.</exception>
  /// <exception cref="FileNotFoundException">No attachment matches <paramref name="fileName"/>.</exception>
  public static void RemoveAttachment(Stream archive, string fileName) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(fileName);

    var blob = ReadAll(archive);
    var (boundary, bodyStart) = FindMultipartBoundary(blob);
    var delimPositions = FindDelimiterPositions(blob, boundary, bodyStart);
    if (delimPositions.Count < 2)
      throw new NotSupportedException("EML: multipart body has fewer than two boundary markers.");

    // Walk each [start, next) range, treating it as a part. The last range ends
    // at the closing '--boundary--' delimiter (which appears once at the end
    // of the list because we recorded it as a delimiter position too).
    var delim = Encoding.ASCII.GetBytes("--" + boundary);
    for (var i = 0; i < delimPositions.Count - 1; i++) {
      var partStart = delimPositions[i];
      // Skip past the delimiter line itself.
      var afterDelim = partStart + delim.Length;
      if (afterDelim + 1 < blob.Length && blob[afterDelim] == '-' && blob[afterDelim + 1] == '-')
        break; // hit the closing delimiter — no more parts past this point.
      while (afterDelim < blob.Length && (blob[afterDelim] == '\r' || blob[afterDelim] == '\n'))
        afterDelim++;
      var partEnd = delimPositions[i + 1];

      if (!PartIsAttachmentNamed(blob.AsSpan(afterDelim, partEnd - afterDelim), fileName)) continue;

      // Splice out [partStart, partEnd). The next delimiter slides up to take
      // partStart's place; every surviving byte stays at its original offset
      // except those after partEnd which shift left by (partEnd - partStart).
      var newSize = blob.Length - (partEnd - partStart);
      var shrunk = new byte[newSize];
      blob.AsSpan(0, partStart).CopyTo(shrunk);
      blob.AsSpan(partEnd, blob.Length - partEnd).CopyTo(shrunk.AsSpan(partStart));
      WriteBlob(archive, shrunk);
      return;
    }

    throw new FileNotFoundException($"EML: no attachment with filename '{fileName}' found in the message.");
  }

  // ── helpers ────────────────────────────────────────────────────────────────

  private static byte[] ReadAll(Stream archive) {
    if (archive.CanSeek) archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    return ms.ToArray();
  }

  private static void WriteBlob(Stream archive, byte[] blob) {
    if (!archive.CanSeek)
      throw new NotSupportedException("EML in-place modifier requires a seekable stream.");
    archive.Position = 0;
    archive.SetLength(blob.Length);
    archive.Write(blob, 0, blob.Length);
    archive.Flush();
  }

  /// <summary>
  /// Locates the top-level Content-Type header, extracts the multipart
  /// boundary, and returns the body byte offset (immediately after the
  /// header-body separator).
  /// </summary>
  private static (string Boundary, int BodyStart) FindMultipartBoundary(byte[] blob) {
    var split = FindHeaderBodySplit(blob);
    var headerText = Encoding.Latin1.GetString(blob, 0, split.HeadersEnd);
    var ct = FindHeader(headerText, "Content-Type");
    if (ct == null || !ct.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
      throw new NotSupportedException(
        "EML in-place modifier requires a multipart/* top-level Content-Type. Single-part " +
        "messages can't be edited in place without rewriting the header block.");
    var boundary = EmlParser.ParseParameter(ct, "boundary");
    if (string.IsNullOrEmpty(boundary))
      throw new InvalidDataException("EML: multipart Content-Type has no boundary parameter.");
    return (boundary, split.BodyStart);
  }

  private readonly record struct HeaderBodySplit(int HeadersEnd, int BodyStart);

  private static HeaderBodySplit FindHeaderBodySplit(byte[] blob) {
    for (var i = 0; i + 3 < blob.Length; i++) {
      if (blob[i] == '\r' && blob[i + 1] == '\n' && blob[i + 2] == '\r' && blob[i + 3] == '\n')
        return new HeaderBodySplit(i, i + 4);
    }
    for (var i = 0; i + 1 < blob.Length; i++) {
      if (blob[i] == '\n' && blob[i + 1] == '\n')
        return new HeaderBodySplit(i, i + 2);
    }
    throw new InvalidDataException("EML: header/body separator (blank line) not found.");
  }

  private static string? FindHeader(string headerText, string name) {
    var lines = headerText.Split('\n');
    var current = new StringBuilder();
    string? currentName = null;
    string? result = null;

    void Flush() {
      if (currentName != null && currentName.Equals(name, StringComparison.OrdinalIgnoreCase))
        result ??= current.ToString().Trim();
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

  /// <summary>
  /// Returns every byte offset inside <paramref name="blob"/> at which a
  /// <c>--boundary</c> delimiter sits at the start of a line (covers both
  /// the part-introducing <c>--boundary</c> form and the closing
  /// <c>--boundary--</c> form, which the closing-detection routine
  /// distinguishes by looking at the two bytes after the delim).
  /// </summary>
  private static List<int> FindDelimiterPositions(byte[] blob, string boundary, int bodyStart) {
    var delim = Encoding.ASCII.GetBytes("--" + boundary);
    var positions = new List<int>();
    for (var i = bodyStart; i + delim.Length <= blob.Length; i++) {
      if (!blob.AsSpan(i, delim.Length).SequenceEqual(delim)) continue;
      var atLineStart = i == bodyStart || blob[i - 1] == '\n';
      if (!atLineStart) continue;
      positions.Add(i);
    }
    return positions;
  }

  private static int FindClosingBoundary(byte[] blob, string boundary, int bodyStart) {
    var delim = Encoding.ASCII.GetBytes("--" + boundary + "--");
    for (var i = bodyStart; i + delim.Length <= blob.Length; i++) {
      if (!blob.AsSpan(i, delim.Length).SequenceEqual(delim)) continue;
      var atLineStart = i == bodyStart || blob[i - 1] == '\n';
      if (atLineStart) return i;
    }
    return -1;
  }

  private static bool PartIsAttachmentNamed(ReadOnlySpan<byte> partBytes, string fileName) {
    // Parse the part headers (everything up to the first blank line).
    var headersEnd = -1;
    for (var i = 0; i + 3 < partBytes.Length; i++) {
      if (partBytes[i] == '\r' && partBytes[i + 1] == '\n' && partBytes[i + 2] == '\r' && partBytes[i + 3] == '\n') {
        headersEnd = i;
        break;
      }
    }
    if (headersEnd < 0) {
      for (var i = 0; i + 1 < partBytes.Length; i++) {
        if (partBytes[i] == '\n' && partBytes[i + 1] == '\n') {
          headersEnd = i;
          break;
        }
      }
    }
    if (headersEnd < 0) return false;

    var headerText = Encoding.Latin1.GetString(partBytes[..headersEnd]);
    var cd = FindHeader(headerText, "Content-Disposition");
    var ct = FindHeader(headerText, "Content-Type");
    var name = EmlParser.ParseParameter(cd, "filename") ?? EmlParser.ParseParameter(ct, "name");
    return string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase);
  }

  private static byte[] BuildAttachmentPart(string boundary, string fileName, byte[] content, string? contentType) {
    var sb = new StringBuilder();
    sb.Append("--").Append(boundary).Append("\r\n");
    sb.Append("Content-Type: ").Append(contentType ?? "application/octet-stream")
      .Append("; name=\"").Append(EscapeQuoted(fileName)).Append("\"\r\n");
    sb.Append("Content-Disposition: attachment; filename=\"")
      .Append(EscapeQuoted(fileName)).Append("\"\r\n");
    sb.Append("Content-Transfer-Encoding: base64\r\n");
    sb.Append("\r\n");

    var b64 = Convert.ToBase64String(content);
    // RFC 2045 caps base64 lines at 76 chars; split to that width for compat.
    for (var i = 0; i < b64.Length; i += 76) {
      var len = Math.Min(76, b64.Length - i);
      sb.Append(b64, i, len);
      sb.Append("\r\n");
    }

    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  private static string EscapeQuoted(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
