using System.Buffers.Binary;
using System.Text;
using FileFormat.Woz;

namespace Compression.Tests.Woz;

[TestFixture]
public class WozTests {

  // Minimal WOZ2: 12-byte file header + INFO + TMAP + META + TRKS chunk. The TRK
  // table places track 0 at file-absolute block 4 (offset 2048); the bitstream
  // bytes are written into the full file buffer at that offset.
  private static byte[] BuildSyntheticWoz() {
    using var ms = new MemoryStream();
    // File header.
    ms.Write(Encoding.ASCII.GetBytes("WOZ2"));
    ms.WriteByte(0xFF); ms.WriteByte(0x0A); ms.WriteByte(0x0D); ms.WriteByte(0x0A);
    ms.Write(new byte[4]); // CRC placeholder (not validated)

    WriteChunk(ms, "INFO", BuildInfo());
    var tmap = new byte[160];
    for (var i = 1; i < 160; ++i) tmap[i] = 0xFF;
    WriteChunk(ms, "TMAP", tmap);
    var meta = "title\tDemo Disk\nsubtitle\tTest\n";
    WriteChunk(ms, "META", Encoding.UTF8.GetBytes(meta));

    // TRKS payload: 160 * 8-byte TRK table. Track 0 -> startBlock 4, 1 block,
    // 4096 bits (512 bytes).
    var trks = new byte[160 * 8];
    BinaryPrimitives.WriteUInt16LittleEndian(trks.AsSpan(0, 2), 4);    // startBlock (file offset 2048)
    BinaryPrimitives.WriteUInt16LittleEndian(trks.AsSpan(2, 2), 1);    // blockCount
    BinaryPrimitives.WriteUInt32LittleEndian(trks.AsSpan(4, 4), 4096); // bitCount => 512 bytes
    WriteChunk(ms, "TRKS", trks);

    var buf = ms.ToArray();
    // Append/grow to include the file-absolute bitstream at offset 2048.
    const int trackOffset = 4 * 512;
    var final = new byte[Math.Max(buf.Length, trackOffset + 512)];
    Array.Copy(buf, final, buf.Length);
    for (var i = 0; i < 512; ++i) final[trackOffset + i] = (byte)(i & 0xFF);
    return final;
  }

  private static byte[] BuildInfo() {
    var info = new byte[60];
    info[0] = 2;  // info version
    info[1] = 1;  // disk type 5.25
    info[2] = 0;  // write protected = no
    info[3] = 1;  // synchronized = yes
    return info;
  }

  private static void WriteChunk(MemoryStream ms, string id, byte[] payload) {
    ms.Write(Encoding.ASCII.GetBytes(id));
    Span<byte> sz = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sz, (uint)payload.Length);
    ms.Write(sz);
    ms.Write(payload);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new WozFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Woz"));
    Assert.That(d.Extensions, Contains.Item(".woz"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataMetaAndTracks() {
    var img = BuildSyntheticWoz();
    var d = new WozFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.woz"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "meta.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "tracks/track000.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalMetadataAndTrack() {
    var img = BuildSyntheticWoz();
    var d = new WozFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "woz_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.woz"));
      Assert.That(full, Is.EqualTo(img));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("version=2"));
      Assert.That(meta, Does.Contain("disk_type=5.25"));
      Assert.That(meta, Does.Contain("synchronized=1"));
      Assert.That(meta, Does.Contain("parse_status=ok"));

      var metaIni = File.ReadAllText(Path.Combine(dir, "meta.ini"));
      Assert.That(metaIni, Does.Contain("title=Demo Disk"));

      var track = File.ReadAllBytes(Path.Combine(dir, "tracks", "track000.bin"));
      Assert.That(track.Length, Is.EqualTo(512));
      Assert.That(track[5], Is.EqualTo(5));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[32];
    Array.Fill(garbage, (byte)0x55);
    var d = new WozFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "woz_bad_" + Guid.NewGuid().ToString("N"));
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
