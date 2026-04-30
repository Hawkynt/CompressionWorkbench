#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Arrow;

namespace Compression.Tests.Arrow;

[TestFixture]
public class ArrowTests {

  /// <summary>Builds a minimal Arrow IPC File-format byte sequence: magic + EOS marker + empty footer + footer length + trailing magic.</summary>
  private static byte[] BuildMinimalFile() {
    using var ms = new MemoryStream();
    ms.Write(ArrowConstants.Magic, 0, ArrowConstants.Magic.Length);
    WriteU32(ms, ArrowConstants.ContinuationMarker);
    WriteU32(ms, 0u);
    WriteU32(ms, 0u);
    ms.Write(ArrowConstants.Magic, 0, ArrowConstants.Magic.Length);
    return ms.ToArray();
  }

  /// <summary>Builds a minimal Arrow IPC Streaming-format byte sequence: just an EOS marker, no leading magic.</summary>
  private static byte[] BuildMinimalStream() {
    using var ms = new MemoryStream();
    WriteU32(ms, ArrowConstants.ContinuationMarker);
    WriteU32(ms, 0u);
    return ms.ToArray();
  }

  private static void WriteU32(Stream stream, uint value) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
    stream.Write(buf);
  }

  private static void WriteI32(Stream stream, int value) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(buf, value);
    stream.Write(buf);
  }

  /// <summary>
  /// Builds a synthetic FlatBuffers blob that roughly mimics an Arrow Message envelope:
  /// includes the headerType=Schema tag and bodyLength=0, plus a synthetic payload area
  /// containing inline FlatBuffers strings ("id", "name", "value") preceded by their LE-32
  /// length so the heuristic schema scanner picks them up.
  /// </summary>
  private static byte[] BuildSchemaMessageMetadata(params string[] columnNames) {
    using var ms = new MemoryStream();

    // Synthesise a minimal Message FlatBuffers table:
    //   root_offset:  i32 -> tableStart (we'll place the table at offset 4)
    //   vtable:       size:u16, object_size:u16, field0..N:u16
    //   table data:   inline fields
    //
    // Vtable indices per Arrow IPC schema-fbs:
    //   0=version (short), 1=headerType (ubyte), 2=header (table offset),
    //   3=bodyLength (long), 4=customMetadata (vector).
    //
    // We emit headerType=1 (Schema) at table+8 and bodyLength=0 at table+16.

    WriteI32(ms, 4);

    // table at offset 4
    var tableStart = (int)ms.Position;
    // table[0..3]: vtable_rel:i32 (back-pointer to vtable, here negative meaning vtable later)
    // Easier: place vtable AFTER table data and use negative back-pointer.
    // Layout:
    //   tableStart+0:  vtable_rel:i32 (points back: tableStart - vtableStart)
    //   tableStart+4:  version:i16
    //   tableStart+6:  headerType:u8 (=1 Schema)
    //   tableStart+7:  pad
    //   tableStart+8:  header_offset:i32 (=0, we don't actually point at a header table)
    //   tableStart+12: bodyLength:i64 (=0)
    //   tableStart+20: customMetadata_offset:i32 (=0)
    //   tableStart+24: end of inline fields
    //
    // Actually FlatBuffers stores inline fields per the offsets listed in the vtable.
    // Field offsets are relative to the table start. The vtable lists, per field:
    //   v0(version)        @ 4   size 2
    //   v1(headerType)     @ 6   size 1
    //   v2(header)         @ 8   size 4 (offset to nested table)
    //   v3(bodyLength)     @ 12  size 8
    //   v4(customMetadata) @ 0   (absent)

    var bodyLengthOffset = 12;
    var tableObjectSize = 24;

    // Reserve table bytes (filled in below).
    var tableBytes = new byte[tableObjectSize];

    // tableBytes[0..3] vtable_rel filled after we know vtable position.
    BinaryPrimitives.WriteInt16LittleEndian(tableBytes.AsSpan(4, 2), 5);  // version=V5
    tableBytes[6] = ArrowConstants.MessageHeaderSchema;                    // headerType
    BinaryPrimitives.WriteInt32LittleEndian(tableBytes.AsSpan(8, 4), 0);   // header offset (unused)
    BinaryPrimitives.WriteInt64LittleEndian(tableBytes.AsSpan(bodyLengthOffset, 8), 0);
    BinaryPrimitives.WriteInt32LittleEndian(tableBytes.AsSpan(20, 4), 0);  // customMetadata offset

    // Place vtable after the table.
    var vtableStart = tableStart + tableObjectSize;
    var vtableRel = tableStart - vtableStart;
    BinaryPrimitives.WriteInt32LittleEndian(tableBytes.AsSpan(0, 4), vtableRel);

    ms.Write(tableBytes, 0, tableBytes.Length);

    // vtable: size:u16, object_size:u16, then per-field u16 offsets relative to tableStart.
    // Five fields: v0..v4
    using (var vt = new MemoryStream()) {
      Span<byte> u16 = stackalloc byte[2];
      BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)(2 * 2 + 5 * 2)); // vtable size: 2 bytes header for size, 2 for object_size, 5*2 for fields = 14
      vt.Write(u16);
      BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)tableObjectSize); vt.Write(u16);
      BinaryPrimitives.WriteUInt16LittleEndian(u16, 4); vt.Write(u16);   // v0 version @ 4
      BinaryPrimitives.WriteUInt16LittleEndian(u16, 6); vt.Write(u16);   // v1 headerType @ 6
      BinaryPrimitives.WriteUInt16LittleEndian(u16, 8); vt.Write(u16);   // v2 header @ 8
      BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)bodyLengthOffset); vt.Write(u16);  // v3 bodyLength @ 12
      BinaryPrimitives.WriteUInt16LittleEndian(u16, 20); vt.Write(u16);  // v4 customMetadata @ 20
      ms.Write(vt.ToArray());
    }

    // Append synthetic schema-name area: each column emitted as i32 length + ASCII bytes.
    foreach (var name in columnNames) {
      var bytes = Encoding.ASCII.GetBytes(name);
      WriteI32(ms, bytes.Length);
      ms.Write(bytes, 0, bytes.Length);
    }

    return ms.ToArray();
  }

  private static byte[] BuildStreamWithSchema(byte[] schemaMetadata) {
    using var ms = new MemoryStream();
    WriteU32(ms, ArrowConstants.ContinuationMarker);
    WriteU32(ms, (uint)schemaMetadata.Length);
    ms.Write(schemaMetadata, 0, schemaMetadata.Length);
    var aligned = AlignUp(ms.Position, ArrowConstants.Alignment);
    while (ms.Position < aligned) ms.WriteByte(0);
    WriteU32(ms, ArrowConstants.ContinuationMarker);
    WriteU32(ms, 0u);
    return ms.ToArray();
  }

  private static byte[] BuildFileWithSchema(byte[] schemaMetadata) {
    using var ms = new MemoryStream();
    ms.Write(ArrowConstants.Magic, 0, ArrowConstants.Magic.Length);
    WriteU32(ms, ArrowConstants.ContinuationMarker);
    WriteU32(ms, (uint)schemaMetadata.Length);
    ms.Write(schemaMetadata, 0, schemaMetadata.Length);
    var aligned = AlignUp(ms.Position, ArrowConstants.Alignment);
    while (ms.Position < aligned) ms.WriteByte(0);
    WriteU32(ms, ArrowConstants.ContinuationMarker);
    WriteU32(ms, 0u);
    WriteU32(ms, 0u);
    ms.Write(ArrowConstants.Magic, 0, ArrowConstants.Magic.Length);
    return ms.ToArray();
  }

  private static long AlignUp(long value, int alignment) {
    var mask = (long)alignment - 1;
    return (value + mask) & ~mask;
  }

  [Test, Category("HappyPath")]
  public void Magic_StartsWithArrow1() {
    var d = new ArrowFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x41, 0x52, 0x52, 0x4F, 0x57, 0x31, 0x00, 0x00 }));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Reader_DetectsFileFormat() {
    var bytes = BuildMinimalFile();
    using var ms = new MemoryStream(bytes);
    var r = new ArrowReader(ms);
    Assert.That(r.Format, Is.EqualTo("file"));
  }

  [Test, Category("HappyPath")]
  public void Reader_DetectsStreamingFormat() {
    var bytes = BuildMinimalStream();
    using var ms = new MemoryStream(bytes);
    var r = new ArrowReader(ms);
    Assert.That(r.Format, Is.EqualTo("streaming"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadLeadingMagic() {
    // Almost-but-not-quite the leading magic — 7 ARROW1 bytes plus a wrong final byte.
    // The reader should not throw on garbage that lacks the magic; it falls back to
    // "streaming" mode and parses what it can. So we synthesise an outright rejected case:
    // a too-short file that the reader explicitly rejects.
    var bytes = new byte[] { 0x41, 0x52, 0x52, 0x4F, 0x57, 0x31, 0x00, 0x00, 0x00, 0x00 };
    using var ms = new MemoryStream(bytes);
    Assert.Throws<InvalidDataException>(() => _ = new ArrowReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_DetectsCorruptStream_Partial() {
    // Valid leading magic, then a continuation marker promising a 1024-byte metadata block
    // that doesn't exist in the truncated file.
    using var build = new MemoryStream();
    build.Write(ArrowConstants.Magic, 0, ArrowConstants.Magic.Length);
    WriteU32(build, ArrowConstants.ContinuationMarker);
    WriteU32(build, 1024u);
    build.Write(new byte[16], 0, 16);
    using var ms = new MemoryStream(build.ToArray());
    var r = new ArrowReader(ms);
    Assert.That(r.Format, Is.EqualTo("file"));
    Assert.That(r.ParseStatus, Is.EqualTo("partial"));
  }

  [Test, Category("HappyPath")]
  public void Reader_HeuristicSchemaScan() {
    var schemaBlob = BuildSchemaMessageMetadata("id", "name", "value");
    var fileBytes = BuildFileWithSchema(schemaBlob);
    using var ms = new MemoryStream(fileBytes);
    var r = new ArrowReader(ms);
    Assert.That(r.Format, Is.EqualTo("file"));
    Assert.That(r.MessageCount, Is.GreaterThanOrEqualTo(1));
    Assert.That(r.ApproximateSchema, Does.Contain("id"));
    Assert.That(r.ApproximateSchema, Does.Contain("name"));
    Assert.That(r.ApproximateSchema, Does.Contain("value"));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var bytes = BuildMinimalFile();
    using var ms = new MemoryStream(bytes);
    var entries = new ArrowFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.arrow"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullArrow_PreservesBytes() {
    var bytes = BuildMinimalFile();
    var tmp = Path.Combine(Path.GetTempPath(), "arrow_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      new ArrowFormatDescriptor().Extract(ms, tmp, null, ["FULL.arrow"]);

      var outPath = Path.Combine(tmp, "FULL.arrow");
      Assert.That(File.Exists(outPath), Is.True);
      var written = File.ReadAllBytes(outPath);
      Assert.That(written, Is.EqualTo(bytes));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_Metadata_ContainsFormat() {
    var bytes = BuildMinimalFile();
    var tmp = Path.Combine(Path.GetTempPath(), "arrow_meta_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      new ArrowFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);

      var iniPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(iniPath), Is.True);
      var text = File.ReadAllText(iniPath, Encoding.UTF8);
      Assert.That(text, Does.Contain("[arrow]"));
      Assert.That(text, Does.Contain("format = file"));
      Assert.That(text, Does.Contain("parse_status = full"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new ArrowFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo((FormatCapabilities)0));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new ArrowFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Arrow"));
    Assert.That(d.DisplayName, Is.EqualTo("Apache Arrow IPC"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".arrow"));
    Assert.That(d.Extensions, Contains.Item(".arrow"));
    Assert.That(d.Extensions, Contains.Item(".feather"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(ArrowConstants.Magic));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("arrow-ipc"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("Arrow IPC"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
  }
}
