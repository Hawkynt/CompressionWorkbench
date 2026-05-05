#pragma warning disable CS1591
namespace FileFormat.Iceberg;

public static class IcebergConstants {

  public const string FormatVersionKey = "format-version";
  public const string TableUuidKey = "table-uuid";
  public const string SnapshotsKey = "snapshots";
  public const string LocationKey = "location";
  public const string LastUpdatedMsKey = "last-updated-ms";
  public const string LastColumnIdKey = "last-column-id";
  public const string CurrentSchemaIdKey = "current-schema-id";
  public const string CurrentSnapshotIdKey = "current-snapshot-id";
  public const string SchemasKey = "schemas";
  public const string SchemaKey = "schema";
  public const string PartitionSpecsKey = "partition-specs";
  public const string SortOrdersKey = "sort-orders";
  public const string FieldsKey = "fields";
  public const string SchemaIdKey = "schema-id";
  public const string NameKey = "name";

  public static readonly string[] RequiredKeys = [FormatVersionKey, TableUuidKey, SnapshotsKey];
}
