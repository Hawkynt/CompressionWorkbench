using System.Buffers.Binary;
using FileFormat.Td0;

namespace Compression.Tests.Td0;

[TestFixture]
public class Td0Tests {

  // Minimal uncompressed "TD" image: header (no comment) + one track with one
  // raw (encoding 0) sector of 128 bytes, then 0xFF track terminator.
  private static byte[] BuildSyntheticTd0(byte fill) {
    using var ms = new MemoryStream();
    // 12-byte header.
    Span<byte> h = stackalloc byte[12];
    h[0] = (byte)'T'; h[1] = (byte)'D';
    h[2] = 0;    // sequence
    h[3] = 0;    // check sequence
    h[4] = 0x15; // version 21
    h[5] = 0x02; // data rate
    h[6] = 0x02; // drive type
    h[7] = 0x00; // stepping/flags: bit7 clear => no comment
    h[8] = 0x00; // dos flag
    h[9] = 0x02; // sides
    BinaryPrimitives.WriteUInt16LittleEndian(h[10..12], 0x1234); // crc (not validated)
    ms.Write(h);

    // Track header: sectorCount, cyl, head, crc.
    ms.WriteByte(1); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
    // Sector header: cyl, head, sectorNum, sizeCode(0=>128), flags(0), dataCRC.
    ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(1); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
    // Data block: len(u16 LE) incl encoding byte = 129, encoding 0 (raw), 128 bytes.
    Span<byte> dl = stackalloc byte[3];
    BinaryPrimitives.WriteUInt16LittleEndian(dl[..2], 129);
    dl[2] = 0;
    ms.Write(dl);
    var payload = new byte[128];
    Array.Fill(payload, fill);
    ms.Write(payload);
    // Track terminator.
    ms.WriteByte(0xFF);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new Td0FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Td0"));
    Assert.That(d.Extensions, Contains.Item(".td0"));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndSectors() {
    var img = BuildSyntheticTd0(0x5A);
    var d = new Td0FormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.td0"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("tracks/")), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalAndSectorDecoded() {
    var img = BuildSyntheticTd0(0x5A);
    var d = new Td0FormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "td0_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.td0"));
      Assert.That(full, Is.EqualTo(img));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("compression=none"));
      Assert.That(meta, Does.Contain("sides=2"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      var sector = File.ReadAllBytes(Path.Combine(dir, "tracks", "c00_h0_s01.bin"));
      Assert.That(sector.Length, Is.EqualTo(128));
      Assert.That(sector[0], Is.EqualTo(0x5A));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Boundary")]
  public void Advanced_MarksCompressionPartial() {
    var img = BuildSyntheticTd0(0x00);
    img[0] = (byte)'t'; img[1] = (byte)'d'; // advanced LZH variant
    var d = new Td0FormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "td0_adv_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("compression=advanced-lzh"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[64];
    Array.Fill(garbage, (byte)0x33);
    var d = new Td0FormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "td0_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
