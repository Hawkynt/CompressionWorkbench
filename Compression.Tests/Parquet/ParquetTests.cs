#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Parquet;

namespace Compression.Tests.Parquet;

[TestFixture]
public class ParquetTests {

  // Builds a minimal Parquet file with a synthetic Thrift footer holding:
  //   field 1 (i32) version
  //   field 2 (list<struct>) schema, each schema element having field 4 (binary) name
  //   field 3 (i64) num_rows
  //   field 4 (list<struct>) row_groups (size only, structs are empty)
  //   field 6 (binary) created_by
  // Field ids are emitted in ascending order so each header uses a delta of >=1 fitting in 4 bits.
  private static byte[] BuildParquet(int version, long numRows, string[] columnNames, int numRowGroups, string? createdBy) {
    using var footer = new MemoryStream();
    var prevId = 0;

    ThriftCompact.WriteFieldHeader(footer, ParquetConstants.TypeI32, 1, ref prevId);
    ThriftCompact.WriteVarInt(footer, version);

    ThriftCompact.WriteFieldHeader(footer, ParquetConstants.TypeList, 2, ref prevId);
    ThriftCompact.WriteListHeader(footer, columnNames.Length, ParquetConstants.TypeStruct);
    foreach (var name in columnNames) {
      var elemPrev = 0;
      ThriftCompact.WriteFieldHeader(footer, ParquetConstants.TypeBinary, 4, ref elemPrev);
      ThriftCompact.WriteBinary(footer, name);
      ThriftCompact.WriteStop(footer);
    }

    ThriftCompact.WriteFieldHeader(footer, ParquetConstants.TypeI64, 3, ref prevId);
    ThriftCompact.WriteVarLong(footer, numRows);

    ThriftCompact.WriteFieldHeader(footer, ParquetConstants.TypeList, 4, ref prevId);
    ThriftCompact.WriteListHeader(footer, numRowGroups, ParquetConstants.TypeStruct);
    for (var i = 0; i < numRowGroups; i++) ThriftCompact.WriteStop(footer);

    if (createdBy != null) {
      ThriftCompact.WriteFieldHeader(footer, ParquetConstants.TypeBinary, 6, ref prevId);
      ThriftCompact.WriteBinary(footer, createdBy);
    }

    ThriftCompact.WriteStop(footer);

    var footerBytes = footer.ToArray();

    using var ms = new MemoryStream();
    ms.Write(ParquetConstants.Magic, 0, ParquetConstants.Magic.Length);
    ms.Write(footerBytes, 0, footerBytes.Length);
    Span<byte> lenLe = stackalloc byte[4];
    var len = (uint)footerBytes.Length;
    lenLe[0] = (byte)(len & 0xFF);
    lenLe[1] = (byte)((len >> 8) & 0xFF);
    lenLe[2] = (byte)((len >> 16) & 0xFF);
    lenLe[3] = (byte)((len >> 24) & 0xFF);
    ms.Write(lenLe);
    ms.Write(ParquetConstants.Magic, 0, ParquetConstants.Magic.Length);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Magic_LeadingTrailingPAR1() {
    var d = new ParquetFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x50, 0x41, 0x52, 0x31 }));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesMinimalFile() {
    var bytes = BuildParquet(1, 0, ["root"], 0, "test-writer 1.0");
    using var ms = new MemoryStream(bytes);
    var r = new ParquetReader(ms);
    Assert.That(r.Version, Is.EqualTo(1));
    Assert.That(r.NumRows, Is.EqualTo(0L));
    Assert.That(r.Columns, Has.Count.EqualTo(1));
    Assert.That(r.Columns[0], Is.EqualTo("root"));
    Assert.That(r.NumRowGroups, Is.EqualTo(0));
    Assert.That(r.CreatedBy, Is.EqualTo("test-writer 1.0"));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesMultiColumnSchema() {
    var bytes = BuildParquet(2, 42, ["root", "id", "name", "value"], 3, "pyarrow 14.0");
    using var ms = new MemoryStream(bytes);
    var r = new ParquetReader(ms);
    Assert.That(r.Version, Is.EqualTo(2));
    Assert.That(r.NumRows, Is.EqualTo(42L));
    Assert.That(r.Columns, Is.EqualTo(new[] { "root", "id", "name", "value" }));
    Assert.That(r.NumRowGroups, Is.EqualTo(3));
    Assert.That(r.CreatedBy, Is.EqualTo("pyarrow 14.0"));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsMissingLeadingMagic() {
    var bytes = BuildParquet(1, 0, ["root"], 0, null);
    bytes[0] = 0x58;
    bytes[1] = 0x58;
    bytes[2] = 0x58;
    bytes[3] = 0x58;
    using var ms = new MemoryStream(bytes);
    Assert.Throws<InvalidDataException>(() => _ = new ParquetReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsMissingTrailingMagic() {
    var bytes = BuildParquet(1, 0, ["root"], 0, null);
    var n = bytes.Length;
    bytes[n - 4] = 0x58;
    bytes[n - 3] = 0x58;
    bytes[n - 2] = 0x58;
    bytes[n - 1] = 0x58;
    using var ms = new MemoryStream(bytes);
    Assert.Throws<InvalidDataException>(() => _ = new ParquetReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_DetectsCorruptThrift_PartialStatus() {
    // Build a file with valid PAR1 framing but garbage bytes inside the footer.
    var garbageFooter = new byte[64];
    Array.Fill(garbageFooter, (byte)0xCC);

    using var ms = new MemoryStream();
    ms.Write(ParquetConstants.Magic, 0, ParquetConstants.Magic.Length);
    ms.Write(garbageFooter, 0, garbageFooter.Length);
    Span<byte> lenLe = stackalloc byte[4];
    var len = (uint)garbageFooter.Length;
    lenLe[0] = (byte)(len & 0xFF);
    lenLe[1] = (byte)((len >> 8) & 0xFF);
    lenLe[2] = (byte)((len >> 16) & 0xFF);
    lenLe[3] = (byte)((len >> 24) & 0xFF);
    ms.Write(lenLe);
    ms.Write(ParquetConstants.Magic, 0, ParquetConstants.Magic.Length);
    ms.Position = 0;

    var r = new ParquetReader(ms);
    Assert.That(r.ParseStatus, Is.EqualTo("partial"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ThriftCompact_VarLongRoundTrip() {
    long[] values = [0, 1, -1, 100, -100, 0x1234567890L, -0x1234567890L, long.MaxValue, long.MinValue];
    foreach (var v in values) {
      using var ms = new MemoryStream();
      ThriftCompact.WriteVarLong(ms, v);
      ms.Position = 0;
      var actual = ThriftCompact.ReadVarLong(ms);
      Assert.That(actual, Is.EqualTo(v), $"Round-trip failed for {v}");
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ThriftCompact_BinaryRoundTrip() {
    var samples = new[] {
      string.Empty,
      "hello",
      new string('A', 1024),
    };
    foreach (var s in samples) {
      using var ms = new MemoryStream();
      ThriftCompact.WriteBinary(ms, s);
      ms.Position = 0;
      var actual = ThriftCompact.ReadBinary(ms);
      Assert.That(actual, Is.EqualTo(s));
    }
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var bytes = BuildParquet(1, 0, ["root"], 0, null);
    using var ms = new MemoryStream(bytes);
    var entries = new ParquetFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.parquet"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullParquet_PreservesBytes() {
    var bytes = BuildParquet(1, 0, ["root"], 0, null);
    var tmp = Path.Combine(Path.GetTempPath(), "parquet_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      new ParquetFormatDescriptor().Extract(ms, tmp, null, ["FULL.parquet"]);
      var outPath = Path.Combine(tmp, "FULL.parquet");
      Assert.That(File.Exists(outPath), Is.True);
      var written = File.ReadAllBytes(outPath);
      Assert.That(written, Is.EqualTo(bytes));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_Metadata_ContainsParseStatus() {
    var bytes = BuildParquet(2, 7, ["root", "col1"], 1, "unit-test 0.1");
    var tmp = Path.Combine(Path.GetTempPath(), "parquet_meta_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      new ParquetFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);
      var iniPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(iniPath), Is.True);
      var text = File.ReadAllText(iniPath, Encoding.UTF8);
      Assert.That(text, Does.Contain("[parquet]"));
      Assert.That(text, Does.Contain("parse_status = full"));
      Assert.That(text, Does.Contain("version = 2"));
      Assert.That(text, Does.Contain("num_rows = 7"));
      Assert.That(text, Does.Contain("num_row_groups = 1"));
      Assert.That(text, Does.Contain("num_columns = 2"));
      Assert.That(text, Does.Contain("schema = root;col1"));
      Assert.That(text, Does.Contain("created_by = unit-test 0.1"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new ParquetFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo((FormatCapabilities)0));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new ParquetFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Parquet"));
    Assert.That(d.DisplayName, Is.EqualTo("Apache Parquet"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".parquet"));
    Assert.That(d.Extensions, Contains.Item(".parquet"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(ParquetConstants.Magic));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("parquet"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("Parquet"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
  }
}
