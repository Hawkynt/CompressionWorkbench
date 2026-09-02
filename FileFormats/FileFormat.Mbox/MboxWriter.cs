#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileFormat.Mbox;

/// <summary>
/// Writer for Unix mbox mailboxes.  Each appended RFC 822 message is emitted
/// with a leading "From " separator line (column 0) per RFC 4155, and any
/// line in the body that begins with the bytes <c>From </c> is byte-stuffed
/// to <c>&gt;From </c> so the resulting mbox can be unambiguously split by
/// any RFC-conformant reader.
/// </summary>
/// <remarks>
/// <para>
/// The writer accepts each message as a complete RFC 822 byte sequence
/// (i.e. an <c>.eml</c> payload). The caller may supply an envelope sender
/// and an envelope timestamp; absent values default to
/// <c>"unknown@localhost"</c> and <see cref="DateTimeOffset.UtcNow"/>
/// formatted in the traditional asctime layout
/// (<c>Mon Jan  1 00:00:00 2024</c>) as expected by mutt / mail / procmail.
/// </para>
/// <para>
/// Both LF-only and CRLF line endings are accepted on input; the byte
/// content of each message is preserved verbatim except for the
/// <c>From </c> byte-stuffing required by the mbox specification.
/// </para>
/// </remarks>
public sealed class MboxWriter : IDisposable {

  private static readonly byte[] FromMarker = "From "u8.ToArray();
  private static readonly byte[] EscapedFromMarker = ">From "u8.ToArray();

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;
  private bool _firstMessage = true;

  /// <summary>
  /// Initializes a new <see cref="MboxWriter"/>.
  /// </summary>
  /// <param name="stream">The destination stream.</param>
  /// <param name="leaveOpen">Whether to leave the underlying stream open on dispose.</param>
  public MboxWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Appends a single RFC 822 message to the mailbox.
  /// </summary>
  /// <param name="emlBytes">Raw RFC 822 message bytes (headers + blank line + body).</param>
  /// <param name="envelopeSender">Optional envelope sender address; defaults to <c>"unknown@localhost"</c>.</param>
  /// <param name="envelopeDate">Optional delivery timestamp; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
  public void AddMessage(ReadOnlySpan<byte> emlBytes,
                         string? envelopeSender = null,
                         DateTimeOffset? envelopeDate = null) {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    // RFC 4155: messages are separated by a blank line followed by a "From " line.
    // We always emit an LF separator between messages so back-to-back appends are valid.
    if (!this._firstMessage)
      this._stream.WriteByte((byte)'\n');
    this._firstMessage = false;

    // Envelope separator: "From <sender> <ctime>\n"
    var sender = string.IsNullOrEmpty(envelopeSender) ? "unknown@localhost" : envelopeSender;
    var when = envelopeDate ?? DateTimeOffset.UtcNow;
    // asctime layout: "Mon Jan  1 00:00:00 2024" (two spaces before single-digit day).
    var ctime = FormatAsctime(when);

    var separator = $"From {sender} {ctime}\n";
    var separatorBytes = Encoding.ASCII.GetBytes(separator);
    this._stream.Write(separatorBytes);

    // Byte-stuff "From " at the start of any line in the body.
    WriteStuffed(this._stream, emlBytes);

    // Per RFC 4155 each message ends with a newline.  Only append one if the
    // payload doesn't already end with LF so we don't double-terminate.
    if (emlBytes.Length == 0 || emlBytes[^1] != (byte)'\n')
      this._stream.WriteByte((byte)'\n');
  }

  /// <summary>
  /// Writes <paramref name="data"/> to <paramref name="dest"/> with mbox
  /// "From "-line byte-stuffing applied: any line that starts with the
  /// bytes <c>From </c> is rewritten to <c>&gt;From </c>.
  /// </summary>
  private static void WriteStuffed(Stream dest, ReadOnlySpan<byte> data) {
    var lineStart = true;
    var i = 0;
    while (i < data.Length) {
      if (lineStart && StartsWithFromSpace(data, i)) {
        dest.Write(EscapedFromMarker);
        i += FromMarker.Length;
        lineStart = false;
        continue;
      }
      var b = data[i++];
      dest.WriteByte(b);
      lineStart = b == (byte)'\n';
    }
  }

  private static bool StartsWithFromSpace(ReadOnlySpan<byte> data, int offset) {
    if (offset + FromMarker.Length > data.Length) return false;
    for (var i = 0; i < FromMarker.Length; ++i)
      if (data[offset + i] != FromMarker[i]) return false;
    return true;
  }

  /// <summary>
  /// Formats <paramref name="value"/> in the asctime layout used by
  /// traditional mbox envelope separators: <c>"Mon Jan  1 00:00:00 2024"</c>.
  /// Day-of-month is padded with a space (not a leading zero) for
  /// single-digit days, matching the C <c>asctime(3)</c> convention.
  /// </summary>
  private static string FormatAsctime(DateTimeOffset value) {
    var utc = value.UtcDateTime;
    var dow = utc.ToString("ddd", CultureInfo.InvariantCulture);
    var month = utc.ToString("MMM", CultureInfo.InvariantCulture);
    var day = utc.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2);
    var time = utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    var year = utc.Year.ToString(CultureInfo.InvariantCulture);
    return $"{dow} {month} {day} {time} {year}";
  }

  /// <inheritdoc />
  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    this._stream.Flush();
    if (!this._leaveOpen) this._stream.Dispose();
  }
}
