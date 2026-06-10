#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileFormat.Mbox;

/// <summary>
/// True in-place R/W for Unix mbox mailboxes. The mbox container is a flat
/// stream of <c>From&#32;</c>-delimited messages (no central index, no
/// structural overhead), so:
/// <list type="bullet">
///   <item><b>Add</b>: append a new <c>From&#32;</c> separator + the new
///         message bytes at EOF. Every pre-existing byte stays byte-identical.</item>
///   <item><b>Remove</b>: there is no native delete in mbox. We use the
///         RFC&#160;4155 / POP3 convention of marking the message with an
///         <c>X-Status: D</c> header (already understood by mutt, pine, alpine
///         and procmail) <i>and</i> zero-wiping the message body so the
///         deleted content is unrecoverable from the bytes. The separator
///         line + headers area is preserved as a same-size record so byte
///         offsets of every other message are unchanged — truly in-place.</item>
/// </list>
/// </summary>
public static class MboxInPlaceModifier {

  /// <summary>
  /// Appends a single RFC&#160;822 message to the mailbox, prefixed by a
  /// canonical <c>From&#32;MAILER-DAEMON@LOCALHOST &lt;date&gt;</c> separator
  /// line. The mailbox bytes preceding <see cref="Stream.Length"/> are not
  /// modified. The caller is responsible for the message format itself
  /// (RFC&#160;822 + body, line endings as they prefer).
  /// </summary>
  /// <param name="mbox">A readable, writable, seekable stream.</param>
  /// <param name="messageBytes">
  /// The bytes of the message body (headers + blank line + body). Must NOT
  /// itself begin with a <c>From&#32;</c> line — this method adds the
  /// separator. Internal <c>From&#32;</c> lines should already be byte-stuffed
  /// (<c>&gt;From&#32;</c>) per mbox convention; this method does not stuff
  /// them, mirroring <see cref="MboxReader"/>'s "preserve verbatim" contract.
  /// </param>
  /// <param name="fromEnvelope">
  /// The envelope address used in the separator line. Defaults to
  /// <c>MAILER-DAEMON@LOCALHOST</c>.
  /// </param>
  /// <param name="timestamp">
  /// Timestamp inserted in the separator line. Defaults to
  /// <see cref="DateTime.UtcNow"/>. The format mirrors the standard
  /// <c>asctime()</c> form used by most MTAs (e.g.
  /// <c>Thu Jan&#160;1 00:00:00 1970</c>).
  /// </param>
  public static void Append(Stream mbox, byte[] messageBytes,
      string? fromEnvelope = null, DateTime? timestamp = null) {
    ArgumentNullException.ThrowIfNull(mbox);
    ArgumentNullException.ThrowIfNull(messageBytes);
    if (!mbox.CanSeek || !mbox.CanWrite)
      throw new ArgumentException("Mbox stream must be writable and seekable.", nameof(mbox));

    var envelope = fromEnvelope ?? "MAILER-DAEMON@LOCALHOST";
    var ts = (timestamp ?? DateTime.UtcNow).ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture);
    var separator = $"From {envelope} {ts}\n";
    var sepBytes = Encoding.Latin1.GetBytes(separator);

    mbox.Position = mbox.Length;

    // mbox separators MUST start at the beginning of a line. If the file is
    // non-empty and doesn't already end with a newline, inject one. We probe
    // the last byte rather than scanning — the file's other content stays
    // strictly untouched.
    if (mbox.Length > 0) {
      mbox.Position = mbox.Length - 1;
      var lastByte = mbox.ReadByte();
      mbox.Position = mbox.Length;
      if (lastByte != (byte)'\n')
        mbox.WriteByte((byte)'\n');
    }

    mbox.Write(sepBytes);
    mbox.Write(messageBytes);

