#pragma warning disable CS1591
using System.Text.Json;

namespace FileFormat.Zarr;

/// <summary>
/// Reads Zarr v2 and v3 array metadata and exposes the chunks of the array as archive entries.
/// </summary>
public sealed class ZarrReader {

  /// <summary>
  /// Gets the zarr format.
  /// </summary>
public int ZarrFormat { get; }
  /// <summary>
  /// Gets the node type.
  /// </summary>
public string NodeType { get; }
  /// <summary>
  /// Gets the shape.
  /// </summary>
public IReadOnlyList<long> Shape { get; }
  /// <summary>
  /// Gets the chunks.
  /// </summary>
public IReadOnlyList<long> Chunks { get; }
  /// <summary>
  /// Gets the data type.
  /// </summary>
public string DataType { get; }
  /// <summary>
  /// Gets the compressor.
  /// </summary>
public string Compressor { get; }
  /// <summary>
  /// Gets the filters count.
  /// </summary>
public int FiltersCount { get; }
  /// <summary>
  /// Gets the codecs count.
  /// </summary>
public int CodecsCount { get; }
  /// <summary>
  /// Gets the order.
  /// </summary>
public string Order { get; }
  /// <summary>
  /// Gets the parse status.
  /// </summary>
public string ParseStatus { get; }

