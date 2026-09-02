#pragma warning disable CS1591
namespace FileFormat.Iceberg;

/// <summary>
/// Represents an iceberg constants.
/// </summary>
public static class IcebergConstants {

  /// <summary>
  /// Defines the format version key constant value.
  /// </summary>
  public const string FormatVersionKey = "format-version";
  /// <summary>
  /// Defines the table uuid key constant value.
  /// </summary>
  public const string TableUuidKey = "table-uuid";
  /// <summary>
  /// Defines the snapshots key constant value.
  /// </summary>
  public const string SnapshotsKey = "snapshots";
  /// <summary>
  /// Defines the location key constant value.
  /// </summary>
  public const string LocationKey = "location";
  /// <summary>
  /// Defines the last updated ms key constant value.
  /// </summary>
  public const string LastUpdatedMsKey = "last-updated-ms";
  /// <summary>
  /// Defines the last column id key constant value.
  /// </summary>
  public const string LastColumnIdKey = "last-column-id";
  /// <summary>
  /// Defines the current schema id key constant value.
  /// </summary>
  public const string CurrentSchemaIdKey = "current-schema-id";
  /// <summary>
  /// Defines the current snapshot id key constant value.
  /// </summary>
  public const string CurrentSnapshotIdKey = "current-snapshot-id";
  /// <summary>
  /// Defines the schemas key constant value.
  /// </summary>
  public const string SchemasKey = "schemas";
  /// <summary>
  /// Defines the schema key constant value.
  /// </summary>
  public const string SchemaKey = "schema";
  /// <summary>
  /// Defines the partition specs key constant value.
  /// </summary>
  public const string PartitionSpecsKey = "partition-specs";
  /// <summary>
  /// Defines the sort orders key constant value.
  /// </summary>
  public const string SortOrdersKey = "sort-orders";
  /// <summary>
  /// Defines the fields key constant value.
  /// </summary>
  public const string FieldsKey = "fields";
  /// <summary>
  /// Defines the schema id key constant value.
  /// </summary>
  public const string SchemaIdKey = "schema-id";
  /// <summary>
  /// Defines the name key constant value.
  /// </summary>
  public const string NameKey = "name";

  /// <summary>
  /// Provides the required keys value.
  /// </summary>
  public static readonly string[] RequiredKeys = [FormatVersionKey, TableUuidKey, SnapshotsKey];
}
