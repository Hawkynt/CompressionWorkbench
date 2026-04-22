#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Dng;

namespace Compression.Tests.Dng;

[TestFixture]
public class DngTests {

  // Build a synthetic little-endian DNG: header + IFD0 (with SubIFDs pointing to one
  // sub-IFD carrying an embedded JPEG) + embedded JPEG bytes + sub-IFD + strip data.
  private static byte[] MakeMinimalDng(out int expectedJpegOff, out int expectedJpegLen) {
    var stream = new List<byte>();

    // --- Placeholder offsets, patched later ---
    // Layout plan:
    //   0..7   : TIFF header (II, 42, IFD0 offset = 8)
    //   8..    : IFD0 (we'll compute size)
    //   ...    : SubIFD array (1 entry × 4 bytes)
    //   ...    : JPEG payload
    //   ...    : SubIFD body
    //   ...    : strip data referenced by SubIFD

    // Header
    stream.AddRange([(byte)'I', (byte)'I']);
    AppendUInt16LE(stream, 42);
    var ifd0OffsetPos = stream.Count;
    AppendUInt32LE(stream, 0);          // IFD0 offset — patch

    var ifd0Start = stream.Count;

    // IFD0 entries. We'll put SubIFDs tag (0x014A) pointing at a SubIFD array.
    // For IFD0 simplicity: no strip data; just SubIFDs + DNGVersion.
    // entry_count = 2
    AppendUInt16LE(stream, 2);
    // Entry 1: DNGVersion (0xC612), BYTE(1), count=4, value="1.4.0.0" inline as 01 04 00 00
    AppendUInt16LE(stream, 0xC612);
    AppendUInt16LE(stream, 1);          // BYTE
    AppendUInt32LE(stream, 4);          // count
    stream.AddRange([1, 4, 0, 0]);

    // Entry 2: SubIFDs (0x014A), LONG(4), count=1, value = offset to SubIFD (4 bytes, inline when count=1)
    AppendUInt16LE(stream, 0x014A);
    AppendUInt16LE(stream, 4);          // LONG
    AppendUInt32LE(stream, 1);
    var subIfdPointerPos = stream.Count;
    AppendUInt32LE(stream, 0);          // offset to SubIFD — patch

    // IFD0 next-IFD offset = 0
    AppendUInt32LE(stream, 0);

    // --- JPEG payload ---
    var jpegOffset = stream.Count;
    var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 2, 3, 0xFF, 0xD9 };
    stream.AddRange(jpegBytes);

    // --- SubIFD ---
    var subIfdStart = stream.Count;
    AppendUInt16LE(stream, 2); // entry_count
    // JpegInterchangeFormat (0x0201), LONG(4), count=1, value=jpegOffset
    AppendUInt16LE(stream, 0x0201);
    AppendUInt16LE(stream, 4);
    AppendUInt32LE(stream, 1);
    AppendUInt32LE(stream, (uint)jpegOffset);
    // JpegInterchangeFormatLength (0x0202), LONG(4), count=1, value=jpegBytes.Length
    AppendUInt16LE(stream, 0x0202);
    AppendUInt16LE(stream, 4);
    AppendUInt32LE(stream, 1);
    AppendUInt32LE(stream, (uint)jpegBytes.Length);
    AppendUInt32LE(stream, 0); // next IFD

    // --- Patches ---
    var arr = stream.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(arr.AsSpan(ifd0OffsetPos), (uint)ifd0Start);
    BinaryPrimitives.WriteUInt32LittleEndian(arr.AsSpan(subIfdPointerPos), (uint)subIfdStart);

    expectedJpegOff = jpegOffset;
    expectedJpegLen = jpegBytes.Length;
    return arr;
  }

  private static void AppendUInt16LE(List<byte> dst, ushort v) {
    dst.Add((byte)v);
    dst.Add((byte)(v >> 8));
  }

  private static void AppendUInt32LE(List<byte> dst, uint v) {
    dst.Add((byte)v);
    dst.Add((byte)(v >> 8));
    dst.Add((byte)(v >> 16));
    dst.Add((byte)(v >> 24));
  }

  [Test]
  public void Reader_DetectsDngVersion_And_SubIfd() {
    var data = MakeMinimalDng(out _, out _);
    var r = new DngReader(data);
    Assert.Multiple(() => {
      Assert.That(r.IsBigEndian, Is.False);
      Assert.That(r.TopLevelIfds, Has.Count.EqualTo(1));
      Assert.That(r.SubIfds, Has.Count.EqualTo(1));
      Assert.That(r.DngVersionLength, Is.EqualTo(4));
      Assert.That(DngReader.IsJpegPreviewIfd(r.SubIfds[0]), Is.True);
    });
  }

  [Test]
  public void Reader_ExtractsEmbeddedJpeg() {
    var data = MakeMinimalDng(out _, out var jpegLen);
    var r = new DngReader(data);
    var jpeg = r.ReadEmbeddedJpeg(r.SubIfds[0]);
    Assert.That(jpeg, Has.Length.EqualTo(jpegLen));
    Assert.That(jpeg[0], Is.EqualTo(0xFF));
    Assert.That(jpeg[1], Is.EqualTo(0xD8));
    Assert.That(jpeg[jpeg.Length - 1], Is.EqualTo(0xD9));
  }

  [Test]
  public void Descriptor_List_HasFullAndPreview() {
    var data = MakeMinimalDng(out _, out _);
    using var ms = new MemoryStream(data);
    var entries = new DngFormatDescriptor().List(ms, null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "FULL.dng"), Is.True);
      Assert.That(entries.Any(e => e.Name == "preview_00.jpg"), Is.True);
    });
  }

  [Test]
  public void Descriptor_Extract_WritesFiles() {
    var data = MakeMinimalDng(out _, out var jpegLen);
    var dir = Path.Combine(Path.GetTempPath(), "dng_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(data);
      new DngFormatDescriptor().Extract(ms, dir, null, null);
      var previewPath = Path.Combine(dir, "preview_00.jpg");
      Assert.Multiple(() => {
        Assert.That(File.Exists(Path.Combine(dir, "FULL.dng")), Is.True);
        Assert.That(File.Exists(previewPath), Is.True);
      });
      Assert.That(new FileInfo(previewPath).Length, Is.EqualTo(jpegLen));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void BadMagic_Throws() {
    var bad = new byte[] { (byte)'I', (byte)'I', 0x00, 0x00, 0, 0, 0, 0 };
    Assert.Throws<InvalidDataException>(() => _ = new DngReader(bad));
  }
}
