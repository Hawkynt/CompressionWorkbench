#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Avro;

namespace Compression.Tests.Avro;

[TestFixture]
public class AvroTests {

  private static byte[] EncodeVarLong(long value) {
    using var ms = new MemoryStream();
    AvroVarLong.WriteLong(ms, value);
    return ms.ToArray();
  }

  private static byte[] EncodeBytes(byte[] payload) {
    using var ms = new MemoryStream();
    AvroVarLong.WriteLong(ms, payload.Length);
    ms.Write(payload, 0, payload.Length);
    return ms.ToArray();
  }

  private static byte[] EncodeString(string s) => EncodeBytes(Encoding.UTF8.GetBytes(s));

  private static byte[] BuildOcf(string schema, string codec, byte[] sync,
                                 IEnumerable<(long Count, byte[] Payload, byte[] TrailingSync)> blocks) {
    using var ms = new MemoryStream();
    ms.Write(AvroConstants.Magic, 0, AvroConstants.Magic.Length);

    var entries = new List<(string Key, byte[] Value)>();
    if (schema != null) entries.Add((AvroConstants.MetaKeySchema, Encoding.UTF8.GetBytes(schema)));
    if (codec != null) entries.Add((AvroConstants.MetaKeyCodec, Encoding.UTF8.GetBytes(codec)));

    AvroVarLong.WriteLong(ms, entries.Count);
    foreach (var (k, v) in entries) {
      var keyEncoded = EncodeString(k);
      ms.Write(keyEncoded, 0, keyEncoded.Length);
      var valueEncoded = EncodeBytes(v);
      ms.Write(valueEncoded, 0, valueEncoded.Length);
    }
    AvroVarLong.WriteLong(ms, 0);

    ms.Write(sync, 0, sync.Length);

    foreach (var (count, payload, trailingSync) in blocks) {
      AvroVarLong.WriteLong(ms, count);
      AvroVarLong.WriteLong(ms, payload.Length);
      ms.Write(payload, 0, payload.Length);
      ms.Write(trailingSync, 0, trailingSync.Length);
    }

    return ms.ToArray();
  }

  private static byte[] DefaultSync() => Enumerable.Repeat((byte)0xAB, 16).ToArray();

  [Test, Category("HappyPath")]
  public void VarLong_RoundTripsZero() {
    var bytes = EncodeVarLong(0);
    Assert.That(bytes, Is.EqualTo(new byte[] { 0x00 }));
    using var ms = new MemoryStream(bytes);
    Assert.That(AvroVarLong.ReadLong(ms), Is.EqualTo(0));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void VarLong_RoundTripsPositive() {
    var bytes = EncodeVarLong(1);
    Assert.That(bytes, Is.EqualTo(new byte[] { 0x02 }));
    using var ms = new MemoryStream(bytes);
    Assert.That(AvroVarLong.ReadLong(ms), Is.EqualTo(1));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void VarLong_RoundTripsNegative() {
    var bytes = EncodeVarLong(-1);
    Assert.That(bytes, Is.EqualTo(new byte[] { 0x01 }));
    using var ms = new MemoryStream(bytes);
    Assert.That(AvroVarLong.ReadLong(ms), Is.EqualTo(-1));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void VarLong_LargeValues() {
    const long Value = 0x1234567890L;
    var bytes = EncodeVarLong(Value);
    using var ms = new MemoryStream(bytes);
    Assert.That(AvroVarLong.ReadLong(ms), Is.EqualTo(Value));

    var negBytes = EncodeVarLong(-Value);
    using var ms2 = new MemoryStream(negBytes);
    Assert.That(AvroVarLong.ReadLong(ms2), Is.EqualTo(-Value));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesMinimalOcf() {
    var sync = DefaultSync();
    const string Schema = "{\"type\":\"int\"}";
    var record = new byte[] { 0x02 };
    var ocf = BuildOcf(Schema, "null", sync, [(1, record, sync)]);

    using var ms = new MemoryStream(ocf);
    var r = new AvroReader(ms);
    Assert.That(r.Schema, Is.EqualTo(Schema));
    Assert.That(r.Codec, Is.EqualTo("null"));
    Assert.That(r.SyncMarker, Is.EqualTo(sync));
    Assert.That(r.BlockCount, Is.EqualTo(1));
    Assert.That(r.RecordCount, Is.EqualTo(1L));
    Assert.That(r.ParseStatus, Is.EqualTo("partial"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var garbage = new byte[64];
    Array.Fill(garbage, (byte)0xCC);
    using var ms = new MemoryStream(garbage);
    Assert.Throws<InvalidDataException>(() => _ = new AvroReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_DetectsCorruptSync() {
    var sync = DefaultSync();
    var badSync = Enumerable.Repeat((byte)0x55, 16).ToArray();
    var record = new byte[] { 0x02 };
    var ocf = BuildOcf("{\"type\":\"int\"}", "null", sync, [(1, record, badSync)]);

    using var ms = new MemoryStream(ocf);
    var r = new AvroReader(ms);
    Assert.That(r.ParseStatus, Is.EqualTo("corrupt"));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var sync = DefaultSync();
    var ocf = BuildOcf("{\"type\":\"int\"}", "null", sync, [(1, new byte[] { 0x02 }, sync)]);
    using var ms = new MemoryStream(ocf);

    var entries = new AvroFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.avro"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullAvro_PreservesBytes() {
    var sync = DefaultSync();
    var ocf = BuildOcf("{\"type\":\"int\"}", "null", sync, [(1, new byte[] { 0x02 }, sync)]);
    var tmp = Path.Combine(Path.GetTempPath(), "avro_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(ocf);
      new AvroFormatDescriptor().Extract(ms, tmp, null, ["FULL.avro"]);

      var outPath = Path.Combine(tmp, "FULL.avro");
      Assert.That(File.Exists(outPath), Is.True);
      var written = File.ReadAllBytes(outPath);
      Assert.That(written, Is.EqualTo(ocf));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_Metadata_ContainsSchema() {
    var sync = DefaultSync();
    const string Schema = "{\"type\":\"int\"}";
    var ocf = BuildOcf(Schema, "null", sync, [(1, new byte[] { 0x02 }, sync)]);
    var tmp = Path.Combine(Path.GetTempPath(), "avro_meta_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(ocf);
      new AvroFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);

      var iniPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(iniPath), Is.True);
      var text = File.ReadAllText(iniPath, Encoding.UTF8);
      Assert.That(text, Does.Contain("parse_status = partial"));
      Assert.That(text, Does.Contain("[avro]"));
      Assert.That(text, Does.Contain("codec = null"));
      Assert.That(text, Does.Contain("\\\"type\\\":\\\"int\\\"").Or.Contain(Schema));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new AvroFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo((FormatCapabilities)0));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new AvroFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Avro"));
    Assert.That(d.DisplayName, Is.EqualTo("Apache Avro OCF"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".avro"));
    Assert.That(d.Extensions, Contains.Item(".avro"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(AvroConstants.Magic));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("avro-ocf"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("Avro OCF"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
  }
}
