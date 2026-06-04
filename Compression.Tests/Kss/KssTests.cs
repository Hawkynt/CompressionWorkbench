#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Kss;

namespace Compression.Tests.Kss;

[TestFixture]
public class KssTests {

  private static byte[] BuildKscc(byte deviceFlags = 0x02) {
    var blob = new byte[0x10 + 4];
    blob[0] = 0x4B; blob[1] = 0x53; blob[2] = 0x43; blob[3] = 0x43; // "KSCC"
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x04), 0x0000); // load
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x06), 0x1234); // dataLen
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), 0x0010); // init
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0A), 0x0013); // play
    blob[0x0C] = 0x00; // start bank
    blob[0x0D] = 0x00; // extra banks
    blob[0x0E] = 0x00; // extra header len
    blob[0x0F] = deviceFlags;
    blob[0x10] = 0xDE; blob[0x11] = 0xAD; blob[0x12] = 0xBE; blob[0x13] = 0xEF;
    return blob;
  }

  private static byte[] BuildKssx() {
    // KSSX with a 0x10-byte extension block; payload follows at 0x20.
    var blob = new byte[0x10 + 0x10 + 2];
    blob[0] = 0x4B; blob[1] = 0x53; blob[2] = 0x53; blob[3] = 0x58; // "KSSX"
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x06), 0x2000); // dataLen
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), 0x0010);
    blob[0x0E] = 0x10; // extra header length → KSSX extension present
    blob[0x0F] = 0x01; // FMPAC
    // extension block at 0x10
    blob[0x10] = 0x04; // extra device flags
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x11), 1); // first song
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x13), 8); // song count
    // payload at 0x20
    blob[0x20] = 0xCA; blob[0x21] = 0xFE;
    return blob;
  }

  private static string Meta(byte[] blob) => Encoding.UTF8.GetString(Bytes(blob, "metadata.ini"));

  private static byte[] Bytes(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new KssFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }

  [Test]
  public void Kscc_SurfacesFullMetadataAndProgram() {
    var blob = BuildKscc();
    using var ms = new MemoryStream(blob);
    var entries = new KssFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.kss").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "program.bin").Kind, Is.EqualTo("Stream"));
    Assert.That(Bytes(blob, "program.bin"), Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
  }

  [Test]
  public void Kscc_MetadataFieldsAndDevices() {
    var ini = Meta(BuildKscc(deviceFlags: 0x02)); // SCC
    Assert.That(ini, Does.Contain("variant=KSCC"));
    Assert.That(ini, Does.Contain("data_len=0x1234"));
    Assert.That(ini, Does.Contain("init_addr=0x0010"));
    Assert.That(ini, Does.Contain("SCC"));
  }

  [Test]
  public void Kssx_ParsesExtensionAndPayloadAfterExtraHeader() {
    var blob = BuildKssx();
    var ini = Meta(blob);
    Assert.That(ini, Does.Contain("variant=KSSX"));
    Assert.That(ini, Does.Contain("first_song=1"));
    Assert.That(ini, Does.Contain("song_count=8"));

    Assert.That(Bytes(blob, "program.bin"), Is.EqualTo(new byte[] { 0xCA, 0xFE }), "payload begins after the extension block");
  }

  [Test]
  public void ShortBlob_DegradesToFullOnly() {
    using var ms = new MemoryStream(new byte[4]);
    var entries = new KssFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.kss"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.bin"), Is.False);
  }
}
