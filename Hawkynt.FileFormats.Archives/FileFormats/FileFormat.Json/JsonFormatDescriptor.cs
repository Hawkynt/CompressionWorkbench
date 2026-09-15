#pragma warning disable CS1591
using System.Text;
using System.Text.Json;
using Compression.Registry;
using FileFormat.Structured;

namespace FileFormat.Json;

/// <summary>RFC 8259 JSON exposed as a hierarchy of virtual archive entries.</summary>
public sealed class JsonFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveCreatable {
  private const string BinaryType = "$cwb:type";
  private const string BinaryVersion = "$cwb:version";
  private const string BinaryData = "$cwb:data";

  public string Id => "Json";
  public string DisplayName => "JSON object graph";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".json";
  public IReadOnlyList<string> Extensions => [".json"];
  public IReadOnlyList<string> CompoundExtensions => [".cwb.json"];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("json", "JSON")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "JSON objects become folders; arrays become indexed folders; primitive properties become files.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => StructuredArchive.List(stream, Parse);
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) => StructuredArchive.Extract(stream, outputDir, files, Parse);
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) => StructuredArchive.ExtractEntry(input, entryName, output, Parse);

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
    WriteNode(writer, StructuredArchive.FromInputs(inputs));
    writer.Flush();
  }

  private static StructuredNode Parse(Stream stream) {
    try {
      using var document = JsonDocument.Parse(stream, new JsonDocumentOptions {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 256,
      });
      return ToNode(document.RootElement, 0);
    } catch (JsonException ex) {
      throw new InvalidDataException("Invalid JSON document.", ex);
    }
  }

  private static StructuredNode ToNode(JsonElement element, int depth) {
    if (depth > 256) throw new InvalidDataException("JSON nesting exceeds the 256-level safety limit.");
    switch (element.ValueKind) {
      case JsonValueKind.Object: {
        var properties = element.EnumerateObject().ToArray();
        if (properties.Length == 3
            && properties.Any(p => p.NameEquals(BinaryType) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "binary")
            && properties.Any(p => p.NameEquals(BinaryVersion) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var version) && version == 1)
            && properties.FirstOrDefault(p => p.NameEquals(BinaryData)).Value.ValueKind == JsonValueKind.String) {
          try {
            var encoded = properties.First(p => p.NameEquals(BinaryData)).Value.GetString() ?? "";
            return StructuredNode.Binary(Convert.FromBase64String(encoded), "binary");
          } catch (FormatException ex) {
            throw new InvalidDataException("Invalid CompressionWorkbench JSON binary envelope.", ex);
          }
        }
        var result = StructuredNode.Object("object");
        foreach (var property in properties) result.Add(property.Name, ToNode(property.Value, depth + 1));
        return result;
      }
      case JsonValueKind.Array: {
        var result = StructuredNode.Array("array");
        foreach (var item in element.EnumerateArray()) result.Items.Add(ToNode(item, depth + 1));
        return result;
      }
      case JsonValueKind.String:
        return StructuredNode.Text(StructuredNodeKind.String, element.GetString() ?? "", "string");
      case JsonValueKind.Number:
        return StructuredNode.Text(StructuredNodeKind.Number, element.GetRawText(), "number");
      case JsonValueKind.True:
        return StructuredNode.Text(StructuredNodeKind.Boolean, "true", "boolean");
      case JsonValueKind.False:
        return StructuredNode.Text(StructuredNodeKind.Boolean, "false", "boolean");
      case JsonValueKind.Null:
        return StructuredNode.Null("null");
      default:
        throw new InvalidDataException($"Unsupported JSON value kind {element.ValueKind}.");
    }
  }

  private static void WriteNode(Utf8JsonWriter writer, StructuredNode node) {
    switch (node.Kind) {
      case StructuredNodeKind.Object:
        writer.WriteStartObject();
        foreach (var member in node.Members) {
          writer.WritePropertyName(member.Key);
          WriteNode(writer, member.Value);
        }
        writer.WriteEndObject();
        break;
      case StructuredNodeKind.Array:
        writer.WriteStartArray();
        foreach (var item in node.Items) WriteNode(writer, item);
        writer.WriteEndArray();
        break;
      case StructuredNodeKind.Binary:
        writer.WriteStartObject();
        writer.WriteString(BinaryType, "binary");
        writer.WriteNumber(BinaryVersion, 1);
        writer.WriteBase64String(BinaryData, node.Data);
        writer.WriteEndObject();
        break;
      case StructuredNodeKind.String:
        writer.WriteStringValue(Encoding.UTF8.GetString(node.Data));
        break;
      case StructuredNodeKind.Number:
        writer.WriteRawValue(Encoding.UTF8.GetString(node.Data), skipInputValidation: false);
        break;
      case StructuredNodeKind.Boolean:
        writer.WriteBooleanValue(node.Data.AsSpan().SequenceEqual("true"u8));
        break;
      case StructuredNodeKind.Null:
        writer.WriteNullValue();
        break;
      default:
        writer.WriteStringValue(Encoding.UTF8.GetString(node.Data));
        break;
    }
  }
}
