#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileFormat.Warc;

/// <summary>
/// Writes WARC/1.0 archives. Each input becomes one record (default type
/// "resource"); the existing <see cref="WarcReader"/> roundtrips them.
///
/// Per the WARC spec each record looks like:
/// <code>
/// WARC/1.0 CRLF
/// header: value CRLF
/// ...
/// CRLF
/// payload (Content-Length bytes)
/// CRLF CRLF
/// </code>
/// </summary>
public sealed class WarcWriter {
  private const string CRLF = "\r\n";
  private static readonly byte[] CrlfBytes = "\r\n"u8.ToArray();

  private readonly List<(WarcEntry Entry, byte[] Payload)> _records = [];

  /// <summary>Adds a record. Required: <c>Type</c>; recommended: <c>TargetUri</c>, <c>Date</c>.</summary>
  public void AddRecord(WarcEntry entry, byte[] payload) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(payload);
    if (string.IsNullOrEmpty(entry.Type))
      throw new ArgumentException("WARC-Type is required.", nameof(entry));
    _records.Add((entry, payload));
  }

  /// <summary>
  /// Convenience helper for the common "I just have files to wrap" case --
  /// emits one "resource" record per file.
  /// </summary>
  public void AddResource(string targetUri, byte[] payload, string? contentType = null, DateTime? date = null) {
    var entry = new WarcEntry {
      Type = "resource",
      TargetUri = targetUri,
      RecordId = $"<urn:uuid:{Guid.NewGuid():D}>",
      Date = (date ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
      ContentType = contentType ?? "application/octet-stream",
      ContentLength = payload.Length,
    };
    AddRecord(entry, payload);
  }

  public void WriteTo(Stream output) {
    foreach (var (entry, payload) in _records) {
      // Mirror Content-Length to the actual payload to avoid mismatches.
      entry.ContentLength = payload.Length;

      var headers = new StringBuilder();
      headers.Append("WARC/1.0").Append(CRLF);
      AppendHeader(headers, "WARC-Type", entry.Type);
      AppendHeader(headers, "WARC-Record-ID", string.IsNullOrEmpty(entry.RecordId)
        ? $"<urn:uuid:{Guid.NewGuid():D}>" : entry.RecordId);
      AppendHeader(headers, "WARC-Date", entry.Date ?? DateTime.UtcNow.ToString(
        "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
      if (!string.IsNullOrEmpty(entry.TargetUri))
        AppendHeader(headers, "WARC-Target-URI", entry.TargetUri);
      if (!string.IsNullOrEmpty(entry.ContentType))
        AppendHeader(headers, "Content-Type", entry.ContentType);
      AppendHeader(headers, "Content-Length", payload.Length.ToString(CultureInfo.InvariantCulture));
      headers.Append(CRLF); // blank line ending headers

      var headerBytes = Encoding.UTF8.GetBytes(headers.ToString());
      output.Write(headerBytes);
      output.Write(payload);
      output.Write(CrlfBytes); // mandatory trailer: two CRLFs
      output.Write(CrlfBytes);
    }
  }

  private static void AppendHeader(StringBuilder sb, string name, string value)
    => sb.Append(name).Append(": ").Append(value).Append(CRLF);
}
