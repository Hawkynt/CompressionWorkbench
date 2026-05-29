#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Gif;

namespace Compression.Tests.Gif;

[TestFixture]
public class GifLayoutMapTests {

  /// <summary>
  /// Builds a minimal GIF89a with one or more 1x1 frames.
  /// </summary>
  private static MemoryStream BuildTestGif(int frameCount = 1, bool includeGce = true) {
    var ms = new MemoryStream();

    // Header: "GIF89a"
    ms.Write("GIF89a"u8);

    // Logical Screen Descriptor (7 bytes)
    ms.Write(new byte[] { 0x01, 0x00 }); // width = 1
    ms.Write(new byte[] { 0x01, 0x00 }); // height = 1
    ms.WriteByte(0x80); // packed: GCT flag set, 2-color table
    ms.WriteByte(0x00); // background color index
    ms.WriteByte(0x00); // pixel aspect ratio

    // Global Color Table (2 entries * 3 bytes = 6 bytes)
    ms.Write(new byte[] { 0x00, 0x00, 0x00 }); // black
    ms.Write(new byte[] { 0xFF, 0xFF, 0xFF }); // white

    for (var i = 0; i < frameCount; i++) {
      if (includeGce) {
        // Graphic Control Extension
        ms.WriteByte(0x21); // extension introducer
        ms.WriteByte(0xF9); // GCE label
        ms.WriteByte(0x04); // block size
        ms.Write(new byte[] { 0x00, 0x0A, 0x00, 0x00 }); // delay=10, no transparent
        ms.WriteByte(0x00); // block terminator
      }

      // Image Descriptor
      ms.WriteByte(0x2C); // image separator
      ms.Write(new byte[] { 0x00, 0x00 }); // left = 0
      ms.Write(new byte[] { 0x00, 0x00 }); // top = 0
      ms.Write(new byte[] { 0x01, 0x00 }); // width = 1
      ms.Write(new byte[] { 0x01, 0x00 }); // height = 1
      ms.WriteByte(0x00); // packed: no local CT

      // LZW minimum code size
      ms.WriteByte(0x02);
      // LZW data: minimal compressed data for 1 pixel
      ms.WriteByte(0x02); // sub-block size = 2
      ms.Write(new byte[] { 0x4C, 0x01 }); // compressed pixel data
      ms.WriteByte(0x00); // block terminator
    }

    // Trailer
    ms.WriteByte(0x3B);

    ms.Position = 0;
    return ms;
  }

  [Test]
  public void EnumerateChunks_HasHeader() {
    using var ms = BuildTestGif();
    var chunks = GifLayoutMap.Enumerate(ms).ToList();

    var header = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("header"));
    Assert.That(header, Is.Not.Null);
    Assert.That(header!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    Assert.That(header.Offset, Is.EqualTo(0));
    // 6 (header) + 7 (LSD) + 6 (GCT 2 entries) = 19
    Assert.That(header.Length, Is.EqualTo(19));
  }

  [Test]
  public void EnumerateChunks_HasFrames() {
    using var ms = BuildTestGif(frameCount: 3);
    var chunks = GifLayoutMap.Enumerate(ms).ToList();

    var frames = chunks.Where(c => c.Kind == DefragBlockKind.Used).ToList();
    Assert.That(frames, Has.Count.EqualTo(3));
    for (var i = 0; i < frames.Count; i++)
      Assert.That(frames[i].FileName, Does.Contain($"Frame {i}"));
  }

  [Test]
  public void EnumerateChunks_HasExtensions() {
    using var ms = BuildTestGif(frameCount: 1, includeGce: true);
    var chunks = GifLayoutMap.Enumerate(ms).ToList();

    var exts = chunks.Where(c => c.FileName != null && c.FileName.Contains("Extension")).ToList();
    Assert.That(exts, Has.Count.EqualTo(1));
    Assert.That(exts[0].Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateChunks_HasTrailer() {
    using var ms = BuildTestGif();
    var chunks = GifLayoutMap.Enumerate(ms).ToList();

    var trailer = chunks.LastOrDefault(c => c.FileName != null && c.FileName.Contains("Trailer"));
    Assert.That(trailer, Is.Not.Null);
    Assert.That(trailer!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    Assert.That(trailer.Length, Is.EqualTo(1));
    Assert.That(trailer.Offset, Is.EqualTo(ms.Length - 1));
  }

  [Test]
  public void EnumerateChunks_CoversFullFile() {
    using var ms = BuildTestGif(frameCount: 2);
    var chunks = GifLayoutMap.Enumerate(ms).ToList();
    var total = chunks.Sum(c => c.Length);
    Assert.That(total, Is.EqualTo(ms.Length));
  }

  [Test]
  public void EnumerateChunks_Contiguous() {
    using var ms = BuildTestGif(frameCount: 2);
    var chunks = GifLayoutMap.Enumerate(ms).OrderBy(c => c.Offset).ToList();
    for (var i = 1; i < chunks.Count; i++)
      Assert.That(chunks[i].Offset, Is.EqualTo(chunks[i - 1].Offset + chunks[i - 1].Length),
        $"Gap between '{chunks[i - 1].FileName}' and '{chunks[i].FileName}'");
  }

  [Test]
  public void EnumerateChunks_EmptyStream_ReturnsNothing() {
    using var ms = new MemoryStream();
    var chunks = GifLayoutMap.Enumerate(ms).ToList();
    Assert.That(chunks, Is.Empty);
  }
}
