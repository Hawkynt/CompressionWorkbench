using System.Buffers.Binary;
using System.Text;
using FileSystem.ZxScl;

namespace Compression.Tests.ZxScl;

[TestFixture]
public class ZxSclTests {

  /// <summary>
  /// Builds a minimal valid SCL archive holding one BASIC file with <paramref name="payload"/>
  /// padded to 1 x 256-byte sector.
  /// </summary>
  private static byte[] BuildMinimalScl(string baseName, byte[] payload, char type = 'B') {
    if (baseName.Length > 8) throw new ArgumentException("baseName max 8 chars.");

    using var ms = new MemoryStream();

    // Magic.
    ms.Write(ZxSclReader.Magic);

    // File count.
    ms.WriteByte(0x01);

    // 14-byte header.
    var nameBytes = new byte[8];
    for (var i = 0; i < baseName.Length; i++) nameBytes[i] = (byte)baseName[i];
    for (var i = baseName.Length; i < 8; i++) nameBytes[i] = 0x20;
    ms.Write(nameBytes);              // 0..7 name
    ms.WriteByte((byte)type);          // 8 type
    var p1 = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(p1, 10);
    ms.Write(p1);                     // 9..10 param1
    var p2 = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(p2, (ushort)payload.Length);
    ms.Write(p2);                     // 11..12 param2
    ms.WriteByte(0x01);                // 13 length in sectors

    // Payload sector (256 bytes).
    var sector = new byte[256];
    Buffer.BlockCopy(payload, 0, sector, 0, Math.Min(payload.Length, 256));
    ms.Write(sector);

    // 4-byte CRC placeholder (not validated by reader).
    ms.Write(new byte[4]);

    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Reader_ListsSingleBasicFile() {
    var payload = "10 PRINT \"HELLO\""u8.ToArray();
    var scl = BuildMinimalScl("HELLO", payload, 'B');

    using var r = new ZxSclReader(new MemoryStream(scl));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO.bas"));
    Assert.That(r.Entries[0].FileType, Is.EqualTo('B'));
    Assert.That(r.Entries[0].Size, Is.EqualTo(256));
    Assert.That(r.Entries[0].LengthSectors, Is.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsFileContent() {
    var payload = Encoding.ASCII.GetBytes("ZX SPECTRUM LIVES");
    var scl = BuildMinimalScl("PAYLOAD", payload, 'C');

    using var r = new ZxSclReader(new MemoryStream(scl));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted.Length, Is.EqualTo(256));
    Assert.That(extracted.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new ZxSclFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("ZxScl"));
    Assert.That(d.Extensions, Does.Contain(".scl"));
    Assert.That(d.MagicSignatures, Is.Not.Empty);
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(ZxSclReader.Magic));
    Assert.That(d.MaxTotalArchiveSize, Is.EqualTo(ZxSclReader.MaxPayloadSize));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var scl = BuildMinimalScl("TEST", "data"u8.ToArray());
    var d = new ZxSclFormatDescriptor();
    var entries = d.List(new MemoryStream(scl), null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("TEST.bas"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_MissingMagic_Throws() {
    var bad = new byte[256];  // zeros, no magic
    Assert.Throws<InvalidDataException>(() =>
      _ = new ZxSclReader(new MemoryStream(bad)));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    Assert.Throws<InvalidDataException>(() =>
      _ = new ZxSclReader(new MemoryStream(new byte[4])));
  }
}
