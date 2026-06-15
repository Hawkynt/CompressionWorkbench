using System.Text;
using FileFormat.Imd;

namespace Compression.Tests.Imd;

[TestFixture]
public class ImdTests {

  // Minimal IMD: header line + comment + 0x1A, then one track with two sectors:
  // sector 1 normal (type 1, 128 bytes of 0xAA), sector 2 compressed (type 2, fill).
  private static byte[] BuildSyntheticImd() {
    using var ms = new MemoryStream();
    var header = "IMD 1.18: 01/01/2020 12:00:00\r\n";
    ms.Write(Encoding.ASCII.GetBytes(header));
    ms.Write(Encoding.ASCII.GetBytes("synthetic comment"));
    ms.WriteByte(0x1A); // EOF terminator

    // Track: mode, cyl, head, sectorCount, sizeCode(0=>128).
    ms.WriteByte(5); // mode 5 (500 kbps MFM, informational)
    ms.WriteByte(0); // cyl
    ms.WriteByte(0); // head (no maps)
    ms.WriteByte(2); // 2 sectors
    ms.WriteByte(0); // size code 0 => 128
    // Sector numbering map.
    ms.WriteByte(1); ms.WriteByte(2);
    // Sector 1: type 1 (normal) + 128 bytes.
    ms.WriteByte(1);
    var sec1 = new byte[128];
    Array.Fill(sec1, (byte)0xAA);
    ms.Write(sec1);
    // Sector 2: type 2 (compressed) + 1 fill byte 0xBB.
    ms.WriteByte(2);
    ms.WriteByte(0xBB);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new ImdFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Imd"));
    Assert.That(d.Extensions, Contains.Item(".imd"));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndSectors() {
    var img = BuildSyntheticImd();
    var d = new ImdFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.imd"));
    Assert.That(entries.Count(e => e.Name.StartsWith("tracks/")), Is.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalAndSectorsDecoded() {
    var img = BuildSyntheticImd();
    var d = new ImdFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "imd_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.imd"));
      Assert.That(full, Is.EqualTo(img));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("track_count=1"));
      Assert.That(meta, Does.Contain("comment=synthetic comment"));
      Assert.That(meta, Does.Contain("parse_status=ok"));

      var s1 = File.ReadAllBytes(Path.Combine(dir, "tracks", "c00_h0_s01.bin"));
      Assert.That(s1.Length, Is.EqualTo(128));
      Assert.That(s1[0], Is.EqualTo(0xAA));
      var s2 = File.ReadAllBytes(Path.Combine(dir, "tracks", "c00_h0_s02.bin"));
      Assert.That(s2.Length, Is.EqualTo(128));
      Assert.That(s2[0], Is.EqualTo(0xBB));
      Assert.That(s2[^1], Is.EqualTo(0xBB));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = Encoding.ASCII.GetBytes("not an imd file at all");
    var d = new ImdFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "imd_bad_" + Guid.NewGuid().ToString("N"));
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
