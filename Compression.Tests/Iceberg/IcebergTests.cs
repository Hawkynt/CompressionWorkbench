using System.Text;
using Compression.Registry;
using FileFormat.Iceberg;

namespace Compression.Tests.Iceberg;

[TestFixture]
public class IcebergTests {

  private const string MinimalV2 =
    """
    {
      "format-version": 2,
      "table-uuid": "9c12d441-03fe-4693-9a96-a0705ddf69c1",
      "location": "s3://warehouse/db/table",
      "last-updated-ms": 1700000000000,
      "last-column-id": 3,
      "current-schema-id": 0,
      "schemas": [
        {
          "schema-id": 0,
          "type": "struct",
          "fields": [
            { "id": 1, "name": "id", "required": true, "type": "long" },
            { "id": 2, "name": "name", "required": false, "type": "string" },
            { "id": 3, "name": "value", "required": false, "type": "double" }
          ]
        }
      ],
      "partition-specs": [ { "spec-id": 0, "fields": [] } ],
      "default-spec-id": 0,
      "sort-orders": [ { "order-id": 0, "fields": [] } ],
      "default-sort-order-id": 0,
      "current-snapshot-id": -1,
      "snapshots": [],
      "snapshot-log": [],
      "metadata-log": []
    }
    """;

  private const string MinimalV1Inline =
    """
    {
      "format-version": 1,
      "table-uuid": "11111111-2222-3333-4444-555555555555",
      "location": "s3://bucket/old",
      "last-updated-ms": 1600000000000,
      "last-column-id": 2,
      "schema": {
        "type": "struct",
        "fields": [
          { "id": 1, "name": "alpha", "required": true, "type": "int" },
          { "id": 2, "name": "beta", "required": false, "type": "string" }
        ]
      },
      "partition-spec": [],
      "current-snapshot-id": -1,
      "snapshots": []
    }
    """;

  private static MemoryStream Stream(string json) => new(Encoding.UTF8.GetBytes(json), writable: false);

  [Test, Category("HappyPath")]
  public void Reader_ParsesMinimalV2() {
    using var ms = Stream(MinimalV2);
    var r = new IcebergReader(ms);
    Assert.That(r.FormatVersion, Is.EqualTo(2));
    Assert.That(r.TableUuid, Is.EqualTo("9c12d441-03fe-4693-9a96-a0705ddf69c1"));
    Assert.That(r.Location, Is.EqualTo("s3://warehouse/db/table"));
    Assert.That(r.LastUpdatedMs, Is.EqualTo(1700000000000));
    Assert.That(r.CurrentSnapshotId, Is.EqualTo(-1));
    Assert.That(r.SnapshotCount, Is.EqualTo(0));
    Assert.That(r.PartitionSpecCount, Is.EqualTo(1));
    Assert.That(r.SortOrderCount, Is.EqualTo(1));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesV1WithInlineSchema() {
    using var ms = Stream(MinimalV1Inline);
    var r = new IcebergReader(ms);
    Assert.That(r.FormatVersion, Is.EqualTo(1));
    Assert.That(r.TableUuid, Is.EqualTo("11111111-2222-3333-4444-555555555555"));
    Assert.That(r.SchemaColumns, Is.EqualTo(new[] { "alpha", "beta" }));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadJson() {
    using var ms = Stream("{ this is not json");
    Assert.Throws<InvalidDataException>(() => _ = new IcebergReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsMissingFormatVersion() {
    const string json = """{"table-uuid":"abc","snapshots":[]}""";
    using var ms = Stream(json);
    Assert.Throws<InvalidDataException>(() => _ = new IcebergReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsMissingTableUuid() {
    const string json = """{"format-version":2,"snapshots":[]}""";
    using var ms = Stream(json);
    Assert.Throws<InvalidDataException>(() => _ = new IcebergReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesNoSnapshots() {
    using var ms = Stream(MinimalV2);
    var r = new IcebergReader(ms);
    Assert.That(r.SnapshotCount, Is.EqualTo(0));
    Assert.That(r.CurrentSnapshotId, Is.EqualTo(-1));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsSchemaColumns() {
    using var ms = Stream(MinimalV2);
    var r = new IcebergReader(ms);
    Assert.That(r.SchemaColumns, Is.EqualTo(new[] { "id", "name", "value" }));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var d = new IcebergFormatDescriptor();
    using var ms = Stream(MinimalV2);
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.json"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullJson_PreservesBytes() {
    var d = new IcebergFormatDescriptor();
    var original = Encoding.UTF8.GetBytes(MinimalV2);
    using var ms = new MemoryStream(original);
    var dir = Path.Combine(Path.GetTempPath(), "iceberg_test_" + Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(dir);
      d.Extract(ms, dir, null, ["FULL.json"]);
      var fullPath = Path.Combine(dir, "FULL.json");
      Assert.That(File.Exists(fullPath), Is.True);
      var roundTrip = File.ReadAllBytes(fullPath);
      Assert.That(roundTrip, Is.EqualTo(original));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_Metadata_ContainsTableUuid() {
    var d = new IcebergFormatDescriptor();
    using var ms = Stream(MinimalV2);
    var dir = Path.Combine(Path.GetTempPath(), "iceberg_test_" + Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(dir);
      d.Extract(ms, dir, null, ["metadata.ini"]);
      var metaPath = Path.Combine(dir, "metadata.ini");
      Assert.That(File.Exists(metaPath), Is.True);
      var text = File.ReadAllText(metaPath);
      Assert.That(text, Does.Contain("table_uuid = 9c12d441-03fe-4693-9a96-a0705ddf69c1"));
      Assert.That(text, Does.Contain("format_version = 2"));
      Assert.That(text, Does.Contain("schema_columns = id,name,value"));
      Assert.That(text, Does.Contain("parse_status = full"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new IcebergFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That((object)d is IArchiveCreatable, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new IcebergFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Iceberg"));
    Assert.That(d.DisplayName, Is.EqualTo("Apache Iceberg metadata"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Extensions, Is.Empty);
    Assert.That(d.MagicSignatures, Is.Empty);
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("iceberg"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("Iceberg metadata"));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }
}
