#pragma warning disable CS1591
using System.Text.Json;

namespace FileFormat.Iceberg;

/// <summary>
/// Reads Apache Iceberg table metadata and exposes the manifests and data files it references as archive entries.
/// </summary>
public sealed class IcebergReader {

  /// <summary>
  /// Gets the format version.
  /// </summary>
  public int FormatVersion { get; }
  /// <summary>
  /// Gets the table uuid.
  /// </summary>
  public string TableUuid { get; }
  /// <summary>
  /// Gets the location.
  /// </summary>
  public string Location { get; }
  /// <summary>
  /// Gets the last updated ms.
  /// </summary>
  public long LastUpdatedMs { get; }
  /// <summary>
  /// Gets the last column id.
  /// </summary>
  public int LastColumnId { get; }
  /// <summary>
  /// Gets the current schema id.
  /// </summary>
  public int CurrentSchemaId { get; }
  /// <summary>
  /// Gets the current snapshot id.
  /// </summary>
  public long CurrentSnapshotId { get; }
  /// <summary>
  /// Gets the snapshot count.
  /// </summary>
  public int SnapshotCount { get; }
  /// <summary>
  /// Gets the partition spec count.
  /// </summary>
  public int PartitionSpecCount { get; }
  /// <summary>
  /// Gets the sort order count.
  /// </summary>
  public int SortOrderCount { get; }
  /// <summary>
  /// Gets the schema columns.
  /// </summary>
  public IReadOnlyList<string> SchemaColumns { get; }
  /// <summary>
  /// Gets the parse status.
  /// </summary>
  public string ParseStatus { get; }

