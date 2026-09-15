#pragma warning disable CS1591
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Compression.Registry;
using FileFormat.Structured;

namespace FileFormat.Xml;

/// <summary>XML 1.0 exposed as a hierarchy of virtual archive entries without resolving DTDs or external entities.</summary>
public sealed class XmlFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveCreatable {
  private static readonly XNamespace Cwb = "urn:hawkynt:compressionworkbench:structured-archive:1";

  public string Id => "Xml";
  public string DisplayName => "XML object graph";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".xml";
  public IReadOnlyList<string> Extensions => [".xml"];
  public IReadOnlyList<string> CompoundExtensions => [".cwb.xml"];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("<?xml"u8.ToArray(), Confidence: 0.55)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xml", "XML")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "XML elements become folders/files, attributes become @-prefixed files, and repeated elements remain distinct.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => StructuredArchive.List(stream, Parse);
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) => StructuredArchive.Extract(stream, outputDir, files, Parse);
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) => StructuredArchive.ExtractEntry(input, entryName, output, Parse);

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var writer = XmlWriter.Create(output, new XmlWriterSettings {
      Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false,
      OmitXmlDeclaration = false,
    });
    writer.WriteStartDocument();
    writer.WriteStartElement("cwb", "archive", Cwb.NamespaceName);
    writer.WriteAttributeString("version", "1");
    foreach (var member in StructuredArchive.FromInputs(inputs).Members) WriteMember(writer, member.Key, member.Value);
    writer.WriteEndElement();
    writer.WriteEndDocument();
  }

  private static StructuredNode Parse(Stream stream) {
    try {
      using var reader = XmlReader.Create(stream, new XmlReaderSettings {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = 256L * 1024 * 1024,
        CloseInput = false,
      });
      var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
      var root = document.Root ?? throw new InvalidDataException("XML document has no root element.");
      if (root.Name == Cwb + "archive" && root.Attribute("version")?.Value == "1")
        return ParseCwbArchive(root);
      var result = StructuredNode.Object("xml-document");
      result.Add(root.Name.ToString(), ConvertElement(root, 0));
      return result;
    } catch (XmlException ex) {
      throw new InvalidDataException("Invalid or unsafe XML document.", ex);
    }
  }

  private static StructuredNode ParseCwbArchive(XElement root) {
    var result = StructuredNode.Object("archive");
    foreach (var element in root.Elements()) ParseCwbMember(result, element, 0);
    return result;
  }

  private static void ParseCwbMember(StructuredNode parent, XElement element, int depth) {
    if (depth > 256) throw new InvalidDataException("XML nesting exceeds the 256-level safety limit.");
    var name = element.Attribute("name")?.Value ?? throw new InvalidDataException("CWB XML entry has no name.");
    if (element.Name == Cwb + "directory") {
      var directory = StructuredNode.Object("directory");
      foreach (var child in element.Elements()) ParseCwbMember(directory, child, depth + 1);
      parent.Add(name, directory);
      return;
    }
    if (element.Name != Cwb + "file") throw new InvalidDataException($"Unknown CWB XML entry '{element.Name}'.");
    if (element.Attribute("encoding")?.Value != "base64") throw new InvalidDataException("CWB XML file is not base64 encoded.");
    try { parent.Add(name, StructuredNode.Binary(Convert.FromBase64String(element.Value), "binary")); }
    catch (FormatException ex) { throw new InvalidDataException("Invalid base64 payload in CWB XML file.", ex); }
  }

  private static StructuredNode ConvertElement(XElement element, int depth) {
    if (depth > 256) throw new InvalidDataException("XML nesting exceeds the 256-level safety limit.");
    var children = element.Nodes().ToArray();
    if (!element.HasAttributes && children.All(node => node is XText))
      return StructuredNode.Text(StructuredNodeKind.String, string.Concat(children.Select(TextValue)), "text");

    var result = StructuredNode.Object("element");
    foreach (var attribute in element.Attributes())
      result.Add("@" + attribute.Name, StructuredNode.Text(StructuredNodeKind.String, attribute.Value, "attribute"));

    var textOrdinal = 0;
    foreach (var node in children) {
      switch (node) {
        case XElement child:
          result.Add(child.Name.ToString(), ConvertElement(child, depth + 1));
          break;
        case XCData cdata:
          result.Add(textOrdinal++ == 0 ? "#cdata" : $"#cdata-{textOrdinal - 1:D6}", StructuredNode.Text(StructuredNodeKind.String, cdata.Value, "cdata"));
          break;
        case XText text when !string.IsNullOrWhiteSpace(text.Value):
          result.Add(textOrdinal++ == 0 ? "#text" : $"#text-{textOrdinal - 1:D6}", StructuredNode.Text(StructuredNodeKind.String, text.Value, "text"));
          break;
        case XComment comment:
          result.Add("#comment", StructuredNode.Text(StructuredNodeKind.String, comment.Value, "comment"));
          break;
        case XProcessingInstruction pi:
          result.Add("#pi-" + pi.Target, StructuredNode.Text(StructuredNodeKind.String, pi.Data, "processing-instruction"));
          break;
      }
    }
    return result;
  }

  private static string TextValue(XNode node) => node switch { XCData cdata => cdata.Value, XText text => text.Value, _ => "" };

  private static void WriteMember(XmlWriter writer, string name, StructuredNode node) {
    if (node.Kind == StructuredNodeKind.Object) {
      writer.WriteStartElement("cwb", "directory", Cwb.NamespaceName);
      writer.WriteAttributeString("name", name);
      foreach (var member in node.Members) WriteMember(writer, member.Key, member.Value);
      writer.WriteEndElement();
      return;
    }
    writer.WriteStartElement("cwb", "file", Cwb.NamespaceName);
    writer.WriteAttributeString("name", name);
    writer.WriteAttributeString("encoding", "base64");
    writer.WriteBase64(node.Data, 0, node.Data.Length);
    writer.WriteEndElement();
  }
}
