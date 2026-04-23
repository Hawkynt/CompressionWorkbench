using System.Buffers.Binary;
using System.Text;
using FileFormat.Vhdx;

namespace Compression.Tests.Vhdx;

[TestFixture]
public class VhdxTests {

  // Build the first 320 KiB of a VHDX: File Type Identifier + two headers + two region tables.
  // That's enough surface area for the reader's structural walk without us having to
  // fabricate the (optional) log + metadata + BAT regions beyond them.
  private static byte[] BuildVhdx(string creator = "CompressionWorkbench 1.0") {
    const int totalSize = 5 * VhdxReader.RegionSize; // 320 KiB
    var buf = new byte[totalSize];

    // File Type Identifier: "vhdxfile" + UTF-16LE creator.
    VhdxReader.FileSignature.CopyTo(buf, 0);
    var creatorBytes = Encoding.Unicode.GetBytes(creator);
    Buffer.BlockCopy(creatorBytes, 0, buf, 8, Math.Min(creatorBytes.Length, 1024));

    // Header 1.
    WriteHeader(buf, VhdxReader.Header1Offset, sequence: 1);
    // Header 2 (different sequence so the reader can distinguish active copy).
    WriteHeader(buf, VhdxReader.Header2Offset, sequence: 2);

    // Region tables — just the "regi" signature; the reader currently only
    // surfaces the raw region bytes as entries so no parsing is expected.
    VhdxReader.RegionTableSignature.CopyTo(buf, VhdxReader.RegionTable1Offset);
    VhdxReader.RegionTableSignature.CopyTo(buf, VhdxReader.RegionTable2Offset);
    return buf;
  }

  private static void WriteHeader(byte[] buf, int offset, ulong sequence) {
    VhdxReader.HeaderSignature.CopyTo(buf, offset);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 4), 0xDEADBEEF);       // checksum
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(offset + 8), sequence);
    var fileWrite = Guid.Parse("11111111-1111-1111-1111-111111111111").ToByteArray();
    var dataWrite = Guid.Parse("22222222-2222-2222-2222-222222222222").ToByteArray();
    var logGuid = Guid.Parse("33333333-3333-3333-3333-333333333333").ToByteArray();
    fileWrite.CopyTo(buf.AsSpan(offset + 16));
    dataWrite.CopyTo(buf.AsSpan(offset + 32));
    logGuid.CopyTo(buf.AsSpan(offset + 48));
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset + 64), 0);               // log version
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset + 66), 1);               // version
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 68), 1048576);         // log length
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(offset + 72), 0x100000);        // log offset (1 MiB)
  }

  [Test, Category("HappyPath")]
  public void Read_ParsesFileIdentifierAndBothHeaders() {
    var data = BuildVhdx(creator: "qemu-img 9.0.0");
    var img = VhdxReader.Read(data);

    Assert.That(img.Creator, Is.EqualTo("qemu-img 9.0.0"));
    Assert.That(img.FileTypeIdentifier, Has.Length.EqualTo(VhdxReader.RegionSize));
    Assert.That(img.HeaderPrimary, Has.Length.EqualTo(VhdxReader.RegionSize));
    Assert.That(img.HeaderBackup, Has.Length.EqualTo(VhdxReader.RegionSize));
    Assert.That(img.PrimaryHeaderInfo, Is.Not.Null);
    Assert.That(img.BackupHeaderInfo, Is.Not.Null);
    Assert.That(img.PrimaryHeaderInfo!.SequenceNumber, Is.EqualTo(1ul));
    Assert.That(img.BackupHeaderInfo!.SequenceNumber, Is.EqualTo(2ul));
    Assert.That(img.PrimaryHeaderInfo.Version, Is.EqualTo((ushort)1));
    Assert.That(img.PrimaryHeaderInfo.LogOffset, Is.EqualTo(0x100000ul));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_EmitsExpectedEntries() {
    var data = BuildVhdx();
    using var ms = new MemoryStream(data);
    var entries = new VhdxFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("file_type_identifier.bin"));
    Assert.That(names, Does.Contain("header_primary.bin"));
    Assert.That(names, Does.Contain("header_backup.bin"));
    Assert.That(names, Does.Contain("region_table_primary.bin"));
    Assert.That(names, Does.Contain("region_table_backup.bin"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesHeaderPrimaryToDisk() {
    var data = BuildVhdx();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new VhdxFormatDescriptor().Extract(ms, tmp, null, null);
      var headerFile = Path.Combine(tmp, "header_primary.bin");
      Assert.That(File.Exists(headerFile), Is.True);
      Assert.That(new FileInfo(headerFile).Length, Is.EqualTo(VhdxReader.RegionSize));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Read_BadSignature_Throws() {
    var data = new byte[VhdxReader.Header1Offset + VhdxReader.RegionSize];
    Encoding.ASCII.GetBytes("notvhdx!").CopyTo(data.AsMemory());
    Assert.That(() => VhdxReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_TruncatedBelowHeader1_Throws() {
    var data = new byte[1024]; // way under the 128 KiB minimum
    VhdxReader.FileSignature.CopyTo(data, 0);
    Assert.That(() => VhdxReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }
}
