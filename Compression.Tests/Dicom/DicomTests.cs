#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Dicom;

namespace Compression.Tests.Dicom;

[TestFixture]
public class DicomTests {

  /// <summary>
  /// Builds a minimal DICOM Part-10 file: 128-byte preamble + DICM magic +
  /// Explicit-VR file meta (TransferSyntaxUID = Explicit VR LE) + body with
  /// Modality, Rows, Columns, BitsAllocated, and a short PixelData (OW).
  /// </summary>
  private static byte[] MakeMinimalDicom() {
    using var ms = new MemoryStream();
    ms.Write(new byte[128]);                       // preamble
    ms.Write("DICM"u8);                            // magic

    // File meta: Group 0x0002 Explicit VR LE.
    // (0002,0010) UI TransferSyntaxUID = "1.2.840.10008.1.2.1" (Explicit VR LE)
    WriteExplicit(ms, 0x0002, 0x0010, "UI", "1.2.840.10008.1.2.1\0"u8.ToArray()); // pad to even length

    // Body under Explicit VR LE.
    // (0008,0060) CS Modality = "CT"
    WriteExplicit(ms, 0x0008, 0x0060, "CS", "CT"u8.ToArray());
    // (0010,0010) PN PatientName = "TEST^PATIENT"
    WriteExplicit(ms, 0x0010, 0x0010, "PN", "TEST^PATIENT"u8.ToArray());
    // (0028,0010) US Rows = 4
    WriteExplicit(ms, 0x0028, 0x0010, "US", Us(4));
    // (0028,0011) US Columns = 8
    WriteExplicit(ms, 0x0028, 0x0011, "US", Us(8));
    // (0028,0100) US BitsAllocated = 8
    WriteExplicit(ms, 0x0028, 0x0100, "US", Us(8));
    // (6000,3000) OW OverlayData
    WriteExplicit(ms, 0x6000, 0x3000, "OW", [0x11, 0x22, 0x33, 0x44]);
    // (7FE0,0010) OW PixelData = 4*8 = 32 bytes
    var pixels = new byte[32];
    for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)i;
    WriteExplicit(ms, 0x7FE0, 0x0010, "OW", pixels);

    return ms.ToArray();
  }

  private static byte[] Us(ushort v) {
    var b = new byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(b, v);
    return b;
  }

  private static void WriteExplicit(Stream ms, ushort group, ushort element, string vr, byte[] value) {
    Span<byte> header = stackalloc byte[8];
    BinaryPrimitives.WriteUInt16LittleEndian(header[..2], group);
    BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(2, 2), element);
    header[4] = (byte)vr[0]; header[5] = (byte)vr[1];

    if (vr is "OB" or "OW" or "OF" or "OD" or "OL" or "SQ" or "UN" or "UT" or "UC" or "UR") {
      ms.Write(header[..6]);
      ms.Write(new byte[] { 0, 0 });                                // reserved
      Span<byte> len = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)value.Length);
      ms.Write(len);
    } else {
      BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6, 2), (ushort)value.Length);
      ms.Write(header);
    }
    ms.Write(value);
  }

  [Test]
  public void MinimalDicom_ListsCoreEntries() {
    var data = MakeMinimalDicom();
    using var ms = new MemoryStream(data);
    var entries = new DicomFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.dcm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "tags.txt"), Is.True);
    Assert.That(entries.Any(e => e.Name == "pixel_data/pixel_data.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "overlay_data/overlay_6000.bin"), Is.True);
  }

  [Test]
  public void Extract_WritesPixelDataAndMetadata() {
    var data = MakeMinimalDicom();
    var tmp = Path.Combine(Path.GetTempPath(), "dicom_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new DicomFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.dcm")), Is.True);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("transfer_syntax=1.2.840.10008.1.2.1"));
      Assert.That(ini, Does.Contain("modality=CT"));
      Assert.That(ini, Does.Contain("rows=4"));
      Assert.That(ini, Does.Contain("columns=8"));
      Assert.That(ini, Does.Contain("bits_allocated=8"));
      var px = File.ReadAllBytes(Path.Combine(tmp, "pixel_data", "pixel_data.bin"));
      Assert.That(px.Length, Is.EqualTo(32));
      Assert.That(px[0], Is.EqualTo(0));
      Assert.That(px[31], Is.EqualTo(31));
      var tags = File.ReadAllText(Path.Combine(tmp, "tags.txt"));
      Assert.That(tags, Does.Contain("(0008,0060)"));
      Assert.That(tags, Does.Contain("CT"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }
}
