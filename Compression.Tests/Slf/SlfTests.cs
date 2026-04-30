using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Slf;

[TestFixture]
public class SlfTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "JA2 binary blob"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Slf.SlfWriter(ms, leaveOpen: true))
      w.AddEntry("test.bin", data);
    ms.Position = 0;

    var r = new FileFormat.Slf.SlfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.bin"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[256]; Array.Fill(data1, (byte)0xAA);
    var data2 = new byte[128]; Array.Fill(data2, (byte)0xBB);
    var data3 = new byte[ 64]; Array.Fill(data3, (byte)0xCC);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Slf.SlfWriter(ms, leaveOpen: true)) {
      w.AddEntry("sti\\bigicon.sti",     data1);
      w.AddEntry("sti\\smallicon.sti",   data2);
      w.AddEntry("maps\\map001.dat",     data3);
    }
    ms.Position = 0;

    var r = new FileFormat.Slf.SlfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("sti\\bigicon.sti"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("sti\\smallicon.sti"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("maps\\map001.dat"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[64 * 1024];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i * 31);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Slf.SlfWriter(ms, leaveOpen: true))
      w.AddEntry("big.dat", data);
    ms.Position = 0;

    var r = new FileFormat.Slf.SlfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_PreservesLibName() {
    var data = "x"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Slf.SlfWriter(ms, leaveOpen: true, libName: "TestLib", libPath: "TestLib\\"))
      w.AddEntry("a.bin", data);
    ms.Position = 0;

    var r = new FileFormat.Slf.SlfReader(ms);
    Assert.That(r.LibName, Is.EqualTo("TestLib"));
    Assert.That(r.LibPath, Is.EqualTo("TestLib\\"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_SkipsDeletedEntries() {
    // Hand-craft an SLF: 532-byte header claiming 2 entries; the second entry has State=0xFF.
    const int headerSize = 532;
    const int entrySize  = 280;
    const int nameField  = 256;
    var payload0 = "alive"u8.ToArray();
    var payload1 = "dead"u8.ToArray();

    var dataStart = headerSize + 2 * entrySize;
    var totalSize = dataStart + payload0.Length + payload1.Length;
    var buf = new byte[totalSize];

    // Header — leave LibName/LibPath empty, set entry counts.
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(nameField * 2,     4), 2); // NumberOfEntries
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(nameField * 2 + 4, 4), 2); // UsedEntries
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(nameField * 2 + 10, 2), 0x0200); // Version

    // Entry 0 — active.
    var e0 = headerSize;
    Encoding.ASCII.GetBytes("alive.bin").CopyTo(buf, e0);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(e0 + nameField,     4), (uint)dataStart);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(e0 + nameField + 4, 4), (uint)payload0.Length);
    buf[e0 + nameField + 8] = 0x00;

    // Entry 1 — tombstoned.
    var e1 = headerSize + entrySize;
    Encoding.ASCII.GetBytes("dead.bin").CopyTo(buf, e1);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(e1 + nameField,     4), (uint)(dataStart + payload0.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(e1 + nameField + 4, 4), (uint)payload1.Length);
    buf[e1 + nameField + 8] = 0xFF;

    payload0.CopyTo(buf, dataStart);
    payload1.CopyTo(buf, dataStart + payload0.Length);

    using var ms = new MemoryStream(buf);
    var r = new FileFormat.Slf.SlfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("alive.bin"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload0));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsImpossibleEntryCount() {
    var buf = new byte[1024];
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(256 * 2, 4), 10_000_000);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Slf.SlfReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsLongName() {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Slf.SlfWriter(ms, leaveOpen: true);
    var tooLong = new string('x', 256); // 256 ASCII bytes — exceeds 255-byte limit
    Assert.Throws<ArgumentException>(() => w.AddEntry(tooLong, [0]));
  }

  [Test, Category("HappyPath")]
  public void Header_IsExactly532Bytes() {
    var data = "payload"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Slf.SlfWriter(ms, leaveOpen: true))
      w.AddEntry("a.bin", data);

    var bytes = ms.ToArray();
    // Header (532) + 1 entry (280) = 812 → first byte of payload sits at offset 812.
    const int expectedDataOffset = 532 + 280;
    Assert.That(bytes.Length, Is.EqualTo(expectedDataOffset + data.Length));

    // Cross-check the offset stored in the entry record.
    var entryRecordStart = 532;
    var offsetField = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryRecordStart + 256, 4));
    Assert.That(offsetField, Is.EqualTo((uint)expectedDataOffset));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Slf.SlfFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Slf"));
    Assert.That(d.DisplayName, Is.EqualTo("Sir-Tech SLF"));
    Assert.That(d.Extensions, Contains.Item(".slf"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".slf"));
    Assert.That(d.MagicSignatures, Is.Empty);
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("slf"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("SLF"));
  }
}
