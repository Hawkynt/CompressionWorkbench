using System.Buffers.Binary;
using System.Text;
using FileFormat.Dpx;

namespace Compression.Tests.Dpx;

[TestFixture]
public class DpxTests {

  // Minimal big-endian DPX: 2048-byte header region + 64 bytes of image data.
  // imageOffset = 2048; width=16, height=4; descriptor 50 (RGB); bit depth 8.
  private static byte[] BuildSyntheticDpx() {
    const int imageOffset = 2048;
    const int imageBytes = 64;
    var buf = new byte[imageOffset + imageBytes];

    Encoding.ASCII.GetBytes("SDPX").CopyTo(buf, 0);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), imageOffset);
    Encoding.ASCII.GetBytes("V2.0").CopyTo(buf, 8);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16, 4), (uint)buf.Length); // total size
    Encoding.ASCII.GetBytes("CompressionWorkbench").CopyTo(buf, 160);            // creator

    // Image header @768.
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(768, 2), 0); // orientation
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(770, 2), 1); // number of elements
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(772, 4), 16); // width
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(776, 4), 4);  // height
    buf[780] = 50; // descriptor RGB (element 0 @780)
    buf[803] = 8;  // bit depth

    for (var i = 0; i < imageBytes; ++i) buf[imageOffset + i] = (byte)(i * 4);
    return buf;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new DpxFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Dpx"));
    Assert.That(d.Extensions, Contains.Item(".dpx"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndPixels() {
    var img = BuildSyntheticDpx();
    var d = new DpxFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.dpx"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "pixels.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalMetadataAndPixels() {
    var img = BuildSyntheticDpx();
    var d = new DpxFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "dpx_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.dpx"));
      Assert.That(full, Is.EqualTo(img));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("endian=big"));
      Assert.That(meta, Does.Contain("width=16"));
      Assert.That(meta, Does.Contain("height=4"));
      Assert.That(meta, Does.Contain("bit_depth=8"));
      Assert.That(meta, Does.Contain("descriptor=RGB"));
      Assert.That(meta, Does.Contain("creator=CompressionWorkbench"));
      Assert.That(meta, Does.Contain("parse_status=ok"));

      var pixels = File.ReadAllBytes(Path.Combine(dir, "pixels.bin"));
      Assert.That(pixels.Length, Is.EqualTo(64));
      Assert.That(pixels[2], Is.EqualTo(8));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Boundary")]
  public void LittleEndian_MagicDetected() {
    var img = BuildSyntheticDpx();
    // Rewrite as little-endian variant.
    Encoding.ASCII.GetBytes("XPDS").CopyTo(img, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(4, 4), 2048);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(772, 4), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(776, 4), 4);
    var d = new DpxFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "dpx_le_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("endian=little"));
      Assert.That(meta, Does.Contain("width=16"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[32];
    Array.Fill(garbage, (byte)0x66);
    var d = new DpxFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "dpx_bad_" + Guid.NewGuid().ToString("N"));
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
