using System.Buffers.Binary;
using System.Text;
using FileFormat.Ewf;

namespace Compression.Tests.Ewf;

[TestFixture]
public class EwfTests {

  // Build a minimal EWF segment: 13-byte header + one "header" section
  // carrying a fabricated acquisition header + a terminal "done" section.
  private static byte[] BuildEwf(byte[] headerPayload, bool logical = false) {
    const int headerSize = EwfReader.FileHeaderSize;
    const int descSize = EwfReader.SectionDescriptorSize;
    var headerSectionSize = descSize + headerPayload.Length;
    var doneSectionOffset = headerSize + headerSectionSize;
    var total = doneSectionOffset + descSize; // "done" payload empty

    var buf = new byte[total];

    // File header.
    (logical ? EwfReader.LvfSignature : EwfReader.EvfSignature).CopyTo(buf, 0);
    buf[8] = 0x01;                                                          // fields_start
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(9), 1);             // segment 1
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(11), 0x0000);       // fields_end

    // Section 1: "header" — 16-byte type + 8-byte next + 8-byte size + pad + CRC.
    var sec1Offset = headerSize;
    WriteSectionType(buf, sec1Offset, "header");
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(sec1Offset + 16), (ulong)doneSectionOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(sec1Offset + 24), (ulong)headerSectionSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sec1Offset + 72), 0xCAFEBABE); // checksum
    headerPayload.CopyTo(buf.AsSpan(sec1Offset + descSize));

    // Section 2: "done" — terminal; next_offset points to itself per convention.
    WriteSectionType(buf, doneSectionOffset, "done");
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(doneSectionOffset + 16), (ulong)doneSectionOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(doneSectionOffset + 24), descSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(doneSectionOffset + 72), 0xDEADBEEF);

    return buf;
  }

  private static void WriteSectionType(byte[] buf, int offset, string type) {
    var ascii = Encoding.ASCII.GetBytes(type);
    Buffer.BlockCopy(ascii, 0, buf, offset, Math.Min(ascii.Length, 16));
    // Remaining bytes in the 16-byte type field stay zero by default.
  }

  private static byte[] SampleHeader() {
    // Fake "header" text block in the shape libewf emits: version line, key row,
    // value row, tab-separated.
    var text = "1\r\n" +
               "c\tn\te\tnotes\tav\tov\tm\tu\tp\tr\r\n" +
               "CASE123\tSMITH\tExaminer\tNotes\t20060101\t20060102\tMD5\tUnknown\tp\tr\r\n";
    return Encoding.UTF8.GetBytes(text);
  }

  [Test, Category("HappyPath")]
  public void Read_ParsesEvfSignatureAndSections() {
    var data = BuildEwf(SampleHeader());
    var img = EwfReader.Read(data);

    Assert.That(img.IsLogical, Is.False);
    Assert.That(img.SegmentNumber, Is.EqualTo((ushort)1));
    Assert.That(img.Sections, Has.Count.EqualTo(2));
    Assert.That(img.Sections[0].Type, Is.EqualTo("header"));
    Assert.That(img.Sections[1].Type, Is.EqualTo("done"));
    Assert.That(img.Sections[0].Payload.Length, Is.EqualTo(SampleHeader().Length));
  }

  [Test, Category("HappyPath")]
  public void Read_RecognisesLvfSignature() {
    var data = BuildEwf(SampleHeader(), logical: true);
    var img = EwfReader.Read(data);
    Assert.That(img.IsLogical, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_EmitsMetadataAndSectionEntries() {
    var data = BuildEwf(SampleHeader());
    using var ms = new MemoryStream(data);
    var entries = new EwfFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names.Any(n => n.StartsWith("section_00_header", StringComparison.Ordinal)), Is.True);
    Assert.That(names.Any(n => n.StartsWith("section_01_done", StringComparison.Ordinal)), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_ExtractsMetadataIniWithAcquisitionBlock() {
    var data = BuildEwf(SampleHeader());
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new EwfFormatDescriptor().Extract(ms, tmp, null, null);
      var meta = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(meta), Is.True);
      var text = File.ReadAllText(meta);
      Assert.That(text, Does.Contain("[ewf]"));
      Assert.That(text, Does.Contain("section_count = 2"));
      Assert.That(text, Does.Contain("[acquisition]"));
      Assert.That(text, Does.Contain("CASE123"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Read_BadSignature_Throws() {
    var data = new byte[64];
    Encoding.ASCII.GetBytes("NOTEWF!!").CopyTo(data.AsMemory());
    Assert.That(() => EwfReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_TruncatedHeader_Throws() {
    var data = new byte[8]; // signature only, no fields_start/segment/fields_end
    EwfReader.EvfSignature.CopyTo(data, 0);
    Assert.That(() => EwfReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_BadFieldsStart_Throws() {
    var data = BuildEwf(SampleHeader());
    data[8] = 0x02; // corrupt fields_start (must be 0x01)
    Assert.That(() => EwfReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }
}
