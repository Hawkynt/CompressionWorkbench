#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Shar;

/// <summary>
/// Random-access in-place modifier for shell-archive (.shar) files. Shar
/// is a plain-text shell script with a trailing <c>exit 0</c> sentinel —
/// the textbook "append before the terminator" shape. Add seeks to the
/// last <c>exit 0</c> line, overwrites it with a new <c>echo x - name</c>
/// block (heredoc for text, uudecode for binary), and re-writes a fresh
/// <c>exit 0</c> sentinel.
/// </summary>
/// <remarks>
/// The pre-existing bytes before the old <c>exit 0</c> line are byte-identical
/// after the operation: we never touch the script preamble or any prior entry
/// block. Remove is not implemented in-place — Shar's variable-length text
/// records do not lend themselves to a fast safe compactor for the
/// heredoc-with-arbitrary-content case, so callers should rebuild via
/// <see cref="SharReader"/>/<see cref="SharWriter"/>.
/// </remarks>
public static class SharInPlaceModifier {

  private static readonly byte[] ExitMarker = "exit 0\n"u8.ToArray();

  /// <summary>
  /// Appends a file entry to a shar archive. The byte range before the
  /// existing <c>exit 0</c> sentinel is not modified.
  /// </summary>
  public static void AddFile(Stream shar, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(shar);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var exitOffset = FindExitMarkerOffset(shar);
    shar.Position = exitOffset;

    // Emit a new entry block exactly as SharWriter does.
    using (var sw = new StreamWriter(shar, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" }) {
      SharWriter.WriteEntry(sw, name, data);
      sw.WriteLine("exit 0");
      sw.Flush();
    }
    shar.SetLength(shar.Position);
  }

  /// <summary>
  /// Returns the byte offset of the trailing <c>exit 0\n</c> sentinel.
  /// Scans the tail of the stream backwards so very large archives don't
  /// need to be re-buffered in full.
  /// </summary>
  private static long FindExitMarkerOffset(Stream shar) {
    // Scan the trailing region for the most recent "exit 0\n" line —
    // walk the last 64 KiB which is more than enough for any plausible
    // shar trailer (the writer emits a single line, the agent's own
    // marker is 7 bytes).
    var tailLen = (int)Math.Min(shar.Length, 65536);
    if (tailLen <= 0) return shar.Length;
    var tailStart = shar.Length - tailLen;
    var tail = new byte[tailLen];
    shar.Position = tailStart;
    var read = 0;
    while (read < tailLen) {
      var n = shar.Read(tail, read, tailLen - read);
      if (n <= 0) break;
      read += n;
    }

    // We look for "\nexit 0\n" (preceding newline anchors the start of the line).
    var pattern = new byte[ExitMarker.Length + 1];
    pattern[0] = (byte)'\n';
    Array.Copy(ExitMarker, 0, pattern, 1, ExitMarker.Length);

    var match = LastIndexOf(tail.AsSpan(0, read), pattern);
    if (match >= 0)
      return tailStart + match + 1; // +1 so the leading "\n" of the previous line is preserved

    // Tolerate "exit 0\n" at offset 0 (degenerate shar with no preamble).
    var bare = LastIndexOf(tail.AsSpan(0, read), ExitMarker);
    if (bare >= 0)
      return tailStart + bare;

    // No sentinel found — append at EOF and the new entry will become the trailer.
    return shar.Length;
  }

  private static int LastIndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    if (needle.Length == 0 || needle.Length > haystack.Length) return -1;
    for (var i = haystack.Length - needle.Length; i >= 0; --i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j) {
        if (haystack[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return i;
    }
    return -1;
  }
}
