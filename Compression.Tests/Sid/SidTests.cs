#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Sid;

namespace Compression.Tests.Sid;

[TestFixture]
public class SidTests {

  // Builds a PSID/RSID header (big-endian). v2 adds flags + page bytes (header = 0x7C).
  private static byte[] BuildSid(string magic, ushort version, ushort flags = 0, ushort loadAddr = 0x1000, byte[]? program = null) {
    var headerSize = version >= 2 ? 0x7C : 0x76;
    program ??= [0x55, 0x66, 0x77];
    var blob = new byte[headerSize + program.Length];
    Encoding.ASCII.GetBytes(magic).CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x04), version);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x06), (ushort)headerSize); // dataOffset
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x08), loadAddr);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0A), 0x1003); // init
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0C), 0x1006); // play
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0E), 1);      // songs
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x10), 1);      // start song
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(0x12), 0);      // speed
    WriteText(blob, 0x16, "Commando", 32);
    WriteText(blob, 0x36, "Rob Hubbard", 32);
    WriteText(blob, 0x56, "1985 Elite", 32);
    if (version >= 2)
      BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x76), flags);
    program.CopyTo(blob, headerSize);
    return blob;
  }

  private static void WriteText(byte[] b, int off, string t, int len) {
    var bytes = Encoding.ASCII.GetBytes(t);
    Array.Copy(bytes, 0, b, off, Math.Min(bytes.Length, len - 1));
  }

  private static string Meta(byte[] blob) => Encoding.UTF8.GetString(Bytes(blob, "metadata.ini"));

  private static byte[] Bytes(byte[] blob, string entry) {
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new SidFormatDescriptor().ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }

  [Test]
  public void Psid_SurfacesFullMetadataAndProgram() {
    var blob = BuildSid("PSID", 1);
    using var ms = new MemoryStream(blob);
    var entries = new SidFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.sid").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "program.bin").Kind, Is.EqualTo("Stream"));
    Assert.That(Bytes(blob, "program.bin"), Is.EqualTo(new byte[] { 0x55, 0x66, 0x77 }));
  }

  [Test]
  public void Psid_MetadataFields() {
    var ini = Meta(BuildSid("PSID", 1));
    Assert.That(ini, Does.Contain("magic=PSID"));
    Assert.That(ini, Does.Contain("name=Commando"));
    Assert.That(ini, Does.Contain("author=Rob Hubbard"));
    Assert.That(ini, Does.Contain("released=1985 Elite"));
    Assert.That(ini, Does.Contain("load_addr=0x1000"));
  }

  [Test]
  public void Rsid_MagicRecognised() {
    var ini = Meta(BuildSid("RSID", 2, flags: 0x0024)); // clock=PAL(1<<2=0x04 → bits2-3=1), model=8580(2<<4=0x20)
    Assert.That(ini, Does.Contain("magic=RSID"));
    Assert.That(ini, Does.Contain("clock=PAL"));
    Assert.That(ini, Does.Contain("sid_model=MOS8580"));
  }

  [Test]
  public void LoadAddrZero_ReadsRealLoadFromProgram() {
    // loadAddr==0 → first two LE program bytes are the real load address (0x2000).
    var program = new byte[] { 0x00, 0x20, 0xAB, 0xCD };
    var ini = Meta(BuildSid("PSID", 2, loadAddr: 0, program: program));
    Assert.That(ini, Does.Contain("real_load_addr=0x2000"));
  }

  [Test]
  public void ShortBlob_DegradesToFullOnly() {
    using var ms = new MemoryStream("PSID"u8.ToArray());
    var entries = new SidFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.sid"), Is.True);
    Assert.That(entries.Any(e => e.Name == "program.bin"), Is.False);
  }
}
