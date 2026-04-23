using System.Buffers.Binary;
using System.Text;
using CompressionWorkbench.FileFormat.Ani;
using CompressionWorkbench.FileFormat.Ico;

namespace Compression.Tests.Ani;

[TestFixture]
public class AniTests {

  /// <summary>
  /// Builds a tiny ANI containing one CUR frame, where the CUR has one sub-image
  /// of the supplied source bytes (PNG or BMP). The reader should split out the
  /// inner sub-image with the matching <c>.png</c> / <c>.bmp</c> extension.
  /// </summary>
  private static byte[] BuildAni(byte[] curBytes, uint defaultJiffies = 6) {
    using var ms = new MemoryStream();
    // RIFF header — body size patched after we know the total chunk length.
    ms.Write("RIFF"u8); ms.Write(new byte[4]); // size placeholder
    ms.Write("ACON"u8);

    // anih chunk: 36-byte body.
    ms.Write("anih"u8);
    Span<byte> sizeBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, 36);
    ms.Write(sizeBuf);
    Span<byte> anih = stackalloc byte[36];
    BinaryPrimitives.WriteUInt32LittleEndian(anih, 36);                       // cbSize
    BinaryPrimitives.WriteUInt32LittleEndian(anih[4..], 1);                   // numFrames
    BinaryPrimitives.WriteUInt32LittleEndian(anih[8..], 1);                   // numSteps
    BinaryPrimitives.WriteUInt32LittleEndian(anih[28..], defaultJiffies);     // jiffies/step
    BinaryPrimitives.WriteUInt32LittleEndian(anih[32..], 0x01);               // ICON flag
    ms.Write(anih);

