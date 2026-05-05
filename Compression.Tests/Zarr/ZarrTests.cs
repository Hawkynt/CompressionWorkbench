using System.Text;
using Compression.Registry;
using FileFormat.Zarr;

namespace Compression.Tests.Zarr;

[TestFixture]
public class ZarrTests {

  private const string MinimalV2Array =
    """
    {
      "zarr_format": 2,
      "shape": [100, 200],
      "chunks": [10, 20],
      "dtype": "<i4",
      "compressor": null,
      "fill_value": 0,
      "order": "C",
      "filters": null
    }
    """;

  private const string V2BloscArray =
    """
    {
      "zarr_format": 2,
      "shape": [50],
      "chunks": [10],
      "dtype": "|u1",
      "compressor": { "id": "blosc", "cname": "lz4", "clevel": 5, "shuffle": 1, "blocksize": 0 },
      "fill_value": 0,
      "order": "C",
      "filters": []
    }
    """;

  private const string MinimalV3Array =
    """
    {
      "zarr_format": 3,
      "node_type": "array",
      "shape": [100, 200],
      "data_type": "int32",
      "chunk_grid": {
        "name": "regular",
        "configuration": { "chunk_shape": [10, 20] }
      },
      "chunk_key_encoding": { "name": "default", "configuration": { "separator": "/" } },
      "fill_value": 0,
      "codecs": [
        { "name": "blosc", "configuration": { "cname": "zstd", "clevel": 5 } }
      ],
      "attributes": {},
      "dimension_names": ["y", "x"]
    }
    """;

  private const string MinimalV3Group =
    """
    {
      "zarr_format": 3,
      "node_type": "group",
      "attributes": {}
    }
    """;

  private static MemoryStream Stream(string json) => new(Encoding.UTF8.GetBytes(json), writable: false);

  [Test, Category("HappyPath")]
  public void Reader_ParsesV2Array() {
    using var ms = Stream(MinimalV2Array);
    var r = new ZarrReader(ms);
    Assert.That(r.ZarrFormat, Is.EqualTo(2));
    Assert.That(r.NodeType, Is.EqualTo("array"));
    Assert.That(r.Shape, Is.EqualTo(new long[] { 100, 200 }));
    Assert.That(r.Chunks, Is.EqualTo(new long[] { 10, 20 }));
    Assert.That(r.DataType, Is.EqualTo("<i4"));
    Assert.That(r.Order, Is.EqualTo("C"));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesV3Array() {
    using var ms = Stream(MinimalV3Array);
    var r = new ZarrReader(ms);
    Assert.That(r.ZarrFormat, Is.EqualTo(3));
    Assert.That(r.NodeType, Is.EqualTo("array"));
    Assert.That(r.Shape, Is.EqualTo(new long[] { 100, 200 }));
    Assert.That(r.Chunks, Is.EqualTo(new long[] { 10, 20 }));
    Assert.That(r.DataType, Is.EqualTo("int32"));
    Assert.That(r.Compressor, Is.EqualTo("blosc"));
    Assert.That(r.CodecsCount, Is.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesV3Group() {
    using var ms = Stream(MinimalV3Group);
    var r = new ZarrReader(ms);
    Assert.That(r.ZarrFormat, Is.EqualTo(3));
    Assert.That(r.NodeType, Is.EqualTo("group"));
    Assert.That(r.Shape, Is.Empty);
    Assert.That(r.Chunks, Is.Empty);
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadJson() {
    using var ms = Stream("{ this is not json");
    Assert.Throws<InvalidDataException>(() => _ = new ZarrReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsMissingZarrFormat() {
    const string json = """{"shape":[10],"chunks":[5],"dtype":"<i4"}""";
    using var ms = Stream(json);
    Assert.Throws<InvalidDataException>(() => _ = new ZarrReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsUnknownVersion() {
    const string json = """{"zarr_format":4,"shape":[10],"chunks":[5],"dtype":"<i4"}""";
    using var ms = Stream(json);
    var ex = Assert.Throws<InvalidDataException>(() => _ = new ZarrReader(ms));
    Assert.That(ex!.Message, Does.Contain("version").IgnoreCase);
  }

  [Test, Category("ErrorHandling")]
  public void Reader_V2RejectsMissingShape() {
    const string json = """{"zarr_format":2,"chunks":[5],"dtype":"<i4"}""";
    using var ms = Stream(json);
    Assert.Throws<InvalidDataException>(() => _ = new ZarrReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsCompressorName_V2Blosc() {
    using var ms = Stream(V2BloscArray);
    var r = new ZarrReader(ms);
    Assert.That(r.Compressor, Is.EqualTo("blosc"));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsCompressorName_V2Null() {
    using var ms = Stream(MinimalV2Array);
    var r = new ZarrReader(ms);
    Assert.That(r.Compressor, Is.EqualTo("null"));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var d = new ZarrFormatDescriptor();
    using var ms = Stream(MinimalV2Array);
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.json"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullJson_PreservesBytes() {
    var d = new ZarrFormatDescriptor();
    var original = Encoding.UTF8.GetBytes(MinimalV2Array);
    using var ms = new MemoryStream(original);
    var dir = Path.Combine(Path.GetTempPath(), "zarr_test_" + Guid.NewGuid().ToString("N"));
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
  public void Extract_Metadata_ContainsShape() {
    var d = new ZarrFormatDescriptor();
    using var ms = Stream(MinimalV2Array);
    var dir = Path.Combine(Path.GetTempPath(), "zarr_test_" + Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(dir);
      d.Extract(ms, dir, null, ["metadata.ini"]);
      var metaPath = Path.Combine(dir, "metadata.ini");
      Assert.That(File.Exists(metaPath), Is.True);
      var text = File.ReadAllText(metaPath);
      Assert.That(text, Does.Contain("shape ="));
      Assert.That(text, Does.Contain("zarr_format = 2"));
      Assert.That(text, Does.Contain("node_type = array"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new ZarrFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That((object)d is IArchiveCreatable, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new ZarrFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Zarr"));
    Assert.That(d.DisplayName, Is.EqualTo("Zarr array metadata"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Extensions, Is.Empty);
    Assert.That(d.MagicSignatures, Is.Empty);
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("zarr"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("Zarr metadata"));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }
}
