#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.JpegArchive;

namespace Compression.Tests.JpegArchive;

[TestFixture]
public class JpegArchiveTests {

  // Build a minimal JPEG with an APP1 EXIF segment pointing at an IFD1 thumbnail.
  private static byte[] MakeJpegWithExifThumbnail(byte[] thumbJpegBytes) {
    using var ms = new MemoryStream();
    ms.Write([0xFF, 0xD8]);                              // SOI

    // Build EXIF TIFF area: "Exif\0\0" + "II\*\0" + IFD0 offset (8) + IFD0 (0 entries + link to IFD1)
    using var tiff = new MemoryStream();
    tiff.Write("MM"u8);                                   // big-endian
    Span<byte> be2 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(be2, 0x002A);  tiff.Write(be2);
    Span<byte> be4 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(be4, 8);       tiff.Write(be4);
    // IFD0: 0 entries, then pointer to IFD1.
    BinaryPrimitives.WriteUInt16BigEndian(be2, 0);       tiff.Write(be2);
    var ifd1OffsetFieldPos = (int)tiff.Position;
    BinaryPrimitives.WriteUInt32BigEndian(be4, 0);       tiff.Write(be4);  // placeholder for IFD1 offset
    // IFD1 at current position: 2 entries (0x0201 JPEGInterchangeFormat, 0x0202 Length)
    var ifd1Pos = (int)tiff.Position;
    BinaryPrimitives.WriteUInt16BigEndian(be2, 2); tiff.Write(be2);
    // Entry 1: tag 0x0201, type LONG (4), count 1, value (4 bytes) — placeholder
    BinaryPrimitives.WriteUInt16BigEndian(be2, 0x0201); tiff.Write(be2);
    BinaryPrimitives.WriteUInt16BigEndian(be2, 4); tiff.Write(be2);
    BinaryPrimitives.WriteUInt32BigEndian(be4, 1); tiff.Write(be4);
    var thumbOffFieldPos = (int)tiff.Position;
    BinaryPrimitives.WriteUInt32BigEndian(be4, 0); tiff.Write(be4);
    // Entry 2: tag 0x0202, type LONG, count 1, value = thumbnail length
    BinaryPrimitives.WriteUInt16BigEndian(be2, 0x0202); tiff.Write(be2);
    BinaryPrimitives.WriteUInt16BigEndian(be2, 4); tiff.Write(be2);
    BinaryPrimitives.WriteUInt32BigEndian(be4, 1); tiff.Write(be4);
    BinaryPrimitives.WriteUInt32BigEndian(be4, (uint)thumbJpegBytes.Length); tiff.Write(be4);
    // IFD1 terminator (next-IFD offset = 0)
    BinaryPrimitives.WriteUInt32BigEndian(be4, 0); tiff.Write(be4);
    // Thumbnail payload starts at current TIFF-area offset
    var thumbPayloadOff = (int)tiff.Position;
    tiff.Write(thumbJpegBytes);

    // Patch IFD1 offset + thumbnail offset into the TIFF area.
    var tiffBytes = tiff.ToArray();
    BinaryPrimitives.WriteUInt32BigEndian(tiffBytes.AsSpan(ifd1OffsetFieldPos), (uint)ifd1Pos);
    BinaryPrimitives.WriteUInt32BigEndian(tiffBytes.AsSpan(thumbOffFieldPos), (uint)thumbPayloadOff);

    // Write APP1 segment: FFE1 + length + "Exif\0\0" + tiffBytes
    var app1Len = 2 + 6 + tiffBytes.Length;    // length field itself + header + payload
    ms.Write([0xFF, 0xE1]);
    Span<byte> app1LenBuf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(app1LenBuf, (ushort)app1Len);
    ms.Write(app1LenBuf);
    ms.Write([(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0]);
    ms.Write(tiffBytes);

    // Trivial SOS+EOI so the main JPEG body is "valid enough".
    ms.Write([0xFF, 0xDA, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xD9]);
    return ms.ToArray();
  }

  [Test]
  public void DescriptorSurfacesExifThumbnail() {
    var fakeThumb = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };  // SOI + EOI
    var jpeg = MakeJpegWithExifThumbnail(fakeThumb);

    using var ms = new MemoryStream(jpeg);
    var entries = new JpegArchiveDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.jpg"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata/exif.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "thumbnail_exif.jpg"), Is.True);

    using var thumbOut = new MemoryStream();
    new JpegArchiveDescriptor().ExtractEntry(new MemoryStream(jpeg), "thumbnail_exif.jpg", thumbOut, null);
    Assert.That(thumbOut.ToArray(), Is.EqualTo(fakeThumb));
  }
}
