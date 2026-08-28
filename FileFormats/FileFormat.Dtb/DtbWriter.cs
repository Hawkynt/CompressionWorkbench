#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Dtb;

/// <summary>Writer for Flattened Device Tree Blob (FDT v17) images.</summary>
public sealed class DtbWriter {
  internal sealed record PropertySpec(string NodePath, string Name, byte[] Data);

  public static void Write(Stream output, IReadOnlyList<(string Name, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var properties = inputs.Select(i => FromArchiveEntry(i.Name, i.Data)).ToList();
    Write(output, properties, [], 0, addDefaultRootCells: true);
  }

  internal static void Write(Stream output, IReadOnlyList<PropertySpec> properties,
      IReadOnlyList<DtbReader.Reservation> reservations, uint bootCpuidPhys,
      bool addDefaultRootCells = false) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(properties);
    ArgumentNullException.ThrowIfNull(reservations);

    var root = new Node("");
    foreach (var property in properties) {
      var node = root;
      foreach (var segment in property.NodePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        node = node.GetOrAdd(SanitiseNodeName(segment));
      node.Properties.Add(new PropertySpec(property.NodePath,
        SanitisePropertyName(property.Name), property.Data));
    }

    if (addDefaultRootCells) {
      EnsureCellProperty(root, "#address-cells", 2);
      EnsureCellProperty(root, "#size-cells", 2);
    }

    using var strings = new MemoryStream();
    var stringOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
    uint InternName(string name) {
      if (stringOffsets.TryGetValue(name, out var existing)) return existing;
      var offset = checked((uint)strings.Length);
      stringOffsets[name] = offset;
      var bytes = Encoding.ASCII.GetBytes(name);
      strings.Write(bytes);
      strings.WriteByte(0);
      return offset;
    }

    foreach (var node in root.Walk())
      foreach (var property in node.Properties)
        _ = InternName(property.Name);

    using var structBlock = new MemoryStream();
    WriteNode(root, structBlock, InternName);
    WriteToken(structBlock, DtbReader.FDT_END);

    const int headerSize = 40;
    var reservationSize = checked((reservations.Count + 1) * 16);
    var structOffset = checked(headerSize + reservationSize);
    var structSize = checked((uint)structBlock.Length);
    var stringsOffset = checked((uint)(structOffset + structSize));
    var stringsSize = checked((uint)strings.Length);
    var totalSize = checked(stringsOffset + stringsSize);

    Span<byte> header = stackalloc byte[headerSize];
    BinaryPrimitives.WriteUInt32BigEndian(header[0..4], DtbReader.Magic);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..8], totalSize);
    BinaryPrimitives.WriteUInt32BigEndian(header[8..12], (uint)structOffset);
    BinaryPrimitives.WriteUInt32BigEndian(header[12..16], stringsOffset);
    BinaryPrimitives.WriteUInt32BigEndian(header[16..20], headerSize);
    BinaryPrimitives.WriteUInt32BigEndian(header[20..24], 17);
    BinaryPrimitives.WriteUInt32BigEndian(header[24..28], 16);
    BinaryPrimitives.WriteUInt32BigEndian(header[28..32], bootCpuidPhys);
    BinaryPrimitives.WriteUInt32BigEndian(header[32..36], stringsSize);
    BinaryPrimitives.WriteUInt32BigEndian(header[36..40], structSize);
    output.Write(header);

    Span<byte> reservation = stackalloc byte[16];
    foreach (var item in reservations) {
      reservation.Clear();
      BinaryPrimitives.WriteUInt64BigEndian(reservation[..8], item.Address);
      BinaryPrimitives.WriteUInt64BigEndian(reservation[8..], item.Size);
      output.Write(reservation);
    }
    reservation.Clear();
    output.Write(reservation);

    structBlock.Position = 0;
    structBlock.CopyTo(output);
    strings.Position = 0;
    strings.CopyTo(output);
  }

  internal static PropertySpec FromArchiveEntry(string archiveName, byte[] data) {
    var normalized = archiveName.Replace('\\', '/').Trim('/');
    var slash = normalized.LastIndexOf('/');
    var nodePath = slash < 0 ? "/" : "/" + normalized[..slash];
    var leaf = slash < 0 ? normalized : normalized[(slash + 1)..];
    if (leaf.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
        leaf.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
      leaf = leaf[..^4];
    return new PropertySpec(nodePath, SanitisePropertyName(leaf), data);
  }

  public static string SanitisePropertyName(string archiveName) {
    var leaf = archiveName;
    var slash = leaf.LastIndexOfAny(['/', '\\']);
    if (slash >= 0) leaf = leaf[(slash + 1)..];
    if (string.IsNullOrEmpty(leaf)) return "_";

    var sb = new StringBuilder(leaf.Length);
    foreach (var c in leaf) {
      var keep = c is >= '0' and <= '9'
        || c is >= 'a' and <= 'z'
        || c is >= 'A' and <= 'Z'
        || c is ',' or '.' or '_' or '+' or '?' or '#' or '-';
      sb.Append(keep ? c : '_');
    }
    return sb.Length == 0 ? "_" : sb.ToString();
  }

  private static string SanitiseNodeName(string name) {
    if (name.Length == 0) return "_";
    var sb = new StringBuilder(name.Length);
    foreach (var c in name) {
      var keep = c is >= '0' and <= '9'
        || c is >= 'a' and <= 'z'
        || c is >= 'A' and <= 'Z'
        || c is ',' or '.' or '_' or '+' or '?' or '#' or '-' or '@';
      sb.Append(keep ? c : '_');
    }
    return sb.ToString();
  }

  private static void EnsureCellProperty(Node root, string name, uint value) {
    if (root.Properties.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal))) return;
    var data = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(data, value);
    root.Properties.Insert(0, new PropertySpec("/", name, data));
  }

  private static void WriteNode(Node node, Stream output, Func<string, uint> internName) {
    WriteToken(output, DtbReader.FDT_BEGIN_NODE);
    var name = Encoding.ASCII.GetBytes(node.Name);
    output.Write(name);
    output.WriteByte(0);
    Align4(output);

    foreach (var property in node.Properties) {
      WriteToken(output, DtbReader.FDT_PROP);
      Span<byte> header = stackalloc byte[8];
      BinaryPrimitives.WriteUInt32BigEndian(header[..4], checked((uint)property.Data.Length));
      BinaryPrimitives.WriteUInt32BigEndian(header[4..], internName(property.Name));
      output.Write(header);
      output.Write(property.Data);
      Align4(output);
    }

    foreach (var child in node.Children)
      WriteNode(child, output, internName);
    WriteToken(output, DtbReader.FDT_END_NODE);
  }

  private static void WriteToken(Stream output, uint token) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, token);
    output.Write(bytes);
  }

  private static void Align4(Stream output) {
    while ((output.Position & 3) != 0) output.WriteByte(0);
  }

  private sealed class Node(string name) {
    private readonly Dictionary<string, Node> _childrenByName = new(StringComparer.Ordinal);
    public string Name { get; } = name;
    public List<PropertySpec> Properties { get; } = [];
    public List<Node> Children { get; } = [];

    public Node GetOrAdd(string childName) {
      if (_childrenByName.TryGetValue(childName, out var child)) return child;
      child = new Node(childName);
      _childrenByName.Add(childName, child);
      Children.Add(child);
      return child;
    }

    public IEnumerable<Node> Walk() {
      yield return this;
      foreach (var child in Children)
        foreach (var descendant in child.Walk())
          yield return descendant;
    }
  }
}
