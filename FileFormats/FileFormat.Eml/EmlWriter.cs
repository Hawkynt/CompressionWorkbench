#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileFormat.Eml;

/// <summary>
/// WORM writer for RFC 2822 / MIME email messages. When a single input is
/// provided the writer emits a single-part message; with N inputs it emits a
/// <c>multipart/mixed</c> body with one part per input.
/// </summary>
/// <remarks>
/// <para>
/// Output is CRLF-terminated per RFC 5322 §2.3. Per-part bodies are wrapped in
/// base64 (RFC 2045 §6.8) at 76-character lines, which preserves arbitrary
/// binary content verbatim and is line-length-safe under SMTP. Headers are
/// fixed: <c>From</c>/<c>To</c>/<c>Subject</c>/<c>Date</c>/<c>Message-ID</c>
/// are pulled from <see cref="Compression.Registry.FormatCreateOptions"/>'s
/// format-specific knobs when present, otherwise use deterministic defaults
/// so round-trips are reproducible.
/// </para>
/// <para>
/// The multipart boundary is derived from a hash of the input list so it never
/// collides with the content payload (probabilistically), but it doesn't change
/// across builds when the input is the same — important for reproducible test
/// fixtures.
/// </para>
/// </remarks>
public sealed class EmlWriter {

  /// <summary>
  /// Writes a single .eml message to <paramref name="output"/>. With zero or one
  /// input the message is single-part; with two or more inputs it becomes a
  /// <c>multipart/mixed</c> message and each input becomes one attachment.
  /// </summary>
  /// <param name="output">Target stream; not closed by this method.</param>
  /// <param name="parts">Per-part (name, bytes, mime-type) tuples. <paramref name="parts"/>
  ///   may be empty — the message still gets a valid envelope and an empty body.</param>
  /// <param name="headers">Optional envelope overrides. Keys are case-insensitive;
  ///   unknown keys are emitted as additional headers. <c>From</c>, <c>To</c>,
  ///   <c>Subject</c>, <c>Date</c>, <c>Message-ID</c> default to a deterministic
  ///   minimal envelope.</param>
  public static void Write(
      Stream output,
      IReadOnlyList<(string Name, byte[] Data, string? MimeType)> parts,
      IReadOnlyDictionary<string, string>? headers = null) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(parts);

    var hdrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["From"] = "noreply@compression-workbench.invalid",
      ["To"] = "recipient@compression-workbench.invalid",
      ["Subject"] = "CompressionWorkbench EML container",
      ["Date"] = "Mon, 01 Jan 2024 00:00:00 +0000",
      ["Message-ID"] = "<" + DeterministicId(parts) + "@compression-workbench.invalid>",
      ["MIME-Version"] = "1.0",
    };
    if (headers != null)
      foreach (var kv in headers) hdrs[kv.Key] = kv.Value;

    using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true) {
      NewLine = "\r\n",
    };

    if (parts.Count <= 1) {
      // Single-part message.
      var mime = parts.Count == 1 ? parts[0].MimeType ?? "application/octet-stream" : "text/plain; charset=utf-8";
      hdrs["Content-Type"] = mime;
      if (parts.Count == 1)
        hdrs["Content-Transfer-Encoding"] = "base64";
      EmitHeaders(writer, hdrs);
      writer.WriteLine();
      if (parts.Count == 1)
        EmitBase64(writer, parts[0].Data);
      writer.Flush();
      return;
    }

    // Multipart message.
    var boundary = "----=_Part_" + DeterministicId(parts);
    hdrs["Content-Type"] = $"multipart/mixed; boundary=\"{boundary}\"";
    EmitHeaders(writer, hdrs);
    writer.WriteLine();
    writer.WriteLine("This is a multi-part message in MIME format.");
    foreach (var (name, data, mime) in parts) {
      var partMime = mime ?? "application/octet-stream";
      writer.WriteLine("--" + boundary);
      writer.WriteLine($"Content-Type: {partMime}; name=\"{QuoteForHeader(name)}\"");
      writer.WriteLine($"Content-Disposition: attachment; filename=\"{QuoteForHeader(name)}\"");
      writer.WriteLine("Content-Transfer-Encoding: base64");
      writer.WriteLine();
      EmitBase64(writer, data);
    }
    writer.WriteLine("--" + boundary + "--");
    writer.Flush();
  }

  private static void EmitHeaders(StreamWriter w, IReadOnlyDictionary<string, string> headers) {
    // Stable ordering: envelope headers first, then the rest sorted.
    var order = new[] { "From", "To", "Subject", "Date", "Message-ID", "MIME-Version", "Content-Type", "Content-Transfer-Encoding" };
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var key in order) {
      if (headers.TryGetValue(key, out var v)) {
        w.WriteLine($"{key}: {v}");
        seen.Add(key);
      }
    }
    foreach (var kv in headers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) {
      if (seen.Contains(kv.Key)) continue;
      w.WriteLine($"{kv.Key}: {kv.Value}");
    }
  }

  private static void EmitBase64(StreamWriter w, byte[] data) {
    var b64 = Convert.ToBase64String(data);
    for (var i = 0; i < b64.Length; i += 76)
      w.WriteLine(b64.AsSpan(i, Math.Min(76, b64.Length - i)));
  }

  /// <summary>
  /// Replaces CR / LF / double-quote in a filename so it fits in an RFC 2822
  /// header value. Not a complete RFC 2047 encoder — sufficient for typical
  /// archive filenames.
  /// </summary>
  private static string QuoteForHeader(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (var c in s) {
      if (c is '\r' or '\n' or '"') sb.Append('_');
      else sb.Append(c);
    }
    return sb.ToString();
  }

  /// <summary>
  /// Builds a deterministic per-input identifier used as a Message-ID local-part
  /// and multipart boundary. The same inputs always produce the same id, so
  /// reproducible builds of the same archive produce byte-identical .eml output.
  /// </summary>
  private static string DeterministicId(IReadOnlyList<(string Name, byte[] Data, string? MimeType)> parts) {
    unchecked {
      uint h = 2166136261; // FNV-1a 32
      foreach (var (n, d, m) in parts) {
        foreach (var c in n) { h ^= c; h *= 16777619; }
        h ^= (uint)d.Length; h *= 16777619;
        if (m != null) foreach (var c in m) { h ^= c; h *= 16777619; }
      }
      return h.ToString("X8", CultureInfo.InvariantCulture);
    }
  }
}