    // Ensure the appended record is newline-terminated so a subsequent Append
    // can locate the next "From " separator at a line start.
    if (messageBytes.Length == 0 || messageBytes[^1] != (byte)'\n')
      mbox.WriteByte((byte)'\n');
  }

  /// <summary>
  /// Tombstones a message at <paramref name="messageIndex"/>: rewrites its
  /// in-place byte range as a same-size record carrying an
  /// <c>X-Status: D</c> header and a zero-filled body (so the original bytes
  /// are unrecoverable). Every other message's byte offsets are unchanged —
  /// this is true in-place delete.
  /// </summary>
  /// <returns><c>true</c> if a message at <paramref name="messageIndex"/>
  /// was tombstoned; <c>false</c> if the index is out of range.</returns>
  /// <remarks>
  /// Mailers that honour the <c>X-Status: D</c> convention (mutt, alpine,
  /// procmail) will hide the tombstoned message. Our <see cref="MboxReader"/>
  /// surfaces it verbatim (so the offsets stay stable for any later
  /// modification); use the descriptor's <c>List</c> output filter
  /// downstream if you want deleted messages dropped.
  /// </remarks>
  public static bool TombstoneAt(Stream mbox, int messageIndex) {
    ArgumentNullException.ThrowIfNull(mbox);
    if (!mbox.CanSeek || !mbox.CanRead || !mbox.CanWrite)
      throw new ArgumentException("Mbox stream must be readable, writable, and seekable.", nameof(mbox));
    if (messageIndex < 0) return false;

    var ranges = FindMessageRanges(mbox);
    if (messageIndex >= ranges.Count) return false;

    var (start, end) = ranges[messageIndex];
    var length = end - start;
    if (length <= 0) return false;

    TombstoneRange(mbox, start, length);
    return true;
  }

  /// <summary>
  /// Tombstones the first message whose Subject header matches
  /// <paramref name="subject"/> (ordinal comparison). Returns <c>true</c> on
  /// success.
  /// </summary>
  public static bool TombstoneBySubject(Stream mbox, string subject) {
    ArgumentNullException.ThrowIfNull(mbox);
    ArgumentNullException.ThrowIfNull(subject);
    var ranges = FindMessageRanges(mbox);
    for (var i = 0; i < ranges.Count; i++) {
      var (start, end) = ranges[i];
      if (MessageSubjectEquals(mbox, start, end, subject)) {
        TombstoneRange(mbox, start, end - start);
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Tombstones every message whose Subject header matches any of
  /// <paramref name="subjects"/>. Returns the count of tombstoned messages.
  /// Hits are gathered first then rewritten back-to-front so the byte
  /// offsets we already gathered stay valid as we walk the list.
  /// </summary>
  public static int TombstoneBySubjects(Stream mbox, IReadOnlyCollection<string> subjects) {
    ArgumentNullException.ThrowIfNull(mbox);
    ArgumentNullException.ThrowIfNull(subjects);
    if (subjects.Count == 0) return 0;
    var ranges = FindMessageRanges(mbox);
    var requested = new HashSet<string>(subjects, StringComparer.Ordinal);
    var hitRanges = new List<(long Start, long End)>();
    foreach (var (start, end) in ranges) {
      var subject = ReadSubject(mbox, start, end);
      if (subject != null && requested.Contains(subject))
        hitRanges.Add((start, end));
    }
    for (var i = hitRanges.Count - 1; i >= 0; i--) {
      var (start, end) = hitRanges[i];
      TombstoneRange(mbox, start, end - start);
    }
    return hitRanges.Count;
  }

  // ── Range scanning ────────────────────────────────────────────────────────

  /// <summary>
  /// Walks the mbox stream and returns the (start, end) byte offset of every
  /// message record. The "From " separator at column 0 belongs to the
  /// message that follows it (matching <see cref="MboxReader"/>).
  /// </summary>
  public static IReadOnlyList<(long Start, long End)> FindMessageRanges(Stream mbox) {
    mbox.Position = 0;
    using var ms = new MemoryStream();
    mbox.CopyTo(ms);
    var data = ms.ToArray();

    var starts = new List<long>();
    if (StartsWithFromSpace(data, 0)) starts.Add(0);
    for (var i = 0; i < data.Length - 1; i++) {
      if (data[i] != (byte)'\n') continue;
      var lineStart = i + 1;
      if (StartsWithFromSpace(data, lineStart)) starts.Add(lineStart);
    }
    var result = new List<(long, long)>(starts.Count);
    for (var i = 0; i < starts.Count; i++) {
      var s = starts[i];
      var e = i + 1 < starts.Count ? starts[i + 1] : data.Length;
      result.Add((s, e));
    }
    return result;
  }

  private static bool StartsWithFromSpace(byte[] data, long offset) {
    if (offset + 5 > data.Length) return false;
    return data[offset] == 'F' && data[offset + 1] == 'r' && data[offset + 2] == 'o'
        && data[offset + 3] == 'm' && data[offset + 4] == ' ';
  }

  // ── Tombstone construction ────────────────────────────────────────────────

  /// <summary>
  /// Rewrites the byte range <c>[start, start+length)</c> with a same-size
  /// tombstone record. Layout:
  /// <list type="number">
  ///   <item>The original "From " separator line is preserved verbatim
  ///         (otherwise the stream would have a misaligned record boundary).</item>
  ///   <item>The first message header becomes
  ///         <c>X-Status: D\nX-Cwb-Tombstone: 1\n</c>.</item>
  ///   <item>Subsequent bytes — including any original headers and the entire
  ///         body — are filled with <c>0x00</c> so the original content is
  ///         unrecoverable.</item>
  ///   <item>The very last byte stays <c>'\n'</c> when the original record
  ///         had one, so the "From " separator of the next message remains
  ///         line-aligned.</item>
  /// </list>
  /// </summary>
  private static void TombstoneRange(Stream mbox, long start, long length) {
    // Read the separator line (up to and including the first '\n').
    mbox.Position = start;
    var firstNewline = ScanFirstNewline(mbox, start, length);
    var sepLength = firstNewline - start + 1; // includes '\n'
    if (sepLength <= 0 || sepLength > length) sepLength = Math.Min(length, 1);

    // Build the tombstone payload that follows the separator.
    var marker = "X-Status: D\nX-Cwb-Tombstone: 1\n"u8.ToArray();
    var totalAfterSep = length - sepLength;
    var markerLen = (int)Math.Min(marker.Length, totalAfterSep);

    mbox.Position = start + sepLength;
    if (markerLen > 0) mbox.Write(marker, 0, markerLen);

    var remaining = totalAfterSep - markerLen;
    if (remaining > 0) {
      // Preserve a trailing '\n' (if the original record had one) so the
      // next "From " separator continues to live at a line start.
      var preserveNewline = false;
      mbox.Position = start + length - 1;
      var last = mbox.ReadByte();
      if (last == (byte)'\n') {
        preserveNewline = true;
        remaining--;
      }
      mbox.Position = start + sepLength + markerLen;
      var zeros = new byte[4096];
      while (remaining > 0) {
        var chunk = (int)Math.Min(zeros.Length, remaining);
        mbox.Write(zeros, 0, chunk);
        remaining -= chunk;
      }
      if (preserveNewline) {
        mbox.Position = start + length - 1;
        mbox.WriteByte((byte)'\n');
      }
    }
  }

  private static long ScanFirstNewline(Stream mbox, long start, long length) {
    mbox.Position = start;
    var buf = new byte[(int)Math.Min(length, 4096)];
    var read = mbox.Read(buf, 0, buf.Length);
    for (var i = 0; i < read; i++)
      if (buf[i] == (byte)'\n') return start + i;
    return start + Math.Max(0, length - 1);
  }

  // ── Subject lookup ────────────────────────────────────────────────────────

  private static bool MessageSubjectEquals(Stream mbox, long start, long end, string target) {
    var got = ReadSubject(mbox, start, end);
    return got != null && string.Equals(got, target, StringComparison.Ordinal);
  }

  private static string? ReadSubject(Stream mbox, long start, long end) {
    var length = (int)Math.Min(end - start, 65536); // headers area is bounded
    if (length <= 0) return null;
    mbox.Position = start;
    var buf = new byte[length];
    var read = 0;
    while (read < length) {
      var n = mbox.Read(buf, read, length - read);
      if (n <= 0) break;
      read += n;
    }
    // Find the "From " separator line end.
    var p = 0;
    while (p < read && buf[p] != (byte)'\n') p++;
    p++;

    while (p < read) {
      // End-of-headers: a blank line.
      if (p < read && (buf[p] == (byte)'\n' || (p + 1 < read && buf[p] == (byte)'\r' && buf[p + 1] == (byte)'\n')))
        return null;
      var lineStart = p;
      while (p < read && buf[p] != (byte)'\n') p++;
      var lineEnd = p;
      if (lineEnd > lineStart && buf[lineEnd - 1] == (byte)'\r') lineEnd--;
      var line = Encoding.Latin1.GetString(buf, lineStart, lineEnd - lineStart);
      if (line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
        return line["Subject:".Length..].Trim();
      p++;
    }
    return null;
  }
}
