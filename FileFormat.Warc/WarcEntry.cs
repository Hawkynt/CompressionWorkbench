namespace FileFormat.Warc;

/// <summary>
/// Represents a single record in a WARC archive.
/// </summary>
public sealed class WarcEntry {
  /// <summary>Gets or sets the WARC-Type header value (e.g. "response", "warcinfo", "resource").</summary>
  public string Type { get; set; } = "";

  /// <summary>Gets or sets the WARC-Target-URI header value, or null if not present.</summary>
  public string? TargetUri { get; set; }

  /// <summary>Gets or sets the WARC-Record-ID header value.</summary>
  public string RecordId { get; set; } = "";

  /// <summary>Gets or sets the WARC-Date header value, or null if not present.</summary>
  public string? Date { get; set; }

  /// <summary>Gets or sets the Content-Type header value, or null if not present.</summary>
  public string? ContentType { get; set; }

  /// <summary>Gets or sets the Content-Length header value in bytes.</summary>
  public long ContentLength { get; set; }

  /// <summary>Gets or sets the byte offset of the payload within the source stream.</summary>
  public long PayloadOffset { get; set; }
}
