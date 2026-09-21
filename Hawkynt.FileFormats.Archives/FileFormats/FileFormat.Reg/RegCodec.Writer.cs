#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Reg;

internal static partial class RegCodec {
  private const string ArchiveRoot = "HKEY_CURRENT_USER\\Software\\CompressionWorkbench\\PseudoArchive";

  /// <summary>
  /// The byte-level shape of a Registry Editor 5.00 export: a UTF-16LE byte-order mark, the
  /// version banner, and CRLF everywhere regardless of the host platform. <c>Environment.NewLine</c>
  /// must not be used here -- a LF-terminated export is not what <c>reg export</c> writes and not
  /// what <c>regedit /s</c> is documented to consume.
  /// </summary>
  public static void Write(Stream output, StructuredNode root) {
    using var writer = new StreamWriter(output, Encoding.Unicode, 4096, leaveOpen: true) { NewLine = "\r\n" };
    writer.WriteLine("Windows Registry Editor Version 5.00");
    writer.WriteLine();
    WriteKey(writer, ArchiveRoot, root);
    writer.Flush();
  }

  private static void WriteKey(StreamWriter writer, string path, StructuredNode node) {
    writer.Write('['); writer.Write(path); writer.WriteLine(']');
    foreach (var member in node.Members.Where(x => x.Value.Kind != StructuredNodeKind.Object))
      WriteValue(writer, member.Key, member.Value.Data);
    writer.WriteLine();
    foreach (var member in node.Members.Where(x => x.Value.Kind == StructuredNodeKind.Object))
      WriteKey(writer, path + "\\" + EscapeKeySegment(member.Key), member.Value);
  }

  // `reg export` breaks a hex payload so that no line -- counting its own trailing backslash --
  // runs past 80 characters, continuing on the next line after two spaces. Expressed as the
  // emitter's loop that is: having just written the separating comma, wrap when two more
  // characters plus the backslash would pass column 79. A value name long enough to leave no
  // room still gets one byte on its first line, which is why the first byte is never wrapped.
  private const int WrapColumn = 79;
  private const string ContinuationIndent = "  ";

  private static void WriteValue(StreamWriter writer, string name, ReadOnlySpan<byte> data) {
    var head = $"\"{EscapeQuoted(name)}\"=hex:";
    writer.Write(head);

    var column = head.Length;
    for (var i = 0; i < data.Length; ++i) {
      if (i != 0) {
        writer.Write(',');
        ++column;
        if (column + 3 > WrapColumn) {
          writer.WriteLine('\\');
          writer.Write(ContinuationIndent);
          column = ContinuationIndent.Length;
        }
      }

      writer.Write(data[i].ToString("x2", CultureInfo.InvariantCulture));
      column += 2;
    }

    writer.WriteLine();
  }

  private static string EscapeQuoted(string value)
    => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

  private static string EscapeKeySegment(string value)
    => value.Replace("%", "%25", StringComparison.Ordinal)
      .Replace("]", "%5D", StringComparison.Ordinal)
      .Replace("\\", "%5C", StringComparison.Ordinal);
}
