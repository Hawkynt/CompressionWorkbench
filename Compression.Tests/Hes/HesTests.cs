#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Hes;

namespace Compression.Tests.Hes;

[TestFixture]
public class HesTests {

  private static byte[] BuildHesWithBlocks() {
    var ms = new MemoryStream();
    var header = new byte[0x10];
    "HESM"u8.CopyTo(header);
    header[0x04] = 0;    // version
    header[0x05] = 1;    // first song
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x06), 0x1AB0); // init addr
    for (var i = 0; i < 8; ++i)
      header[0x08 + i] = (byte)(0xF8 + i); // MPR table
    ms.Write(header);

    WriteDataBlock(ms, 0x2000, [0x10, 0x20, 0x30]);
    WriteDataBlock(ms, 0x4000, [0xAA, 0xBB]);
    return ms.ToArray();
  }

  private static void WriteDataBlock(Stream s, uint loadAddr, byte[] payload) {
    var bh = new byte[0x10];
    "DATA"u8.CopyTo(bh);
    BinaryPrimitives.WriteUInt32LittleEndian(bh.AsSpan(4), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(bh.AsSpan(8), loadAddr);
    s.Write(bh);
    s.Write(payload);
  }

  private static byte[] BuildHesNoBlocks() {
    var blob = new byte[0x10 + 3];
    "HESM"u8.CopyTo(blob);
    blob[0x04] = 0;
    blob[0x05] = 1;
    blob[0x10] = 0x01; blob[0x11] = 0x02; blob[0x12] = 0x03;
    return blob;
  }

  private static string Meta(byte[] blob) => Encoding.UTF8.GetString(Bytes(blob, "metadata.ini"));

  private static byte[] Bytes(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new HesFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }

  [Test]
  public void DataBlocks_SurfacedWithExactBytes() {
    var blob = BuildHesWithBlocks();
    using var ms = new MemoryStream(blob);
    var entries = new HesFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.hes").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "blocks/00_2000.bin").Kind, Is.EqualTo("Stream"));
    Assert.That(Bytes(blob, "blocks/00_2000.bin"), Is.EqualTo(new byte[] { 0x10, 0x20, 0x30 }));
    Assert.That(Bytes(blob, "blocks/01_4000.bin"), Is.EqualTo(new byte[] { 0xAA, 0xBB }));
  }

  [Test]
  public void Metadata_HasHeaderFields() {
    var ini = Meta(BuildHesWithBlocks());
    Assert.That(ini, Does.Contain("first_song=1"));
    Assert.That(ini, Does.Contain("init_addr=0x1AB0"));
    Assert.That(ini, Does.Contain("data_blocks=2"));
    Assert.That(ini, Does.Contain("initial_mpr="));
  }

  [Test]
  public void NoDataBlocks_FallsBackToProgramBin() {
    var blob = BuildHesNoBlocks();
    using var ms = new MemoryStream(blob);
    var entries = new HesFormatDescriptor().List(ms, null);
    Assert.That(Bytes(blob, "program.bin"), Is.EqualTo(new byte[] { 0x01, 0x02, 0x03 }));
    Assert.That(entries.Any(e => e.Name.StartsWith("blocks/")), Is.False);
  }

  [Test]
  public void ShortBlob_DegradesToFullOnly() {
    using var ms = new MemoryStream("HESM"u8.ToArray());
    var entries = new HesFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.hes"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.bin"), Is.False);
  }
}
