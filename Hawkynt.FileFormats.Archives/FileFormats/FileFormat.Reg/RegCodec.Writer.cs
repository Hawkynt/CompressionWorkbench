#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Reg;

internal static partial class RegCodec {
  private const string ArchiveRoot = "HKEY_CURRENT_USER\\Software\\CompressionWorkbench\\PseudoArchive";

  public static void Write(Stream output, StructuredNode root) {
    using var writer = new StreamWriter(output, Encoding.Unicode, 4096, leaveOpen: true);
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

  private static void WriteValue(StreamWriter writer, string name, ReadOnlySpan<byte> data) {
    writer.Write('"');
    writer.Write(EscapeQuoted(name));
    writer.Write("\"=hex:");
    for (var i = 0; i < data.Length; ++i) {
      if (i != 0) writer.Write(',');
      writer.Write(data[i].ToString("x2", CultureInfo.InvariantCulture));
      if (i + 1 < data.Length && (i + 1) % 24 == 0) {
        writer.WriteLine(",\\");
        writer.Write("  ");
      }
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
