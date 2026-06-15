using System.Buffers.Binary;
using System.Text;
using FileFormat.Hfe;

namespace Compression.Tests.Hfe;

[TestFixture]
public class HfeTests {

  // Minimal HFE: 512-byte header + track LUT at block 1 (offset 512) + one track
  // bitstream block at block 2 (offset 1024), 256 bytes of pattern.
  private static byte[] BuildSyntheticHfe() {
    var buf = new byte[1024 + 256];
    Encoding.ASCII.GetBytes("HXCPICFE").CopyTo(buf, 0);
    buf[8] = 0;   // format revision
    buf[9] = 1;   // numberOfTracks
    buf[10] = 2;  // numberOfSides
    buf[11] = 0;  // trackEncoding ISOIBM_MFM
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12, 2), 250); // bitRate
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14, 2), 300); // rpm
    buf[16] = 7;  // interface mode
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(18, 2), 1);   // trackListOffset = block 1

    // Track LUT at offset 512: track 0 offset=block 2, length=256.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(512, 2), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(514, 2), 256);

    // Track block at offset 1024.
    for (var i = 0; i < 256; ++i) buf[1024 + i] = (byte)i;
    return buf;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new HfeFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Hfe"));
    Assert.That(d.Extensions, Contains.Item(".hfe"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndTracks() {
    var img = BuildSyntheticHfe();
    var d = new HfeFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.hfe"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "tracks/track000.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalAndTrackData() {
    var img = BuildSyntheticHfe();
    var d = new HfeFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "hfe_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.hfe"));
      Assert.That(full, Is.EqualTo(img));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("tracks=1"));
      Assert.That(meta, Does.Contain("sides=2"));
      Assert.That(meta, Does.Contain("track_encoding=ISOIBM_MFM"));
      Assert.That(meta, Does.Contain("bit_rate_kbps=250"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      var track = File.ReadAllBytes(Path.Combine(dir, "tracks", "track000.bin"));
      Assert.That(track.Length, Is.EqualTo(256));
      Assert.That(track[5], Is.EqualTo(5));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[600];
    Array.Fill(garbage, (byte)0x44);
    var d = new HfeFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "hfe_bad_" + Guid.NewGuid().ToString("N"));
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
