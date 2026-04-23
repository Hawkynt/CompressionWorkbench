using System.Buffers.Binary;
using FileFormat.Mz;

namespace Compression.Tests.Mz;

[TestFixture]
public class MzTests {

  // Build a minimal MZ image: 32-byte header + 16-byte body (= 2 blocks of 32 bytes),
  // optionally followed by trailing overlay bytes.
  private static byte[] BuildMz(int bodySize = 16, byte[]? overlay = null) {
    const int headerSize = 32;                 // 2 paragraphs (2×16)
    var imageSize = headerSize + bodySize;      // everything prior to overlay
    var overlayBytes = overlay ?? [];
    var total = imageSize + overlayBytes.Length;
    var buf = new byte[total];

    buf[0] = (byte)'M';
    buf[1] = (byte)'Z';
    var bytesInLast = (ushort)(imageSize % 512 == 0 ? 0 : imageSize % 512);
    var blocks = (ushort)((imageSize + 511) / 512);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), bytesInLast);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), blocks);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), 0);                    // no relocs
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)(headerSize / 16)); // header paragraphs
    // cs:ip / ss:sp left zero
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x18), 0x001C);             // reloc_table_offset
    // Fill body with 0xAA for easy verification.
    for (var i = 0; i < bodySize; i++) buf[headerSize + i] = 0xAA;
    // Overlay copy.
    overlayBytes.CopyTo(buf.AsSpan(imageSize));
    return buf;
  }

  [Test, Category("HappyPath")]
  public void Read_SplitsIntoHeaderAndBody_NoOverlay() {
    var data = BuildMz(bodySize: 32);
    var img = MzReader.Read(data);
    Assert.That(img.Header, Has.Length.EqualTo(32));
    Assert.That(img.Body, Has.Length.EqualTo(32));
    Assert.That(img.Overlay, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Read_SeparatesOverlayBytes() {
    var overlay = new byte[64];
    for (var i = 0; i < overlay.Length; i++) overlay[i] = (byte)i;
    var data = BuildMz(bodySize: 16, overlay: overlay);
    var img = MzReader.Read(data);
    Assert.That(img.Overlay, Has.Length.EqualTo(64));
    Assert.That(img.Overlay[0], Is.EqualTo(0));
    Assert.That(img.Overlay[63], Is.EqualTo(63));
  }

  [Test, Category("HappyPath")]
  public void Read_DetectsPeExtendedSignature() {
    // Build an MZ header large enough to contain e_lfanew at 0x3C.
    var buf = new byte[256];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), 8);   // 128-byte header
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C), 128); // e_lfanew -> offset 128
    buf[128] = (byte)'P'; buf[129] = (byte)'E'; buf[130] = 0; buf[131] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 1); // 1 block
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)(256 % 512)); // bytes in last

    var img = MzReader.Read(buf);
    Assert.That(img.ExtendedSignature, Is.EqualTo("PE"));
    Assert.That(img.ExtendedHeaderOffset, Is.EqualTo(128u));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_EmitsExpectedEntries() {
    var overlay = new byte[20];
    Array.Fill<byte>(overlay, 0x42);
    var data = BuildMz(bodySize: 16, overlay: overlay);

    using var ms = new MemoryStream(data);
    var entries = new MzFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("header.bin"));
    Assert.That(names, Does.Contain("body.bin"));
    Assert.That(names, Does.Contain("overlay.bin"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesOverlayToDisk() {
    var overlay = new byte[32];
    Array.Fill<byte>(overlay, 0x5A);
    var data = BuildMz(bodySize: 16, overlay: overlay);

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new MzFormatDescriptor().Extract(ms, tmp, null, null);
      var overlayFile = Path.Combine(tmp, "overlay.bin");
      Assert.That(File.Exists(overlayFile), Is.True);
      Assert.That(new FileInfo(overlayFile).Length, Is.EqualTo(32));
      Assert.That(File.ReadAllBytes(overlayFile), Is.EqualTo(overlay).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Read_NonMzMagic_Throws() {
    var data = new byte[64];
    data[0] = (byte)'X'; data[1] = (byte)'Y';
    Assert.That(() => MzReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_TruncatedHeader_Throws() {
    var data = new byte[10];
    data[0] = (byte)'M'; data[1] = (byte)'Z';
    Assert.That(() => MzReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }
}
