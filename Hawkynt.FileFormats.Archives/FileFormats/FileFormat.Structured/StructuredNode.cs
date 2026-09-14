#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Structured;

internal enum StructuredNodeKind { Object, Array, String, Binary, Number, Boolean, Null, Reference, Other }

internal sealed class StructuredNode {
  public StructuredNodeKind Kind { get; init; }
  public string? TypeName { get; init; }
  public byte[] Data { get; init; } = [];
  public List<KeyValuePair<string, StructuredNode>> Members { get; } = [];
  public List<StructuredNode> Items { get; } = [];

  public static StructuredNode Object(string? typeName = null) => new() { Kind = StructuredNodeKind.Object, TypeName = typeName };
  public static StructuredNode Array(string? typeName = null) => new() { Kind = StructuredNodeKind.Array, TypeName = typeName };
  public static StructuredNode Scalar(StructuredNodeKind kind, ReadOnlySpan<byte> data, string? typeName = null)
    => new() { Kind = kind, TypeName = typeName, Data = data.ToArray() };
  public static StructuredNode Text(StructuredNodeKind kind, string value, string? typeName = null)
    => Scalar(kind, Encoding.UTF8.GetBytes(value), typeName);
  public static StructuredNode Binary(ReadOnlySpan<byte> data, string? typeName = null)
    => Scalar(StructuredNodeKind.Binary, data, typeName);
  public static StructuredNode Null(string? typeName = null) => Text(StructuredNodeKind.Null, "null", typeName);

  public StructuredNode Add(string name, StructuredNode value) {
    this.Members.Add(new(name, value));
    return this;
  }
}