    // LIST "fram" containing one "icon" subchunk (the CUR file).
    ms.Write("LIST"u8);
    var listBodySize = (uint)(4 + 8 + curBytes.Length + (curBytes.Length & 1));
    BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, listBodySize);
    ms.Write(sizeBuf);
    ms.Write("fram"u8);
    ms.Write("icon"u8);
    BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, (uint)curBytes.Length);
    ms.Write(sizeBuf);
    ms.Write(curBytes);
    if ((curBytes.Length & 1) != 0) ms.WriteByte(0); // RIFF word alignment

    // Patch RIFF body size = total - 8.
    var total = (uint)(ms.Length - 8);
    var bytes = ms.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), total);
    return bytes;
  }

  /// <summary>Builds a valid 8x8 truecolor PNG (smallest IHDR + IDAT/IEND chunks).</summary>
  private static byte[] MinimalPng() {
    static byte[] Chunk(string type, byte[] data) {
      var buf = new byte[4 + 4 + data.Length + 4];
      BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)data.Length);
      for (var i = 0; i < 4; i++) buf[4 + i] = (byte)type[i];
      data.CopyTo(buf.AsSpan(8));
      return buf;
    }
    var ihdr = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), 8);
    BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), 8);
    ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
    using var ms = new MemoryStream();
    ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    ms.Write(Chunk("IHDR", ihdr));
    ms.Write(Chunk("IDAT", new byte[] { 0x78, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 }));
    ms.Write(Chunk("IEND", []));
    return ms.ToArray();
  }

  /// <summary>Builds a 16x16 32-bit BMP file (BITMAPFILEHEADER + BITMAPINFOHEADER + pixels).</summary>
  private static byte[] MinimalBmp() {
    const int fileHeader = 14, infoHeader = 40, pixelBytes = 16 * 16 * 4;
    var fileLen = fileHeader + infoHeader + pixelBytes;
    var data = new byte[fileLen];
    data[0] = (byte)'B'; data[1] = (byte)'M';
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2, 4), (uint)fileLen);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10, 4), fileHeader + infoHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14, 4), infoHeader);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18, 4), 16);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22, 4), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28, 2), 32);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(34, 4), pixelBytes);
    for (var i = fileHeader + infoHeader; i < fileLen; i++) data[i] = 0xCC;
    return data;
  }

  [Test, Category("HappyPath")]
  public void Read_ParsesAnih() {
    var cur = IcoWriter.BuildCur([new IcoWriter.Image(MinimalPng())]);
    var ani = AniReader.Read(BuildAni(cur, defaultJiffies: 12));
    Assert.That(ani.Header.NumFrames, Is.EqualTo(1u));
    Assert.That(ani.Header.NumSteps, Is.EqualTo(1u));
    Assert.That(ani.Header.DefaultJiffiesPerStep, Is.EqualTo(12u));
    Assert.That(ani.Frames, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_PngFrame_KeepsPngExtension() {
    var png = MinimalPng();
    var cur = IcoWriter.BuildCur([new IcoWriter.Image(png)]);
    var ani = BuildAni(cur);

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(ani);
      new AniFormatDescriptor().Extract(ms, tmp, null, null);
      var pngs = Directory.GetFiles(tmp, "*.png", SearchOption.AllDirectories);
      var bmps = Directory.GetFiles(tmp, "*.bmp", SearchOption.AllDirectories);
      Assert.That(pngs, Has.Length.EqualTo(1), "PNG sub-image should extract with .png extension");
      Assert.That(bmps, Has.Length.EqualTo(0), "no BMP entries expected when source is pure PNG");
      // Verify the extracted PNG bytes are the original verbatim — no re-encoding.
      Assert.That(File.ReadAllBytes(pngs[0]), Is.EqualTo(png).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_BmpFrame_KeepsBmpExtension() {
    var bmp = MinimalBmp();
    var cur = IcoWriter.BuildCur([new IcoWriter.Image(bmp)]);
    var ani = BuildAni(cur);

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(ani);
      new AniFormatDescriptor().Extract(ms, tmp, null, null);
      var pngs = Directory.GetFiles(tmp, "*.png", SearchOption.AllDirectories);
      var bmps = Directory.GetFiles(tmp, "*.bmp", SearchOption.AllDirectories);
      Assert.That(bmps, Has.Length.EqualTo(1), "BMP sub-image should extract with .bmp extension");
      Assert.That(pngs, Has.Length.EqualTo(0), "no PNG entries expected when source is pure BMP");
      // The extracted BMP should be a valid BMP (BM signature reconstructed by IcoReader).
      var extracted = File.ReadAllBytes(bmps[0]);
      Assert.That(extracted[0], Is.EqualTo((byte)'B'));
      Assert.That(extracted[1], Is.EqualTo((byte)'M'));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void List_MixedSubImages_ReportsBothExtensions() {
    // One frame's CUR holding both a PNG and a BMP sub-image — verifies the
    // extension-per-entry decision is made independently.
    var cur = IcoWriter.BuildCur([
      new IcoWriter.Image(MinimalPng()),
      new IcoWriter.Image(MinimalBmp()),
    ]);
    var ani = BuildAni(cur);

    using var ms = new MemoryStream(ani);
    var entries = new AniFormatDescriptor().List(ms, null);
    var imageEntries = entries.Where(e => !e.Name.EndsWith("metadata.ini")).ToList();
    Assert.That(imageEntries.Count(e => e.Name.EndsWith(".png")), Is.EqualTo(1));
    Assert.That(imageEntries.Count(e => e.Name.EndsWith(".bmp")), Is.EqualTo(1));
    // Method labels track the on-disk encoding.
    Assert.That(imageEntries.Where(e => e.Name.EndsWith(".png")).Select(e => e.Method), Has.All.EqualTo("png"));
    Assert.That(imageEntries.Where(e => e.Name.EndsWith(".bmp")).Select(e => e.Method), Has.All.EqualTo("dib"));
  }

  [Test, Category("EdgeCase")]
  public void Read_NotRiff_Throws() {
    Assert.That(() => AniReader.Read(new byte[12]), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_RiffButNotAcon_Throws() {
    var buf = new byte[12];
    "RIFF"u8.CopyTo(buf);
    "WAVE"u8.CopyTo(buf.AsSpan(8));
    Assert.That(() => AniReader.Read(buf), Throws.InstanceOf<InvalidDataException>());
  }
}