  /// <summary>
  /// Initializes a new instance of <see cref="IcebergReader"/>.
  /// </summary>
  public IcebergReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);

    JsonDocument doc;
    try {
      doc = JsonDocument.Parse(stream);
    } catch (JsonException ex) {
      throw new InvalidDataException("Iceberg metadata is not valid JSON.", ex);
    }

    using (doc) {
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
        throw new InvalidDataException("Iceberg metadata root must be a JSON object.");

      foreach (var key in IcebergConstants.RequiredKeys)
        if (!root.TryGetProperty(key, out _))
          throw new InvalidDataException($"Iceberg metadata is missing required key '{key}'.");

      var fvProp = root.GetProperty(IcebergConstants.FormatVersionKey);
      if (fvProp.ValueKind != JsonValueKind.Number || !fvProp.TryGetInt32(out var fv))
        throw new InvalidDataException("Iceberg 'format-version' must be an integer.");
      if (fv != 1 && fv != 2)
        throw new InvalidDataException($"Iceberg 'format-version' must be 1 or 2 (got {fv}).");
      this.FormatVersion = fv;

      var uuidProp = root.GetProperty(IcebergConstants.TableUuidKey);
      if (uuidProp.ValueKind != JsonValueKind.String)
        throw new InvalidDataException("Iceberg 'table-uuid' must be a string.");
      this.TableUuid = uuidProp.GetString() ?? string.Empty;
      if (this.TableUuid.Length == 0)
        throw new InvalidDataException("Iceberg 'table-uuid' must be a non-empty string.");

      var snapshotsProp = root.GetProperty(IcebergConstants.SnapshotsKey);
      if (snapshotsProp.ValueKind != JsonValueKind.Array)
        throw new InvalidDataException("Iceberg 'snapshots' must be an array.");
      this.SnapshotCount = snapshotsProp.GetArrayLength();

      var optionalsMissing = false;

      this.Location = TryGetString(root, IcebergConstants.LocationKey, out var locFound);
      if (!locFound) optionalsMissing = true;

      this.LastUpdatedMs = TryGetInt64(root, IcebergConstants.LastUpdatedMsKey, out var luFound);
      if (!luFound) optionalsMissing = true;

      this.LastColumnId = TryGetInt32(root, IcebergConstants.LastColumnIdKey, out var lcFound);
      if (!lcFound) optionalsMissing = true;

      this.CurrentSchemaId = TryGetInt32(root, IcebergConstants.CurrentSchemaIdKey, out var csFound);
      this.CurrentSnapshotId = TryGetInt64Default(root, IcebergConstants.CurrentSnapshotIdKey, -1L, out _);

      this.PartitionSpecCount = TryGetArrayLength(root, IcebergConstants.PartitionSpecsKey, out var psFound);
      if (!psFound) optionalsMissing = true;

      if (fv >= 2) {
        this.SortOrderCount = TryGetArrayLength(root, IcebergConstants.SortOrdersKey, out var soFound);
        if (!soFound) optionalsMissing = true;
      } else {
        this.SortOrderCount = 0;
      }

      this.SchemaColumns = ExtractSchemaColumns(root, fv, csFound ? this.CurrentSchemaId : (int?)null, out var schemaFound);
      if (!schemaFound) optionalsMissing = true;

      this.ParseStatus = optionalsMissing ? "partial" : "full";
    }
  }

  private static string TryGetString(JsonElement obj, string key, out bool found) {
    if (obj.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String) {
      found = true;
      return prop.GetString() ?? string.Empty;
    }
    found = false;
    return string.Empty;
  }

  private static long TryGetInt64(JsonElement obj, string key, out bool found) {
    if (obj.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var v)) {
      found = true;
      return v;
    }
    found = false;
    return 0L;
  }

  private static long TryGetInt64Default(JsonElement obj, string key, long fallback, out bool found) {
    if (obj.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var v)) {
      found = true;
      return v;
    }
    found = false;
    return fallback;
  }

  private static int TryGetInt32(JsonElement obj, string key, out bool found) {
    if (obj.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v)) {
      found = true;
      return v;
    }
    found = false;
    return 0;
  }

  private static int TryGetArrayLength(JsonElement obj, string key, out bool found) {
    if (obj.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Array) {
      found = true;
      return prop.GetArrayLength();
    }
    found = false;
    return 0;
  }

  private static IReadOnlyList<string> ExtractSchemaColumns(JsonElement root, int formatVersion, int? currentSchemaId, out bool found) {
    JsonElement? selectedSchema = null;

    if (root.TryGetProperty(IcebergConstants.SchemasKey, out var schemasProp) && schemasProp.ValueKind == JsonValueKind.Array) {
      foreach (var schema in schemasProp.EnumerateArray()) {
        if (schema.ValueKind != JsonValueKind.Object) continue;
        if (currentSchemaId.HasValue
            && schema.TryGetProperty(IcebergConstants.SchemaIdKey, out var idProp)
            && idProp.ValueKind == JsonValueKind.Number
            && idProp.TryGetInt32(out var sid)
            && sid == currentSchemaId.Value) {
          selectedSchema = schema;
          break;
        }
      }
      if (selectedSchema is null && schemasProp.GetArrayLength() > 0) {
        var first = schemasProp[0];
        if (first.ValueKind == JsonValueKind.Object)
          selectedSchema = first;
      }
    }

    if (selectedSchema is null
        && root.TryGetProperty(IcebergConstants.SchemaKey, out var inlineSchema)
        && inlineSchema.ValueKind == JsonValueKind.Object) {
      selectedSchema = inlineSchema;
    }

    if (selectedSchema is null) {
      found = false;
      return [];
    }

    if (!selectedSchema.Value.TryGetProperty(IcebergConstants.FieldsKey, out var fieldsProp)
        || fieldsProp.ValueKind != JsonValueKind.Array) {
      found = false;
      return [];
    }

    var columns = new List<string>(fieldsProp.GetArrayLength());
    foreach (var field in fieldsProp.EnumerateArray()) {
      if (field.ValueKind != JsonValueKind.Object) continue;
      if (field.TryGetProperty(IcebergConstants.NameKey, out var nameProp) && nameProp.ValueKind == JsonValueKind.String) {
        var n = nameProp.GetString();
        if (!string.IsNullOrEmpty(n)) columns.Add(n);
      }
    }

    found = true;
    _ = formatVersion;
    return columns;
  }
}
