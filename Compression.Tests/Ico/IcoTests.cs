using System.Buffers.Binary;
using CompressionWorkbench.FileFormat.Ico;

namespace Compression.Tests.Ico;

[TestFixture]
public class IcoTests {

  // 16x16 PNG constructed by hand — IHDR + single IDAT (empty zlib stream with adler) +
  // IEND. Small but parser-valid enough that the header read succeeds; rendering is
  // out of scope for the ICO tests.
  private static byte[] MinimalPng(int width, int height) {
    static byte[] Chunk(string type, byte[] data) {
      var buf = new byte[4 + 4 + data.Length + 4];
      BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)data.Length);
      for (var i = 0; i < 4; i++) buf[4 + i] = (byte)type[i];
      data.CopyTo(buf.AsSpan(8));
      // Zero-crc is a valid stop-gap: our reader does not verify PNG CRCs.
      return buf;
    }

    var ihdr = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
    BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
    ihdr[8] = 8;  // bit depth
    ihdr[9] = 6;  // colour type (RGBA)
    ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;

    var sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    using var ms = new MemoryStream();
    ms.Write(sig);
    ms.Write(Chunk("IHDR", ihdr));
    ms.Write(Chunk("IDAT", [0x78, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01])); // trivial deflate
    ms.Write(Chunk("IEND", []));
    return ms.ToArray();
  }

  // 16x16 BMP: BITMAPFILEHEADER + BITMAPINFOHEADER + 16×16×4 = 1024 bytes BGRA.
  private static byte[] MinimalBmp(int width, int height) {
    const int fileHeader = 14, infoHeader = 40;
    var rowBytes = ((width * 32 + 31) / 32) * 4;
    var pixelBytes = rowBytes * height;
    var fileLen = fileHeader + infoHeader + pixelBytes;
    var data = new byte[fileLen];
    data[0] = (byte)'B'; data[1] = (byte)'M';
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2, 4), (uint)fileLen);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10, 4), (uint)(fileHeader + infoHeader));
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14, 4), infoHeader);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18, 4), width);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22, 4), height);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26, 2), 1);   // planes
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28, 2), 32);  // bits per pixel
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(30, 4), 0);   // biCompression = BI_RGB
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(34, 4), (uint)pixelBytes);
    // 18x18 pixels of 0xFF — fully-opaque white, easy to eyeball if debugging.
    for (var i = 0; i < pixelBytes; i++) data[fileHeader + infoHeader + i] = 0xFF;
    return data;
  }

  [Test, Category("HappyPath")]
  public void Detect_PngEmbeddedIco() {
    var png = MinimalPng(16, 16);
    var ico = IcoWriter.BuildIco([new IcoWriter.Image(png)]);
    Assert.That(ico[0], Is.EqualTo(0));
    Assert.That(ico[1], Is.EqualTo(0));
    Assert.That(ico[2], Is.EqualTo(1));  // ICO type
    Assert.That(ico[4], Is.EqualTo(1));  // count
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Write_Read_PngEntry_RoundTrips() {
    var png = MinimalPng(32, 32);
    var ico = IcoWriter.BuildIco([new IcoWriter.Image(png)]);

    var bundle = IcoReader.Read(ico);
    Assert.That(bundle.Entries, Has.Count.EqualTo(1));
    var entry = bundle.Entries[0];
    Assert.That(entry.IsPng, Is.True);
    Assert.That(entry.Width, Is.EqualTo(32));
    Assert.That(entry.Height, Is.EqualTo(32));
    Assert.That(entry.Data, Is.EqualTo(png).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Write_Read_BmpEntry_ProducesBmpOutputWithCorrectHeight() {
    var bmp = MinimalBmp(16, 16);
    var ico = IcoWriter.BuildIco([new IcoWriter.Image(bmp)]);

    var bundle = IcoReader.Read(ico);
    Assert.That(bundle.Entries, Has.Count.EqualTo(1));
    var entry = bundle.Entries[0];
    Assert.That(entry.IsPng, Is.False);
    Assert.That(entry.Width, Is.EqualTo(16));
    Assert.That(entry.Height, Is.EqualTo(16));
    // The extracted BMP must start with 'BM' and its BITMAPINFOHEADER.biHeight must be
    // the real pixel height (16), not the icon convention's 32.
    Assert.That(entry.Data[0], Is.EqualTo((byte)'B'));
    Assert.That(entry.Data[1], Is.EqualTo((byte)'M'));
    var biHeight = BinaryPrimitives.ReadInt32LittleEndian(entry.Data.AsSpan(14 + 8, 4));
    Assert.That(biHeight, Is.EqualTo(16));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReportsEntryCount() {
    var png = MinimalPng(16, 16);
    var ico = IcoWriter.BuildIco([new IcoWriter.Image(png), new IcoWriter.Image(png)]);

    using var ms = new MemoryStream(ico);
    var desc = new IcoFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Does.EndWith(".png"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesFilesToDisk() {
    var png = MinimalPng(24, 24);
    var ico = IcoWriter.BuildIco([new IcoWriter.Image(png)]);

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(ico);
      new IcoFormatDescriptor().Extract(ms, tmp, null, null);
      var pngs = Directory.GetFiles(tmp, "*.png");
      Assert.That(pngs, Has.Length.EqualTo(1));
      Assert.That(File.ReadAllBytes(pngs[0]), Is.EqualTo(png).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughReader() {
    var png = MinimalPng(16, 16);
    var tmpIn = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
    File.WriteAllBytes(tmpIn, png);
    try {
      var desc = new IcoFormatDescriptor();
      using var outMs = new MemoryStream();
      desc.Create(outMs, [new Compression.Registry.ArchiveInputInfo(tmpIn, Path.GetFileName(tmpIn), false)], new Compression.Registry.FormatCreateOptions());
      outMs.Position = 0;
      var listed = desc.List(outMs, null);
      Assert.That(listed, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmpIn);
    }
  }

  [Test, Category("EdgeCase")]
  public void CanAccept_RejectsNonImageExtensions() {
    var desc = new IcoFormatDescriptor();
    var ok = desc.CanAccept(new Compression.Registry.ArchiveInputInfo("/tmp/foo.txt", "foo.txt", false), out var reason);
    Assert.That(ok, Is.False);
    Assert.That(reason, Is.Not.Null.And.Not.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Reader_TruncatedHeader_Throws() {
    Assert.That(() => IcoReader.Read([0, 0, 1]), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Reader_UnknownType_Throws() {
    // type=99 (neither ICO=1 nor CUR=2)
    byte[] data = [0, 0, 99, 0, 1, 0, /* pad for directory */ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    Assert.That(() => IcoReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }
}
