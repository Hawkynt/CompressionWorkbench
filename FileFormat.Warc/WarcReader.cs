using System.Text;

namespace FileFormat.Warc;

/// <summary>
/// Reads WARC records sequentially from a stream. Supports WARC/1.0 and WARC/1.1.
/// </summary>
public sealed class WarcReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="WarcReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the WARC archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public WarcReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Reads all records from the archive, returning each entry with its payload bytes.
  /// </summary>
  public List<(WarcEntry Entry, byte[] Payload)> ReadAll() {
    var result = new List<(WarcEntry, byte[])>();
    while (ReadNext() is { } pair)
      result.Add(pair);
    return result;
  }

  /// <summary>
  /// Reads the next record from the stream.
  /// </summary>
  /// <returns>The entry and its payload, or null if the end of stream has been reached.</returns>
  public (WarcEntry Entry, byte[] Payload)? ReadNext() {
    // Skip any leading blank lines between records (\r\n or \n)
    string? versionLine = null;
    while (true) {
      var line = ReadLine();
      if (line == null)
        return null;
      if (line.Length == 0)
        continue;
      versionLine = line;
      break;
    }

    // Validate WARC version line
    if (!versionLine.StartsWith("WARC/", StringComparison.Ordinal))
      throw new InvalidDataException($"Expected WARC version line, got: {versionLine}");

    // Parse headers until the blank line
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    while (true) {
      var line = ReadLine();
      if (line == null)
        throw new InvalidDataException("Unexpected end of stream in WARC record headers.");
      if (line.Length == 0)
        break; // blank line ends headers
      var colon = line.IndexOf(':');
      if (colon <= 0)
        continue; // skip malformed header lines
      var key = line[..colon].Trim();
      var value = line[(colon + 1)..].Trim();
      headers[key] = value;
    }

    // Build WarcEntry
    var entry = new WarcEntry {
      Type = headers.GetValueOrDefault("WARC-Type", ""),
      TargetUri = headers.TryGetValue("WARC-Target-URI", out var uri) ? uri : null,
      RecordId = headers.GetValueOrDefault("WARC-Record-ID", ""),
      Date = headers.TryGetValue("WARC-Date", out var date) ? date : null,
      ContentType = headers.TryGetValue("Content-Type", out var ct) ? ct : null,
      ContentLength = headers.TryGetValue("Content-Length", out var cl) &&
                      long.TryParse(cl, out var len) ? len : 0,
    };

    // Record payload offset and read payload
    entry.PayloadOffset = this._stream.CanSeek ? this._stream.Position : -1L;
    var payload = new byte[entry.ContentLength];
    if (entry.ContentLength > 0)
      ReadExact(payload, 0, payload.Length);

    // Consume the mandatory two CRLF sequences after the payload (\r\n\r\n)
    // Some writers may emit \n\n, \r\n\r\n, or mixed; consume up to 4 bytes of line endings.
    ConsumeRecordTrailer();

    return (entry, payload);
  }

  // ── Private helpers ──────────────────────────────────────────────────────

  /// <summary>Reads a single text line from the stream (strips trailing \r\n or \n).</summary>
  private string? ReadLine() {
    var sb = new StringBuilder();
    while (true) {
      var b = this._stream.ReadByte();
      if (b == -1)
        return sb.Length == 0 ? null : sb.ToString();
      if (b == '\n')
        break;
      if (b == '\r') {
        var next = this._stream.ReadByte();
        if (next != '\n' && next != -1) {
          // push back by seeking if possible; otherwise just include it
          if (this._stream.CanSeek)
            this._stream.Position--;
        }
        break;
      }
      sb.Append((char)b);
    }
    return sb.ToString();
  }

  /// <summary>
  /// Consumes the two blank lines (CRLF CRLF) that follow the payload in every WARC record.
  /// The spec requires exactly \r\n\r\n; we tolerate \n\n and mixed.
  /// </summary>
  private void ConsumeRecordTrailer() {
    // We need to eat two newline sequences.  Each sequence is \r\n or bare \n.
    var newlinesConsumed = 0;
    while (newlinesConsumed < 2) {
      var b = this._stream.ReadByte();
      if (b == -1) break;
      if (b == '\r') {
        // optionally consume the following \n
        var next = this._stream.ReadByte();
        if (next != '\n' && next != -1 && this._stream.CanSeek)
          this._stream.Position--;
        newlinesConsumed++;
      } else if (b == '\n') {
        newlinesConsumed++;
      } else {
        // non-newline byte: put back and stop
        if (this._stream.CanSeek)
          this._stream.Position--;
        break;
      }
    }
  }

  private void ReadExact(byte[] buffer, int offset, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      var read = this._stream.Read(buffer, offset + totalRead, count - totalRead);
      if (read == 0)
        throw new InvalidDataException("Unexpected end of stream reading WARC payload.");
      totalRead += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
