#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Pickle;

internal static partial class PickleCodec {
  public static void Write(Stream output, StructuredNode root) {
    output.WriteByte(0x80); // PROTO
    output.WriteByte(4);
    WriteNode(output, root);
    output.WriteByte((byte)'.'); // STOP
  }

  private static void WriteNode(Stream output, StructuredNode node) {
    switch (node.Kind) {
      case StructuredNodeKind.Object:
        output.WriteByte((byte)'}');
        if (node.Members.Count == 0) return;
        output.WriteByte((byte)'(');
        foreach (var member in node.Members) {
          WriteUnicode(output, member.Key);
          WriteNode(output, member.Value);
        }
        output.WriteByte((byte)'u');
        return;
      case StructuredNodeKind.Array:
        output.WriteByte((byte)']');
        if (node.Items.Count == 0) return;
        output.WriteByte((byte)'(');
        foreach (var item in node.Items) WriteNode(output, item);
        output.WriteByte((byte)'e');
        return;
      case StructuredNodeKind.Binary:
        WriteBytes(output, node.Data);
        return;
      case StructuredNodeKind.String:
        WriteUnicode(output, Encoding.UTF8.GetString(node.Data));
        return;
      case StructuredNodeKind.Boolean:
        output.WriteByte(node.Data.AsSpan().SequenceEqual("true"u8) ? (byte)0x88 : (byte)0x89);
        return;
      case StructuredNodeKind.Null:
        output.WriteByte((byte)'N');
        return;
      default:
        WriteUnicode(output, Encoding.UTF8.GetString(node.Data));
        return;
    }
  }

  private static void WriteUnicode(Stream output, string value) {
    var bytes = Encoding.UTF8.GetBytes(value);
    if (bytes.Length <= byte.MaxValue) {
      output.WriteByte(0x8c); // SHORT_BINUNICODE
      output.WriteByte((byte)bytes.Length);
    } else {
      output.WriteByte((byte)'X'); // BINUNICODE
      WriteUInt32LE(output, checked((uint)bytes.Length));
    }
    output.Write(bytes);
  }

  private static void WriteBytes(Stream output, ReadOnlySpan<byte> data) {
    if (data.Length <= byte.MaxValue) {
      output.WriteByte((byte)'C');
      output.WriteByte((byte)data.Length);
    } else {
      output.WriteByte((byte)'B');
      WriteUInt32LE(output, checked((uint)data.Length));
    }
    output.Write(data);
  }

  private static void WriteUInt32LE(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }
}
