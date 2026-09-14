#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.MessagePack;

internal static partial class MessagePackCodec {
  public static void Write(Stream output, StructuredNode root) => WriteNode(output, root);

  private static void WriteNode(Stream output, StructuredNode node) {
    switch (node.Kind) {
      case StructuredNodeKind.Object:
        WriteMapHeader(output, node.Members.Count);
        foreach (var member in node.Members) {
          WriteString(output, member.Key);
          WriteNode(output, member.Value);
        }
        return;
      case StructuredNodeKind.Array:
        WriteArrayHeader(output, node.Items.Count);
        foreach (var item in node.Items) WriteNode(output, item);
        return;
      case StructuredNodeKind.Binary:
        WriteBinary(output, node.Data);
        return;
      case StructuredNodeKind.String:
        WriteString(output, Encoding.UTF8.GetString(node.Data));
        return;
      case StructuredNodeKind.Boolean:
        output.WriteByte(node.Data.AsSpan().SequenceEqual("true"u8) ? (byte)0xc3 : (byte)0xc2);
        return;
      case StructuredNodeKind.Null:
        output.WriteByte(0xc0);
        return;
      default:
        WriteString(output, Encoding.UTF8.GetString(node.Data));
        return;
    }
  }

  private static void WriteString(Stream output, string value) {
    var bytes = Encoding.UTF8.GetBytes(value);
    if (bytes.Length <= 31) output.WriteByte((byte)(0xa0 | bytes.Length));
    else if (bytes.Length <= byte.MaxValue) { output.WriteByte(0xd9); output.WriteByte((byte)bytes.Length); }
    else if (bytes.Length <= ushort.MaxValue) { output.WriteByte(0xda); WriteUInt16(output, (ushort)bytes.Length); }
    else { output.WriteByte(0xdb); WriteUInt32(output, checked((uint)bytes.Length)); }
    output.Write(bytes);
  }

  private static void WriteBinary(Stream output, ReadOnlySpan<byte> bytes) {
    if (bytes.Length <= byte.MaxValue) { output.WriteByte(0xc4); output.WriteByte((byte)bytes.Length); }
    else if (bytes.Length <= ushort.MaxValue) { output.WriteByte(0xc5); WriteUInt16(output, (ushort)bytes.Length); }
    else { output.WriteByte(0xc6); WriteUInt32(output, checked((uint)bytes.Length)); }
    output.Write(bytes);
  }

  private static void WriteArrayHeader(Stream output, int count) {
    if (count <= 15) output.WriteByte((byte)(0x90 | count));
    else if (count <= ushort.MaxValue) { output.WriteByte(0xdc); WriteUInt16(output, (ushort)count); }
    else { output.WriteByte(0xdd); WriteUInt32(output, checked((uint)count)); }
  }

  private static void WriteMapHeader(Stream output, int count) {
    if (count <= 15) output.WriteByte((byte)(0x80 | count));
    else if (count <= ushort.MaxValue) { output.WriteByte(0xde); WriteUInt16(output, (ushort)count); }
    else { output.WriteByte(0xdf); WriteUInt32(output, checked((uint)count)); }
  }

  private static void WriteUInt16(Stream output, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    output.Write(bytes);
  }
}
