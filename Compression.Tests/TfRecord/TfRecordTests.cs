namespace Compression.Tests.TfRecord;

[TestFixture]
public class TfRecordTests {

  [Test, Category("HappyPath")]
  public void Crc32C_KnownVector() {
    // Castagnoli well-known test vector: CRC-32C("123456789") == 0xE3069283.
    // Locks the polynomial choice — IEEE CRC-32 ("123456789") would be 0xCBF43926, a different value.
    var crc = FileFormat.TfRecord.Crc32C.Compute("123456789"u8);
    Assert.That(crc, Is.EqualTo(0xE3069283u));
  }

  [Test, Category("HappyPath")]
  public void MaskRoundTrip() {
    Assert.That(FileFormat.TfRecord.Crc32C.Mask(0u), Is.EqualTo(0xa282ead8u));
    // Regression: Mask(Mask(0)) is a fixed value computed once and locked here so any future
    // refactor of the rotate-and-add transform can't silently change behavior.
    Assert.That(FileFormat.TfRecord.Crc32C.Mask(0xa282ead8u), Is.EqualTo(0x78342FDDu));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleRecord() {
    var data = "hello"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.TfRecord.TfRecordWriter(ms, leaveOpen: true))
      w.AddRecord(data);
    ms.Position = 0;

    var r = new FileFormat.TfRecord.TfRecordReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("record_00000.bin"));
    Assert.That(r.Entries[0].IsCorrupt, Is.False);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleRecords() {
    var data1 = "first"u8.ToArray();
    var data2 = new byte[100];
    Array.Fill(data2, (byte)0x5A);
    var data3 = "third record with more text"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileFormat.TfRecord.TfRecordWriter(ms, leaveOpen: true)) {
      w.AddRecord(data1);
      w.AddRecord(data2);
      w.AddRecord(data3);
    }
    ms.Position = 0;

    var r = new FileFormat.TfRecord.TfRecordReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("record_00000.bin"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("record_00001.bin"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("record_00002.bin"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeRecord() {
    var data = new byte[64 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 31);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.TfRecord.TfRecordWriter(ms, leaveOpen: true))
      w.AddRecord(data);
    ms.Position = 0;

    var r = new FileFormat.TfRecord.TfRecordReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_EmptyRecord() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.TfRecord.TfRecordWriter(ms, leaveOpen: true))
      w.AddRecord([]);
    ms.Position = 0;

    var r = new FileFormat.TfRecord.TfRecordReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
    Assert.That(r.Entries[0].IsCorrupt, Is.False);
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("ErrorHandling")]
  public void Reader_DetectsCorruptLength() {
    // Build a valid two-record stream, then corrupt the SECOND record's length-CRC.
    // First record must remain valid so the reader doesn't throw at the file-start sanity check.
    using var ms = new MemoryStream();
    using (var w = new FileFormat.TfRecord.TfRecordWriter(ms, leaveOpen: true)) {
      w.AddRecord("first"u8.ToArray());
      w.AddRecord("second"u8.ToArray());
    }
    var bytes = ms.ToArray();

    // Layout: [len(8) lenCrc(4) data(5) dataCrc(4)] [len(8) lenCrc(4) data(6) dataCrc(4)]
    // Second record's length-CRC starts at offset 8+4+5+4+8 = 29.
    bytes[29] ^= 0xFF;

    using var corrupt = new MemoryStream(bytes);
    var r = new FileFormat.TfRecord.TfRecordReader(corrupt);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].IsCorrupt, Is.False);
    Assert.That(r.Entries[1].IsCorrupt, Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void Reader_DetectsCorruptData() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.TfRecord.TfRecordWriter(ms, leaveOpen: true))
      w.AddRecord("payload"u8.ToArray());
    var bytes = ms.ToArray();

    // Flip a bit inside the data payload (offset = length(8) + lenCrc(4) = 12).
    bytes[12] ^= 0x01;

    using var corrupt = new MemoryStream(bytes);
    var r = new FileFormat.TfRecord.TfRecordReader(corrupt);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].IsCorrupt, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.TfRecord.TfRecordFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("TfRecord"));
    Assert.That(d.DisplayName, Is.EqualTo("TensorFlow TFRecord"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".tfrecord"));
    Assert.That(d.Extensions, Contains.Item(".tfrecord"));
    Assert.That(d.Extensions, Contains.Item(".tfrecords"));
    Assert.That(d.MagicSignatures, Is.Empty);
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("tfrecord"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("TFRecord"));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
  }
}
