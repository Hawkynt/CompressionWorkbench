#pragma warning disable CS1591
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Nrbf;

internal sealed partial class NrbfReader {
  private const string ArchiveMarker = NrbfWriter.Marker;
  private readonly byte[] _data;
  private readonly Dictionary<int, StructuredNode> _objects = [];
  private readonly Dictionary<int, ClassMetadata> _metadata = [];
  private readonly Dictionary<int, string> _libraries = [];
  private int _position;
  private int _records;
  private int _rootId;

  private NrbfReader(byte[] data) => this._data = data;

  public static StructuredNode Read(Stream stream) => new NrbfReader(StructuredArchive.ReadAll(stream)).ReadGraph();

  private StructuredNode ReadGraph() {
    if (ReadByte() != 0) throw new InvalidDataException("NRBF stream does not start with SerializedStreamHeader.");
    this._rootId = ReadInt32();
    _ = ReadInt32(); // header id
    var major = ReadInt32();
    var minor = ReadInt32();
    if (major != 1 || minor != 0) throw new InvalidDataException($"Unsupported NRBF version {major}.{minor}.");

    var ended = false;
    while (this._position < this._data.Length) {
      var type = PeekByte();
      if (type == 11) { _ = ReadByte(); ended = true; break; }
      _ = ReadRecordValue(0);
    }
    if (!ended) throw new InvalidDataException("NRBF stream ended without MessageEnd.");
    if (this._position != this._data.Length) throw new InvalidDataException("NRBF stream contains trailing bytes after MessageEnd.");

    if (this._objects.TryGetValue(this._rootId, out var root) && this.TryReadArchiveEnvelope(root, out var archive))
      return archive;

    var graph = StructuredNode.Object("nrbf-graph");
    graph.Add("$root", StructuredNode.Text(StructuredNodeKind.Reference, $"@{this._rootId}", "object-reference"));
    if (this._libraries.Count > 0) {
      var libraries = StructuredNode.Object("libraries");
      foreach (var library in this._libraries.OrderBy(x => x.Key))
        libraries.Add(library.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), StructuredNode.Text(StructuredNodeKind.String, library.Value, "assembly"));
      graph.Add("$libraries", libraries);
    }
    var objects = StructuredNode.Object("objects");
    foreach (var item in this._objects.OrderBy(x => x.Key)) objects.Add($"@{item.Key}", item.Value);
    graph.Add("objects", objects);
    return graph;
  }

  /// <summary>
  /// Projects the archive envelope <see cref="NrbfWriter" /> emits — an <c>object[]</c> holding
  /// the marker string and then, per entry, its path and either a <c>byte[]</c> (a file) or
  /// <c>null</c> (a directory) — as a file tree. BinaryFormatter writes each <c>byte[]</c> after
  /// the array and leaves a MemberReference in its slot, so references are resolved first.
  /// Anything that does not have exactly that shape is not an envelope and stays a plain graph,
  /// so a malformed or look-alike stream is still listed rather than refused.
  /// </summary>
  private bool TryReadArchiveEnvelope(StructuredNode root, out StructuredNode archive) {
    archive = null!;
    if (root.Kind != StructuredNodeKind.Array || root.Items.Count == 0 || (root.Items.Count - 1) % 2 != 0)
      return false;
    if (this.Resolve(root.Items[0]) is not { Kind: StructuredNodeKind.String } marker
        || Encoding.UTF8.GetString(marker.Data) != ArchiveMarker)
      return false;

    var result = StructuredNode.Object("archive");
    for (var i = 1; i < root.Items.Count; i += 2) {
      if (this.Resolve(root.Items[i]) is not { Kind: StructuredNodeKind.String } nameNode)
        return false;
      var content = this.Resolve(root.Items[i + 1]);
      var isDirectory = content is null || content.Kind == StructuredNodeKind.Null;
      if (!isDirectory && content!.Kind != StructuredNodeKind.Binary)
        return false;

      var parts = Encoding.UTF8.GetString(nameNode.Data).Split('/');
      if (parts.Any(x => x.Length == 0 || x is "." or ".."))
        return false;
      var parent = result;
      for (var j = 0; j < parts.Length - 1; ++j) {
        var next = parent.Members.FirstOrDefault(x => x.Key == parts[j]).Value;
        if (next is null) {
          next = StructuredNode.Object("directory");
          parent.Add(parts[j], next);
        }
        if (next.Kind != StructuredNodeKind.Object) return false;
        parent = next;
      }
      if (parent.Members.Any(x => x.Key == parts[^1])) return false;
      parent.Add(parts[^1], isDirectory ? StructuredNode.Object("directory") : StructuredNode.Binary(content!.Data, "file"));
    }

    archive = result;
    return true;
  }

  /// <summary>Follows a MemberReference to the object it names; any other node is itself.</summary>
  private StructuredNode? Resolve(StructuredNode node) {
    if (node.Kind != StructuredNodeKind.Reference) return node;
    var text = Encoding.UTF8.GetString(node.Data);
    return text.StartsWith('@') && int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id)
           && this._objects.TryGetValue(id, out var target)
      ? target
      : null;
  }

  private StructuredNode ReadRecordValue(int depth) {
    if (depth > 256) throw new InvalidDataException("NRBF nesting exceeds the 256-level safety limit.");
    if (++this._records > 2_000_000) throw new InvalidDataException("NRBF stream exceeds the record safety limit.");
    var type = ReadByte();
    if (type == 12) { ReadLibrary(); return ReadRecordValue(depth); }
    return type switch {
      1 => ReadClassWithId(depth),
      4 => ReadClassWithTypes(depth, systemClass: true),
      5 => ReadClassWithTypes(depth, systemClass: false),
      6 => ReadObjectString(),
      7 => ReadBinaryArray(depth),
      8 => ReadPrimitive(ReadByte()),
      9 => StructuredNode.Text(StructuredNodeKind.Reference, $"@{ReadInt32()}", "object-reference"),
      10 => StructuredNode.Null("object-null"),
      15 => ReadSinglePrimitiveArray(),
      16 => ReadSingleRecordArray(depth, "object-array"),
      17 => ReadSingleRecordArray(depth, "string-array"),
      2 or 3 => throw new InvalidDataException("NRBF ClassWithMembers without MemberTypeInfo is not supported safely without out-of-band type metadata."),
      13 or 14 => throw new InvalidDataException("NRBF multi-null record is only valid while reading an array."),
      21 or 22 => throw new InvalidDataException("NRBF remoting method invocation records are intentionally not interpreted."),
      _ => throw new InvalidDataException($"Unsupported NRBF record type {type} at offset {this._position - 1}."),
    };
  }

  private void ReadLibrary() {
    var id = ReadInt32();
    this._libraries[id] = ReadString();
  }

  private StructuredNode ReadObjectString() {
    var id = ReadInt32();
    var node = StructuredNode.Text(StructuredNodeKind.String, ReadString(), "string");
    StoreObject(id, node);
    return node;
  }

  private StructuredNode ReadClassWithTypes(int depth, bool systemClass) {
    var metadata = ReadClassInfo();
    var binaryTypes = ReadMemberTypes(metadata.MemberNames.Length);
    if (!systemClass) _ = ReadInt32(); // library id
    metadata = metadata with { MemberTypes = binaryTypes };
    this._metadata[metadata.ObjectId] = metadata;
    return ReadClassMembers(metadata.ObjectId, metadata, depth);
  }

  private StructuredNode ReadClassWithId(int depth) {
    var objectId = ReadInt32();
    var metadataId = ReadInt32();
    if (!this._metadata.TryGetValue(metadataId, out var metadata))
      throw new InvalidDataException($"NRBF ClassWithId references unknown metadata object {metadataId}.");
    return ReadClassMembers(objectId, metadata, depth);
  }

  private StructuredNode ReadClassMembers(int objectId, ClassMetadata metadata, int depth) {
    var node = StructuredNode.Object(metadata.Name);
    StoreObject(objectId, node);
    for (var i = 0; i < metadata.MemberNames.Length; ++i) {
      var type = metadata.MemberTypes[i];
      var value = type.BinaryType == 0 ? ReadPrimitive(type.PrimitiveType) : ReadRecordValue(depth + 1);
      node.Add(metadata.MemberNames[i], value);
    }
    return node;
  }

  private ClassMetadata ReadClassInfo() {
    var objectId = ReadInt32();
    var name = ReadString();
    var count = ReadCount();
    var names = new string[count];
    for (var i = 0; i < count; ++i) names[i] = ReadString();
    return new(objectId, name, names, []);
  }

  private MemberType[] ReadMemberTypes(int count) {
    var binary = ReadBytes(count).ToArray();
    var result = new MemberType[count];
    for (var i = 0; i < count; ++i) {
      var binaryType = binary[i];
      byte primitive = 0;
      switch (binaryType) {
        case 0: primitive = ReadByte(); break;
        case 3: _ = ReadString(); break;
        case 4: _ = ReadString(); _ = ReadInt32(); break;
        case 7: primitive = ReadByte(); break;
        case 1 or 2 or 5 or 6: break;
        default: throw new InvalidDataException($"Unknown NRBF BinaryTypeEnumeration value {binaryType}.");
      }
      result[i] = new(binaryType, primitive);
    }
    return result;
  }

  private StructuredNode ReadSinglePrimitiveArray() {
    var objectId = ReadInt32();
    // The item-count safety limit is for arrays that become one node per item. A
    // byte[] becomes a single blob, bounded by the stream itself rather than a count.
    var count = ReadCount(max: int.MaxValue);
    var primitive = ReadByte();
    if (primitive == 2) {
      var blob = StructuredNode.Binary(ReadBytes(count), "primitive-array:byte");
      StoreObject(objectId, blob);
      return blob;
    }
    if (count > 1_000_000) throw new InvalidDataException($"NRBF count {count} is outside the supported safety range 0..1000000.");
    var result = StructuredNode.Array($"primitive-array:{PrimitiveName(primitive)}");
    StoreObject(objectId, result);
    for (var i = 0; i < count; ++i) result.Items.Add(ReadPrimitive(primitive));
    return result;
  }

  private StructuredNode ReadSingleRecordArray(int depth, string typeName) {
    var objectId = ReadInt32();
    var count = ReadCount();
    var result = StructuredNode.Array(typeName);
    StoreObject(objectId, result);
    ReadRecordArrayItems(result, count, depth + 1);
    return result;
  }

  private StructuredNode ReadBinaryArray(int depth) {
    var objectId = ReadInt32();
    var arrayType = ReadByte();
    var rank = ReadCount(max: 32);
    var lengths = new int[rank];
    long total = 1;
    for (var i = 0; i < rank; ++i) { lengths[i] = ReadCount(); total = checked(total * lengths[i]); }
    if (arrayType is >= 3 and <= 5) for (var i = 0; i < rank; ++i) _ = ReadInt32();
    var binaryType = ReadByte();
    byte primitive = 0;
    switch (binaryType) {
      case 0: primitive = ReadByte(); break;
      case 3: _ = ReadString(); break;
      case 4: _ = ReadString(); _ = ReadInt32(); break;
      case 7: primitive = ReadByte(); break;
      case 1 or 2 or 5 or 6: break;
      default: throw new InvalidDataException($"Unknown NRBF array BinaryTypeEnumeration value {binaryType}.");
    }
    if (total > 1_000_000) throw new InvalidDataException("NRBF array exceeds the one-million-item safety limit.");
    var result = StructuredNode.Array($"array[{string.Join('x', lengths)}]");
    StoreObject(objectId, result);
    if (binaryType == 0) for (var i = 0; i < total; ++i) result.Items.Add(ReadPrimitive(primitive));
    else ReadRecordArrayItems(result, (int)total, depth + 1);
    return result;
  }

  private void ReadRecordArrayItems(StructuredNode result, int count, int depth) {
    while (result.Items.Count < count) {
      var type = PeekByte();
      if (type == 13) {
        _ = ReadByte();
        var nulls = ReadByte();
        if (nulls == 0 || result.Items.Count + nulls > count) throw new InvalidDataException("Invalid NRBF ObjectNullMultiple256 count.");
        for (var i = 0; i < nulls; ++i) result.Items.Add(StructuredNode.Null("object-null"));
        continue;
      }
      if (type == 14) {
        _ = ReadByte();
        var nulls = ReadCount();
        if (nulls == 0 || result.Items.Count + nulls > count) throw new InvalidDataException("Invalid NRBF ObjectNullMultiple count.");
        for (var i = 0; i < nulls; ++i) result.Items.Add(StructuredNode.Null("object-null"));
        continue;
      }
      result.Items.Add(ReadRecordValue(depth));
    }
  }

  private void StoreObject(int id, StructuredNode node) {
    if (id <= 0) return;
    if (!this._objects.TryAdd(id, node)) throw new InvalidDataException($"Duplicate NRBF object id {id}.");
  }

  private readonly record struct MemberType(byte BinaryType, byte PrimitiveType);
  private sealed record ClassMetadata(int ObjectId, string Name, string[] MemberNames, MemberType[] MemberTypes);
}