  /// <summary>
  /// Initializes a new instance of <see cref="ZarrReader"/>.
  /// </summary>
public ZarrReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);

    JsonDocument doc;
    try {
      doc = JsonDocument.Parse(stream);
    } catch (JsonException ex) {
      throw new InvalidDataException("Zarr metadata is not valid JSON.", ex);
    }

    using (doc) {
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
        throw new InvalidDataException("Zarr metadata root must be a JSON object.");

      if (!root.TryGetProperty(ZarrConstants.ZarrFormatKey, out var versionProp))
        throw new InvalidDataException($"Zarr metadata is missing required key '{ZarrConstants.ZarrFormatKey}'.");
      if (versionProp.ValueKind != JsonValueKind.Number || !versionProp.TryGetInt32(out var version))
        throw new InvalidDataException($"Zarr '{ZarrConstants.ZarrFormatKey}' must be an integer.");
      if (version != 2 && version != 3)
        throw new InvalidDataException($"Unsupported Zarr version {version}; expected 2 or 3.");

      this.ZarrFormat = version;

      if (version == 2) {
        var optionalsMissing = false;

        foreach (var key in ZarrConstants.V2RequiredKeys)
          if (!root.TryGetProperty(key, out _))
            throw new InvalidDataException($"Zarr v2 metadata is missing required key '{key}'.");

        this.NodeType = ZarrConstants.NodeTypeArray;
        this.Shape = ReadLongArray(root, ZarrConstants.ShapeKey, ZarrConstants.ShapeKey);
        this.Chunks = ReadLongArray(root, ZarrConstants.ChunksKey, ZarrConstants.ChunksKey);

        var dtypeProp = root.GetProperty(ZarrConstants.DtypeKey);
        if (dtypeProp.ValueKind != JsonValueKind.String)
          throw new InvalidDataException($"Zarr v2 '{ZarrConstants.DtypeKey}' must be a string.");
        this.DataType = dtypeProp.GetString() ?? string.Empty;

        this.Compressor = ExtractV2CompressorName(root, out var compressorFound);
        if (!compressorFound) optionalsMissing = true;

        this.FiltersCount = TryGetArrayLength(root, ZarrConstants.FiltersKey, out var filtersFound);
        if (!filtersFound) optionalsMissing = true;

        this.CodecsCount = 0;

        this.Order = TryGetString(root, ZarrConstants.OrderKey, out var orderFound);
        if (!orderFound) {
          this.Order = "C";
          optionalsMissing = true;
        }

        this.ParseStatus = optionalsMissing ? "partial" : "full";
      } else {
        var optionalsMissing = false;

        foreach (var key in ZarrConstants.V3RequiredKeys)
          if (!root.TryGetProperty(key, out _))
            throw new InvalidDataException($"Zarr v3 metadata is missing required key '{key}'.");

        var nodeTypeProp = root.GetProperty(ZarrConstants.NodeTypeKey);
        if (nodeTypeProp.ValueKind != JsonValueKind.String)
          throw new InvalidDataException($"Zarr v3 '{ZarrConstants.NodeTypeKey}' must be a string.");
        var nodeType = nodeTypeProp.GetString() ?? string.Empty;
        if (nodeType != ZarrConstants.NodeTypeArray && nodeType != ZarrConstants.NodeTypeGroup)
          throw new InvalidDataException($"Zarr v3 '{ZarrConstants.NodeTypeKey}' must be 'array' or 'group' (got '{nodeType}').");
        this.NodeType = nodeType;

        if (nodeType == ZarrConstants.NodeTypeArray) {
          this.Shape = TryReadLongArray(root, ZarrConstants.ShapeKey, out var shapeFound);
          if (!shapeFound) optionalsMissing = true;

          this.Chunks = ReadV3ChunkShape(root, out var chunksFound);
          if (!chunksFound) optionalsMissing = true;

          this.DataType = TryGetString(root, ZarrConstants.DataTypeKey, out var dtFound);
          if (!dtFound) optionalsMissing = true;

          this.Compressor = ExtractV3CompressorName(root, out var codecCount, out var codecsFound);
          this.CodecsCount = codecCount;
          if (!codecsFound) optionalsMissing = true;
        } else {
          this.Shape = [];
          this.Chunks = [];
          this.DataType = string.Empty;
          this.Compressor = "none";
          this.CodecsCount = 0;
        }

        this.FiltersCount = 0;
        this.Order = string.Empty;

        this.ParseStatus = optionalsMissing ? "partial" : "full";
      }
    }
  }

  private static string ExtractV2CompressorName(JsonElement root, out bool found) {
    if (!root.TryGetProperty(ZarrConstants.CompressorKey, out var compressorProp)) {
      found = false;
      return "none";
    }

    found = true;

    if (compressorProp.ValueKind == JsonValueKind.Null)
      return "null";

    if (compressorProp.ValueKind != JsonValueKind.Object)
      return "none";

    if (compressorProp.TryGetProperty(ZarrConstants.IdKey, out var idProp) && idProp.ValueKind == JsonValueKind.String)
      return idProp.GetString() ?? "none";

    return "none";
  }

  private static string ExtractV3CompressorName(JsonElement root, out int codecCount, out bool found) {
    codecCount = 0;
    if (!root.TryGetProperty(ZarrConstants.CodecsKey, out var codecsProp) || codecsProp.ValueKind != JsonValueKind.Array) {
      found = false;
      return "none";
    }

    found = true;
    codecCount = codecsProp.GetArrayLength();
    if (codecCount == 0)
      return "none";

    foreach (var codec in codecsProp.EnumerateArray()) {
      if (codec.ValueKind != JsonValueKind.Object) continue;
      if (codec.TryGetProperty(ZarrConstants.NameKey, out var nameProp) && nameProp.ValueKind == JsonValueKind.String) {
        var name = nameProp.GetString();
        if (string.IsNullOrEmpty(name)) continue;
        if (IsCompressionCodec(name))
          return name;
      }
    }

    foreach (var codec in codecsProp.EnumerateArray()) {
      if (codec.ValueKind != JsonValueKind.Object) continue;
      if (codec.TryGetProperty(ZarrConstants.NameKey, out var nameProp) && nameProp.ValueKind == JsonValueKind.String) {
        var name = nameProp.GetString();
        if (!string.IsNullOrEmpty(name))
          return name;
      }
    }

    return "none";
  }

  private static bool IsCompressionCodec(string name)
    => name.Equals("blosc", StringComparison.OrdinalIgnoreCase)
       || name.Equals("gzip", StringComparison.OrdinalIgnoreCase)
       || name.Equals("zstd", StringComparison.OrdinalIgnoreCase)
       || name.Equals("lz4", StringComparison.OrdinalIgnoreCase)
       || name.Equals("zlib", StringComparison.OrdinalIgnoreCase)
       || name.Equals("bz2", StringComparison.OrdinalIgnoreCase)
       || name.Equals("lzma", StringComparison.OrdinalIgnoreCase)
       || name.Equals("snappy", StringComparison.OrdinalIgnoreCase);

  private static IReadOnlyList<long> ReadV3ChunkShape(JsonElement root, out bool found) {
    found = false;
    if (!root.TryGetProperty(ZarrConstants.ChunkGridKey, out var grid) || grid.ValueKind != JsonValueKind.Object)
      return [];

    if (!grid.TryGetProperty(ZarrConstants.ConfigurationKey, out var cfg) || cfg.ValueKind != JsonValueKind.Object)
      return [];

    if (!cfg.TryGetProperty(ZarrConstants.ChunkShapeKey, out var shapeProp) || shapeProp.ValueKind != JsonValueKind.Array)
      return [];

    var list = new List<long>(shapeProp.GetArrayLength());
    foreach (var v in shapeProp.EnumerateArray()) {
      if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n))
        return [];
      list.Add(n);
    }

    found = true;
    return list;
  }

  private static IReadOnlyList<long> ReadLongArray(JsonElement obj, string key, string keyForError) {
    var prop = obj.GetProperty(key);
    if (prop.ValueKind != JsonValueKind.Array)
      throw new InvalidDataException($"Zarr '{keyForError}' must be a JSON array.");
    var list = new List<long>(prop.GetArrayLength());
    foreach (var v in prop.EnumerateArray()) {
      if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n))
        throw new InvalidDataException($"Zarr '{keyForError}' must contain integers.");
      list.Add(n);
    }
    return list;
  }

  private static IReadOnlyList<long> TryReadLongArray(JsonElement obj, string key, out bool found) {
    if (!obj.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.Array) {
      found = false;
      return [];
    }
    var list = new List<long>(prop.GetArrayLength());
    foreach (var v in prop.EnumerateArray()) {
      if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n)) {
        found = false;
        return [];
      }
      list.Add(n);
    }
    found = true;
    return list;
  }

  private static string TryGetString(JsonElement obj, string key, out bool found) {
    if (obj.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String) {
      found = true;
      return prop.GetString() ?? string.Empty;
    }
    found = false;
    return string.Empty;
  }

  private static int TryGetArrayLength(JsonElement obj, string key, out bool found) {
    if (obj.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Array) {
      found = true;
      return prop.GetArrayLength();
    }
    found = false;
    return 0;
  }
}
